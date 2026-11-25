using System.Globalization;
using JetQueries.Sdk;

namespace JetQueries.Provider.Oracle.Tests;

/// <summary>The DBA screens' reads on a real Oracle (opt-in: JETQUERIES_ORACLE_TESTS=Y). They connect as SYSTEM.</summary>
[Trait("Category", "Integration")]
public sealed class OracleDbaTests(OracleFixture fixture) : IClassFixture<OracleFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string AppSchema => fixture.Credential.UserName!.ToUpperInvariant();

    [Fact]
    public async Task The_monitor_sees_who_blocks_whom_and_ends_the_blocker()
    {
        await using var holder = await OpenAsync(fixture.Credential);
        await using var waiter = await OpenAsync(fixture.Credential);
        await using var monitor = await OpenAsync(fixture.SystemCredential);
        var activity = Assert.IsAssignableFrom<IActivityMonitor>(monitor);
        await RunAsync(holder, "BEGIN EXECUTE IMMEDIATE 'DROP TABLE jq_activity'; EXCEPTION WHEN OTHERS THEN NULL; END;");
        await RunAsync(holder, "CREATE TABLE jq_activity (id NUMBER PRIMARY KEY, n NUMBER)");
        await RunAsync(holder, "INSERT INTO jq_activity VALUES (1, 0)");
        await RunAsync(holder, "COMMIT");
        await ((ITransactionControl)holder).BeginAsync(Token);
        await RunAsync(holder, "UPDATE jq_activity SET n = 1 WHERE id = 1");
        var holderSid = await SidAsync(holder);
        var waiterSid = await SidAsync(waiter);
        var waiting = RunAsync(waiter, "UPDATE jq_activity SET n = 2 WHERE id = 1");

        ActivitySession? blocked = null;
        ActivitySnapshot? snapshot = null;
        for (var i = 0; i < 50 && blocked is null; i++)
        {
            await Task.Delay(200, Token);
            snapshot = await activity.GetActivityAsync(Token);
            blocked = snapshot.Sessions.FirstOrDefault(s => s.Id.StartsWith(waiterSid + ",", StringComparison.Ordinal) && s.BlockedBy is not null);
        }

        Assert.NotNull(blocked);
        Assert.StartsWith(holderSid + ",", blocked.BlockedBy, StringComparison.Ordinal);
        Assert.StartsWith("enq: TX", blocked.WaitType, StringComparison.Ordinal);
        Assert.Equal("UPDATE", blocked.Command);
        Assert.Contains("jq_activity", blocked.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AppSchema, blocked.Login);
        Assert.StartsWith(await SidAsync(monitor) + ",", snapshot!.OwnSessionId, StringComparison.Ordinal);
        var head = snapshot.Sessions.Single(s => s.Id == blocked.BlockedBy);
        Assert.Equal(1, head.OpenTransactions);
        Assert.Equal("inactive", head.Status);
        Assert.True(head.IsUserSession);
        Assert.Contains(snapshot.Sessions, static s => !s.IsUserSession);

        await activity.KillSessionAsync(head.Id, Token);
        await waiting.WaitAsync(TimeSpan.FromSeconds(20), Token);
        await Assert.ThrowsAsync<ArgumentException>(() => activity.KillSessionAsync("1,2' IMMEDIATE; DROP", Token));
    }

    [Fact]
    public async Task Heavy_queries_full_scans_and_index_use_are_read()
    {
        await using var app = await OpenAsync(fixture.Credential);
        await using var system = await OpenAsync(fixture.SystemCredential);
        var advisor = Assert.IsAssignableFrom<IPerformanceAdvisor>(system);
        await RunAsync(app, "BEGIN EXECUTE IMMEDIATE 'DROP TABLE jq_orders'; EXCEPTION WHEN OTHERS THEN NULL; END;");
        await RunAsync(app, "CREATE TABLE jq_orders (id NUMBER PRIMARY KEY, customer_id NUMBER NOT NULL, status NUMBER NOT NULL, total NUMBER(12, 2) NOT NULL)");
        await RunAsync(app, "INSERT INTO jq_orders SELECT LEVEL, MOD(LEVEL, 5000), MOD(LEVEL, 5), MOD(LEVEL, 10000) FROM dual CONNECT BY LEVEL <= 20000");
        await RunAsync(app, "COMMIT");
        await RunAsync(app, "CREATE INDEX jq_orders_customer ON jq_orders (customer_id)");
        await RunAsync(app, "CREATE INDEX jq_orders_status ON jq_orders (status DESC)");
        await RunAsync(app, "BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER, 'JQ_ORDERS'); END;");
        for (var i = 0; i < 4; i++)
        {
            await RunAsync(app, "SELECT id FROM jq_orders WHERE total = 1234");
            await RunAsync(app, "SELECT id FROM jq_orders WHERE customer_id = 42");
        }

        var heavy = await advisor.GetHeavyQueriesAsync(new HeavyQueryRequest(HeavyQueryOrder.Executions, 100, AppSchema), Token);
        var total = Assert.Single(heavy.Queries, static q => q.Sql.Contains("WHERE total = 1234", StringComparison.Ordinal));
        Assert.True(total.Executions >= 4, total.Executions.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(AppSchema, total.Database);
        Assert.True(total.TotalReads > 0);
        Assert.NotNull(heavy.CountingSince);
        Assert.DoesNotContain(heavy.Queries, static q => q.Sql.Contains("JetQueries:advisor", StringComparison.Ordinal));
        Assert.All(heavy.Queries, q => Assert.Equal(AppSchema, q.Database));

        var scans = await advisor.GetMissingIndexesAsync(AppSchema, Token);
        var orders = Assert.Single(scans, s => s.Table == AppSchema + ".JQ_ORDERS");
        Assert.True(orders.Uses >= 4);
        Assert.Contains("CREATE INDEX", orders.CreateScript, StringComparison.Ordinal);

        var usage = await advisor.GetIndexUsageAsync(AppSchema, Token);
        var primary = Assert.Single(usage.Indexes, i => i.IsConstraint && i.Table == AppSchema + ".JQ_ORDERS");
        Assert.Null(primary.DropScript);
        var customer = Assert.Single(usage.Indexes, static i => i.Name == "JQ_ORDERS_CUSTOMER");
        Assert.Equal(["CUSTOMER_ID"], customer.KeyColumns);
        Assert.True(customer.Seeks >= 4, customer.Seeks.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(0, customer.Scans);
        Assert.Equal("DROP INDEX \"" + AppSchema + "\".\"JQ_ORDERS_CUSTOMER\";", customer.DropScript);
        Assert.Equal(0, usage.Indexes.Single(static i => i.Name == "JQ_ORDERS_STATUS").Reads);
        Assert.True(customer.SizeKb > 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => advisor.GetFragmentationAsync(AppSchema, 1, Token));
    }

    [Fact]
    public async Task The_dashboard_the_health_check_and_a_schemas_detail_are_read()
    {
        await using var app = await OpenAsync(fixture.Credential);
        await using var connection = await OpenAsync(fixture.SystemCredential);
        await RunAsync(app, "BEGIN EXECUTE IMMEDIATE 'DROP TABLE jq_big'; EXCEPTION WHEN OTHERS THEN NULL; END;");
        await RunAsync(app, "CREATE TABLE jq_big (id NUMBER PRIMARY KEY, payload VARCHAR2(200))");
        await RunAsync(app, "INSERT INTO jq_big SELECT LEVEL, RPAD('x', 200, 'x') FROM dual CONNECT BY LEVEL <= 5000");
        await RunAsync(app, "COMMIT");
        await RunAsync(app, "BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER, 'JQ_BIG'); END;");

        var dashboard = Assert.IsAssignableFrom<IServerDashboard>(connection);
        var overview = await dashboard.GetOverviewAsync(Token);
        Assert.StartsWith("Oracle", overview.Product, StringComparison.Ordinal);
        Assert.DoesNotContain("Release", overview.Product, StringComparison.Ordinal);
        Assert.Matches(@"^\d+\.\d+", overview.Version);
        Assert.True(overview.StartedAt < DateTimeOffset.Now);
        Assert.True(overview.CpuCount > 0);
        Assert.Contains(overview.Databases, d => d.Name == AppSchema && d.DataMb > 0);
        Assert.DoesNotContain(overview.Databases, static d => d.Name == "SYS");

        var first = await dashboard.GetSampleAsync(Token);
        for (var i = 0; i < 5; i++)
        {
            await RunAsync(app, "INSERT INTO jq_big VALUES (" + (10000 + i).ToString(CultureInfo.InvariantCulture) + ", 'y')");
            await RunAsync(app, "COMMIT");
        }

        var second = await dashboard.GetSampleAsync(Token);
        Assert.True(second.Transactions > first.Transactions);
        Assert.True(second.BatchRequests > first.BatchRequests);
        Assert.True(second.Connections >= 2);
        Assert.True(second.MemoryUsedMb > 0);
        Assert.NotEmpty(second.Waits);

        var health = await Assert.IsAssignableFrom<IHealthCheck>(connection).CheckAsync(Token);
        Assert.Equal(health.Count, health.Select(static f => f.RuleId).Distinct().Count());
        HealthFinding Rule(string id) => Assert.Single(health, f => f.RuleId == id);
        Assert.Equal(HealthSeverity.Warning, Rule("ArchiveLog").Severity);
        Assert.Equal(HealthSeverity.Critical, Rule("RmanBackup").Severity);
        Assert.Equal(HealthSeverity.Ok, Rule("UnusableIndexes").Severity);
        Assert.Contains(health, static f => f.RuleId == "Uptime");
        Assert.Contains(health, static f => f.RuleId == "TablespaceFull");

        var detail = await Assert.IsAssignableFrom<IDatabaseDetails>(connection).GetDatabaseDetailAsync(AppSchema, Token);
        Assert.Equal(AppSchema, detail.Name);
        Assert.Equal("OPEN", detail.State);
        Assert.Equal("NOARCHIVELOG", detail.RecoveryModel);
        Assert.True(detail.DataMb > 0);
        Assert.True(detail.LogMb > 0);
        var big = Assert.Single(detail.Tables, t => t.Name == AppSchema + ".JQ_BIG");
        Assert.Equal(5000, big.Rows);
        Assert.True(big.DataMb > 0 && big.IndexMb > 0);
        Assert.Contains(detail.Files, static f => f.Type == "DATA" && f.SizeMb > 0);
        Assert.Contains(detail.Options, static o => o.Key == "defaultTablespace" && o.Value is not null);
        Assert.True(detail.BackupsKnown);
        Assert.Empty(detail.Backups);
        await Assert.ThrowsAsync<ArgumentException>(() => ((IDatabaseDetails)connection).GetDatabaseDetailAsync("NO_SUCH_SCHEMA_X", Token));
    }

    private async Task<IDatabaseConnection> OpenAsync(ConnectionCredential credential)
    {
        if (fixture.SkipReason is { } reason)
        {
            Assert.Skip(reason);
        }

        return await new OracleProvider().OpenConnectionAsync(fixture.Profile, credential, Token);
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

    private static async Task<string> SidAsync(IDatabaseConnection connection)
    {
        await foreach (var item in connection.Executor.ExecuteAsync(new QueryRequest("SELECT SYS_CONTEXT('USERENV', 'SID') FROM dual"), Token))
        {
            if (item is RowBatch rows)
            {
                return Convert.ToString(rows.Rows[0][0], CultureInfo.InvariantCulture)!;
            }
        }

        throw new InvalidOperationException("no sid");
    }
}
