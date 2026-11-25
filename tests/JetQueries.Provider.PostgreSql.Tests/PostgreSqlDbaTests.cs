using System.Globalization;
using JetQueries.Sdk;

namespace JetQueries.Provider.PostgreSql.Tests;

/// <summary>The DBA screens' reads on a real PostgreSQL (opt-in: JETQUERIES_POSTGRES_TESTS=Y).</summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlDbaTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_monitor_sees_who_blocks_whom_and_ends_the_blocker()
    {
        await using var holder = await OpenAsync();
        await using var waiter = await OpenAsync();
        await using var monitor = await OpenAsync();
        var activity = Assert.IsAssignableFrom<IActivityMonitor>(monitor);
        await RunAsync(holder, "DROP TABLE IF EXISTS jq_activity; CREATE TABLE jq_activity (id int PRIMARY KEY, n int); INSERT INTO jq_activity VALUES (1, 0);");
        await RunAsync(holder, "BEGIN; UPDATE jq_activity SET n = 1 WHERE id = 1;");
        var holderId = await PidAsync(holder);
        var waiterId = await PidAsync(waiter);
        var waiting = RunAsync(waiter, "UPDATE jq_activity SET n = 2 WHERE id = 1");

        ActivitySession? blocked = null;
        ActivitySnapshot? snapshot = null;
        for (var i = 0; i < 50 && blocked is null; i++)
        {
            await Task.Delay(100, Token);
            snapshot = await activity.GetActivityAsync(Token);
            blocked = snapshot.Sessions.FirstOrDefault(s => s.Id == waiterId && s.BlockedBy is not null);
        }

        Assert.NotNull(blocked);
        Assert.Equal(holderId, blocked.BlockedBy);
        Assert.StartsWith("Lock:", blocked.WaitType, StringComparison.Ordinal);
        Assert.Equal("UPDATE", blocked.Command);
        Assert.Contains("jq_activity", blocked.Sql, StringComparison.Ordinal);
        Assert.Null(snapshot!.Limitation);
        Assert.Equal(await PidAsync(monitor), snapshot.OwnSessionId);
        var head = snapshot.Sessions.Single(s => s.Id == holderId);
        Assert.Equal(1, head.OpenTransactions);
        Assert.StartsWith("idle in transaction", head.Status, StringComparison.Ordinal);

        await activity.KillSessionAsync(holderId, Token);
        await waiting.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await Assert.ThrowsAsync<ArgumentException>(() => activity.KillSessionAsync("1; DROP TABLE x", Token));
    }

    [Fact]
    public async Task Heavy_queries_scans_index_use_and_fragmentation_are_read()
    {
        await using var connection = await OpenAsync();
        var advisor = Assert.IsAssignableFrom<IPerformanceAdvisor>(connection);
        await RunAsync(connection, "CREATE EXTENSION IF NOT EXISTS pg_stat_statements; CREATE EXTENSION IF NOT EXISTS pgstattuple;");
        await RunAsync(connection, """
            DROP TABLE IF EXISTS jq_orders;
            CREATE TABLE jq_orders (id serial PRIMARY KEY, customer_id int NOT NULL, status int NOT NULL, total numeric(12, 2) NOT NULL, code uuid NOT NULL DEFAULT gen_random_uuid());
            INSERT INTO jq_orders (customer_id, status, total) SELECT g % 500, g % 5, g % 10000 FROM generate_series(1, 20000) g;
            CREATE INDEX jq_orders_customer ON jq_orders (customer_id);
            CREATE INDEX jq_orders_customer_total ON jq_orders (customer_id) INCLUDE (total);
            CREATE INDEX jq_orders_status ON jq_orders (status);
            CREATE INDEX jq_orders_code ON jq_orders (code);
            ANALYZE jq_orders;
            """);
        for (var i = 0; i < 4; i++)
        {
            await RunAsync(connection, "SELECT id FROM jq_orders WHERE total = 1234.00");
            await RunAsync(connection, "SELECT customer_id FROM jq_orders WHERE customer_id = 42");
        }

        await RunAsync(connection, "SELECT pg_stat_force_next_flush()");
        await RunAsync(connection, "INSERT INTO jq_orders (customer_id, status, total) SELECT g % 500, g % 5, 1 FROM generate_series(1, 20000) g");
        await RunAsync(connection, "SELECT pg_stat_force_next_flush()");

        var database = (await ((IDatabaseContext)connection).GetCurrentAsync(Token))!;
        var heavy = await advisor.GetHeavyQueriesAsync(new HeavyQueryRequest(HeavyQueryOrder.Executions, 50, database), Token);
        var total = Assert.Single(heavy.Queries, static q => q.Sql.Contains("WHERE total =", StringComparison.Ordinal));
        Assert.True(total.Executions >= 4);
        Assert.Equal(database, total.Database);
        Assert.NotNull(heavy.CountingSince);
        Assert.DoesNotContain(heavy.Queries, static q => q.Sql.Contains("JetQueries:advisor", StringComparison.Ordinal));

        var scans = await advisor.GetMissingIndexesAsync(database, Token);
        var orders = Assert.Single(scans, static s => s.Table == "public.jq_orders");
        Assert.True(orders.Uses >= 4);
        Assert.Contains("CREATE INDEX CONCURRENTLY ON \"public\".\"jq_orders\"", orders.CreateScript, StringComparison.Ordinal);

        var usage = await advisor.GetIndexUsageAsync(database, Token);
        var primary = Assert.Single(usage.Indexes, static i => i.Name == "jq_orders_pkey");
        Assert.True(primary.IsConstraint);
        Assert.Null(primary.DropScript);
        var customer = Assert.Single(usage.Indexes, static i => i.Name == "jq_orders_customer");
        Assert.Equal(["customer_id"], customer.KeyColumns);
        Assert.Equal("DROP INDEX CONCURRENTLY \"public\".\"jq_orders_customer\";", customer.DropScript);
        Assert.Equal(["total"], Assert.Single(usage.Indexes, static i => i.Name == "jq_orders_customer_total").IncludedColumns);
        Assert.Equal(0, usage.Indexes.Single(static i => i.Name == "jq_orders_status").Reads);
        Assert.True(customer.SizeKb > 0);

        var fragmentation = await advisor.GetFragmentationAsync(database, 1, Token);
        var code = Assert.Single(fragmentation, static f => f.Name == "jq_orders_code");
        Assert.True(code.FragmentationPercent >= 30, code.FragmentationPercent.ToString(CultureInfo.InvariantCulture));
        Assert.Equal("REINDEX INDEX CONCURRENTLY \"public\".\"jq_orders_code\";", code.MaintenanceScript);
    }

    [Fact]
    public async Task The_dashboard_the_health_check_and_another_databases_detail_are_read()
    {
        await using var connection = await OpenAsync();
        var dashboard = Assert.IsAssignableFrom<IServerDashboard>(connection);
        var overview = await dashboard.GetOverviewAsync(Token);
        Assert.Equal("PostgreSQL 16", overview.Product);
        Assert.StartsWith("16.", overview.Version, StringComparison.Ordinal);
        Assert.True(overview.StartedAt < DateTimeOffset.Now);
        Assert.Contains(overview.Databases, static d => d.Name == "postgres" && d.DataMb > 0);
        var first = await dashboard.GetSampleAsync(Token);
        await RunAsync(connection, "SELECT count(*) FROM generate_series(1, 100000)");

        // Statistics reach the shared counters at most once a second; this backend's are pushed now.
        await RunAsync(connection, "SELECT pg_stat_force_next_flush()");
        await RunAsync(connection, "SELECT 1");
        var second = await dashboard.GetSampleAsync(Token);
        Assert.True(second.Transactions > first.Transactions);
        Assert.True(second.Connections >= 1);
        Assert.True(second.MemoryUsedMb > 0);
        Assert.NotNull(second.BytesWritten);

        var health = await Assert.IsAssignableFrom<IHealthCheck>(connection).CheckAsync(Token);
        Assert.Equal(health.Count, health.Select(static f => f.RuleId).Distinct().Count());
        HealthFinding Rule(string id) => Assert.Single(health, f => f.RuleId == id);
        Assert.Equal(HealthSeverity.Warning, Rule("SharedBuffers").Severity);
        Assert.Equal("128 MB", Rule("SharedBuffers").Current);
        Assert.Equal(HealthSeverity.Ok, Rule("Autovacuum").Severity);
        Assert.Equal(HealthSeverity.Ok, Rule("StatStatements").Severity);
        Assert.Equal(HealthSeverity.Ok, Rule("Wraparound").Severity);
        Assert.Equal(HealthSeverity.Info, Rule("Uptime").Severity);
        Assert.Equal(HealthSeverity.Info, Rule("RandomPageCost").Severity);

        await RunAsync(connection, "DROP DATABASE IF EXISTS jq_detail");
        await RunAsync(connection, "CREATE DATABASE jq_detail");
        var current = await ((IDatabaseContext)connection).GetCurrentAsync(Token);
        await ((IDatabaseContext)connection).ChangeAsync("jq_detail", Token);
        await RunAsync(connection, "CREATE TABLE big (id serial PRIMARY KEY, payload text); INSERT INTO big (payload) SELECT repeat('x', 200) FROM generate_series(1, 5000); ANALYZE big; SELECT pg_stat_force_next_flush();");
        await ((IDatabaseContext)connection).ChangeAsync(current!, Token);

        var detail = await Assert.IsAssignableFrom<IDatabaseDetails>(connection).GetDatabaseDetailAsync("jq_detail", Token);
        Assert.Equal("jq_detail", detail.Name);
        Assert.Equal("ONLINE", detail.State);
        Assert.NotNull(detail.Owner);
        Assert.True(detail.DataMb > 0);
        Assert.Equal(1, detail.TableCount);
        var big = Assert.Single(detail.Tables);
        Assert.Equal("public.big", big.Name);
        Assert.True(big.ReservedMb > 0 && big.IndexMb > 0);
        Assert.Contains(detail.Files, static f => f.Name == "pg_default" && f.Type == "TABLESPACE");
        Assert.False(detail.BackupsKnown);
        Assert.Equal(current, await ((IDatabaseContext)connection).GetCurrentAsync(Token));
    }

    private async Task<IDatabaseConnection> OpenAsync()
    {
        if (fixture.SkipReason is { } reason)
        {
            Assert.Skip(reason);
        }

        return await new PostgreSqlProvider().OpenConnectionAsync(fixture.Profile, fixture.Credential, Token);
    }

    private static async Task RunAsync(IDatabaseConnection connection, string sql)
    {
        await foreach (var item in connection.Executor.ExecuteAsync(new QueryRequest(sql), Token))
        {
            if (item is QueryMessage { Severity: QueryMessageSeverity.Error } error)
            {
                throw new InvalidOperationException(error.Text);
            }
        }
    }

    private static async Task<string> PidAsync(IDatabaseConnection connection)
    {
        await foreach (var item in connection.Executor.ExecuteAsync(new QueryRequest("SELECT pg_backend_pid()"), Token))
        {
            if (item is RowBatch rows)
            {
                return Convert.ToString(rows.Rows[0][0], CultureInfo.InvariantCulture)!;
            }
        }

        throw new InvalidOperationException("no pid");
    }
}
