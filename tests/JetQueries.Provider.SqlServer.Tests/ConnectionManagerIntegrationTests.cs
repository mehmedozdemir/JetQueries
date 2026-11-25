using JetQueries.Core.Connections;
using JetQueries.Core.Extensions;
using JetQueries.Core.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using JetQueries.Sdk;

namespace JetQueries.Provider.SqlServer.Tests;

/// <summary>The host's connection manager with the real provider, the real TCP probe and a real server.</summary>
[Trait("Category", "Integration")]
public sealed class ConnectionManagerIntegrationTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Test_connection_succeeds_fails_on_a_wrong_password_and_detects_an_unreachable_address()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        var credentials = new SessionCredentialStore();
        await using var manager = new ConnectionManager(new SingleProvider(new SqlServerProvider()), credentials, new TcpReachabilityProbe());
        var definition = new ConnectionDefinition
        {
            Id = Guid.NewGuid(),
            Name = "container",
            ProviderId = SqlServerProvider.ProviderId,
            Settings = fixture.Profile.Settings,
            UserName = fixture.Credential.UserName,
            SavePassword = true,
        };

        var ok = await manager.TestAsync(definition, fixture.Credential.Password, Token);
        Assert.True(ok.Outcome == ConnectionOutcome.Succeeded, ok.Detail);

        var wrong = await manager.TestAsync(definition, "definitely-not-the-password", Token);
        Assert.Equal(ConnectionOutcome.Failed, wrong.Outcome);
        Assert.Contains("Login failed", wrong.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("definitely-not-the-password", wrong.ToString(), StringComparison.Ordinal);

        Assert.Equal(ConnectionOutcome.PasswordRequired, (await manager.TestAsync(definition, null, Token)).Outcome);

        // Nothing listens on port 1 of this machine.
        var closed = definition with { Settings = new Dictionary<string, string>(definition.Settings) { ["server"] = "127.0.0.1,1" } };
        var unreachable = await manager.TestAsync(closed, "x", Token);
        Assert.Equal(ConnectionOutcome.Unreachable, unreachable.Outcome);
        Assert.True(unreachable.Elapsed < TimeSpan.FromSeconds(8), $"took {unreachable.Elapsed}");

        // With the password stored, a session opens and can run a query.
        await credentials.SetPasswordAsync(definition.Id, fixture.Credential.Password!, Token);
        var opened = await manager.OpenAsync(definition, null, Token);
        Assert.Equal(ConnectionOutcome.Succeeded, opened.Outcome);
        var rows = new List<object?[]>();
        await foreach (var item in opened.Session!.Connection.Executor.ExecuteAsync(new QueryRequest("SELECT DB_NAME()"), Token))
        {
            if (item is RowBatch batch)
            {
                rows.AddRange(batch.Rows);
            }
        }

        Assert.Equal("master", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task Metadata_is_read_on_its_own_connection_while_a_query_runs_and_is_cached()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await using var manager = new ConnectionManager(new SingleProvider(new SqlServerProvider()), new SessionCredentialStore(), new TcpReachabilityProbe());
        await using var cache = new SqliteMetadataCache(new MetadataCacheOptions(), TimeProvider.System, NullLogger<SqliteMetadataCache>.Instance);
        var metadata = new MetadataService(cache, TimeProvider.System);
        var definition = new ConnectionDefinition
        {
            Id = Guid.NewGuid(),
            Name = "container",
            ProviderId = SqlServerProvider.ProviderId,
            Settings = fixture.Profile.Settings,
            UserName = fixture.Credential.UserName,
        };
        var session = (await manager.OpenAsync(definition, fixture.Credential.Password, Token)).Session!;

        await foreach (var item in session.Connection.Executor.ExecuteAsync(
                           new QueryRequest("IF OBJECT_ID(N'dbo.jq_probe') IS NULL CREATE TABLE dbo.jq_probe (id int NOT NULL PRIMARY KEY, note nvarchar(20) NULL);"), Token))
        {
            _ = item;
        }

        // A slow statement occupies the query connection...
        var slow = Task.Run(
            async () =>
            {
                await foreach (var item in session.Connection.Executor.ExecuteAsync(new QueryRequest("WAITFOR DELAY '00:00:04'; SELECT 1;"), Token))
                {
                    _ = item;
                }
            },
            Token);
        await Task.Delay(300, Token);

        // ...and the explorer still gets its answers, from the second connection.
        var started = DateTime.UtcNow;
        var databases = await metadata.GetObjectsAsync(session, null, DatabaseObjectKinds.Database, refresh: false, Token);
        var master = databases.Single(static d => d.Name.Name == "master");
        var schemas = await metadata.GetObjectsAsync(session, master, DatabaseObjectKinds.Schema, refresh: false, Token);
        var views = await metadata.GetObjectsAsync(session, schemas.Single(static s => s.Name.Name == "dbo"), DatabaseObjectKinds.Table | DatabaseObjectKinds.View, refresh: false, Token);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "metadata waited for the running query");
        Assert.False(slow.IsCompleted);
        var probe = Assert.Single(views, static v => v.Name.Name == "jq_probe");
        var columns = await metadata.GetColumnsAsync(session, probe.Name, refresh: false, Token);
        Assert.Equal(["id", "note"], columns.Select(static c => c.Name));

        // Later requests are answered from the cache (the very same list, no round trip) until a refresh.
        var cached = await metadata.GetObjectsAsync(session, null, DatabaseObjectKinds.Database, refresh: false, Token);
        Assert.Equal(databases, cached);
        Assert.Same(cached, await metadata.GetObjectsAsync(session, null, DatabaseObjectKinds.Database, refresh: false, Token));
        Assert.NotSame(cached, await metadata.GetObjectsAsync(session, null, DatabaseObjectKinds.Database, refresh: true, Token));
        await slow;
    }

    [Fact]
    public async Task A_session_runs_statements_one_at_a_time_and_refuses_writes_when_read_only()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await using var manager = new ConnectionManager(new SingleProvider(new SqlServerProvider()), new SessionCredentialStore(), new TcpReachabilityProbe());
        var definition = new ConnectionDefinition
        {
            Id = Guid.NewGuid(),
            Name = "container",
            ProviderId = SqlServerProvider.ProviderId,
            Settings = fixture.Profile.Settings,
            UserName = fixture.Credential.UserName,
        };
        var session = (await manager.OpenAsync(definition, fixture.Credential.Password, Token)).Session!;

        // Two statements started together on the one query connection: the second waits for the first
        // instead of failing with "there is already an open DataReader".
        var slow = RowsAsync(session, "WAITFOR DELAY '00:00:01'; SELECT 'first' AS v;");
        var fast = RowsAsync(session, "SELECT 'second' AS v;");
        Assert.Equal("first", Assert.Single(await slow)[0]);
        Assert.Equal("second", Assert.Single(await fast)[0]);

        var readOnly = (await manager.OpenAsync(definition with { Id = Guid.NewGuid(), IsReadOnly = true }, fixture.Credential.Password, Token)).Session!;
        Assert.Equal("master", Assert.Single(await RowsAsync(readOnly, "SELECT DB_NAME() -- no update here"))[0]);
        await Assert.ThrowsAsync<JetQueries.Core.Queries.ReadOnlyConnectionException>(
            () => RowsAsync(readOnly, "CREATE TABLE dbo.should_not_exist (id int);"));
        Assert.Equal(0, Assert.Single(await RowsAsync(readOnly, "SELECT COUNT(*) FROM sys.tables WHERE name = N'should_not_exist'"))[0]);
    }

    [Fact]
    public async Task A_window_never_hands_an_open_transaction_to_the_next_one()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await using var manager = new ConnectionManager(new SingleProvider(new SqlServerProvider()), new SessionCredentialStore(), new TcpReachabilityProbe());
        var definition = new ConnectionDefinition
        {
            Id = Guid.NewGuid(),
            Name = "container",
            ProviderId = SqlServerProvider.ProviderId,
            Settings = fixture.Profile.Settings,
            UserName = fixture.Credential.UserName,
        };
        var session = (await manager.OpenAsync(definition, fixture.Credential.Password, Token)).Session!;
        await Drain(session.ExecuteAsync(new QueryRequest("IF OBJECT_ID(N'dbo.jq_tx') IS NOT NULL DROP TABLE dbo.jq_tx; CREATE TABLE dbo.jq_tx (id int)"), Token));

        // The first window gets the session's main connection, begins and changes, then closes without a word.
        var first = await session.OpenChannelAsync(Token);
        Assert.True(first.SupportsTransactions);
        await first.BeginTransactionAsync(Token);
        await Drain(first.ExecuteAsync(new QueryRequest("INSERT INTO dbo.jq_tx VALUES (1)"), Token));
        Assert.True(await first.HasOpenTransactionAsync(Token));
        await first.DisposeAsync();

        // The next window gets the same connection: nothing open, nothing kept.
        var second = await session.OpenChannelAsync(Token);
        Assert.False(await second.HasOpenTransactionAsync(Token));
        var rows = new List<QueryEvent>();
        await foreach (var item in second.ExecuteAsync(new QueryRequest("SELECT COUNT(*) FROM dbo.jq_tx"), Token))
        {
            rows.Add(item);
        }

        Assert.Equal(0, rows.OfType<RowBatch>().Single().Rows[0][0]);
        await second.DisposeAsync();

        static async Task Drain(IAsyncEnumerable<QueryEvent> events)
        {
            await foreach (var _ in events)
            {
            }
        }
    }

    [Fact]
    public async Task A_script_with_go_runs_batch_by_batch_and_completion_reads_real_metadata()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await using var manager = new ConnectionManager(new SingleProvider(new SqlServerProvider()), new SessionCredentialStore(), new TcpReachabilityProbe());
        await using var cache = new SqliteMetadataCache(new MetadataCacheOptions(), TimeProvider.System, NullLogger<SqliteMetadataCache>.Instance);
        var definition = new ConnectionDefinition
        {
            Id = Guid.NewGuid(),
            Name = "container",
            ProviderId = SqlServerProvider.ProviderId,
            Settings = fixture.Profile.Settings,
            UserName = fixture.Credential.UserName,
        };
        var session = (await manager.OpenAsync(definition, fixture.Credential.Password, Token)).Session!;

        // CREATE PROCEDURE must be alone in its batch: without GO splitting this script cannot run at all.
        const string Script = """
            IF OBJECT_ID(N'dbo.jq_go') IS NOT NULL DROP TABLE dbo.jq_go;
            CREATE TABLE dbo.jq_go (id int IDENTITY PRIMARY KEY, [Açıklama Metni] nvarchar(40) NULL);
            GO
            CREATE OR ALTER PROCEDURE dbo.jq_go_count AS SELECT COUNT(*) AS n FROM dbo.jq_go;
            GO
            INSERT INTO dbo.jq_go DEFAULT VALUES;
            GO 3
            PRINT 'after the inserts';
            EXEC dbo.jq_go_count;
            """;
        var events = new List<QueryEvent>();
        await foreach (var item in session.ExecuteAsync(new QueryRequest(Script), Token))
        {
            events.Add(item);
        }

        Assert.Equal(3, events.OfType<StatementCompleted>().Count(static e => e.RowsAffected == 1));
        Assert.Equal(3, events.OfType<RowBatch>().Single().Rows.Single()[0]);

        // The PRINT is on line 8 of the whole script, not line 1 of its batch.
        var print = events.OfType<QueryMessage>().Single(static m => m.Text == "after the inserts");
        Assert.Equal(8, print.Line);

        // Completion against the real catalog, through the cache.
        var service = session.Provider.LanguageService!;
        var metadata = new SessionMetadataProvider(session, new MetadataService(cache, TimeProvider.System));
        const string Sql = "SELECT g. FROM dbo.jq_go g";
        var columns = await service.GetCompletionsAsync(new SqlDocument(Sql, metadata), "SELECT g.".Length, Token);
        Assert.Equal(["id", "Açıklama Metni"], columns.Select(static c => c.Label));
        Assert.Equal("[Açıklama Metni]", columns[1].InsertText);
        Assert.Equal("int · PK", columns[0].Detail);

        var tables = await service.GetCompletionsAsync(new SqlDocument("SELECT * FROM jq_", metadata), 17, Token);
        Assert.Contains(tables, static t => t is { Label: "jq_go", Kind: CompletionItemKind.Table });

        var procedures = await service.GetCompletionsAsync(new SqlDocument("EXEC ", metadata), 5, Token);
        Assert.Contains(procedures, static t => t is { Label: "jq_go_count", Kind: CompletionItemKind.Procedure });

        var schemas = await service.GetCompletionsAsync(new SqlDocument("SELECT * FROM tempdb.", metadata), 21, Token);
        Assert.Contains(schemas, static t => t is { Label: "dbo", Kind: CompletionItemKind.Schema });

        // Read ahead: the tables of dbo and every one of their columns in one read, kept under the name an
        // unqualified table is asked by.
        await using var fresh = new SqliteMetadataCache(new MetadataCacheOptions(), TimeProvider.System, NullLogger<SqliteMetadataCache>.Instance);
        var ahead = new SessionMetadataProvider(session, new MetadataService(fresh, TimeProvider.System));
        Assert.True(await ((ISqlMetadataWarmUp)service).WarmUpAsync(new SqlDocument(string.Empty, ahead), Token) > 0);
        var preloaded = await fresh.GetColumnsAsync(definition.Id, new DatabaseObjectName(null, "dbo", "jq_go"), Token);
        Assert.Equal(["id", "Açıklama Metni"], preloaded!.Value.Select(static c => c.Name));
        Assert.Equal("int", preloaded.Value[0].DataType);
        Assert.True(preloaded.Value[0].IsPrimaryKey);

        // Go to definition: a procedure's stored source, and a script written from the catalog for a table.
        var procedure = await service.GetDefinitionAsync(new SqlDocument("EXEC dbo.jq_go_count", metadata), 12, Token);
        Assert.Equal(DatabaseObjectKinds.Procedure, procedure!.Target!.Kind);
        Assert.Contains("PROCEDURE dbo.jq_go_count AS SELECT COUNT(*)", await metadata.GetDefinitionAsync(procedure.Target, Token), StringComparison.Ordinal);

        var column = await service.GetDefinitionAsync(new SqlDocument("SELECT g.id FROM jq_go g", metadata), 9, Token);
        Assert.Equal(DatabaseObjectKinds.Table, column!.Target!.Kind);
        Assert.Equal("id", column.MemberName);
        var script = await metadata.GetDefinitionAsync(column.Target, Token);
        Assert.Contains("CREATE TABLE [dbo].[jq_go]", script, StringComparison.Ordinal);
        Assert.Contains("[Açıklama Metni] nvarchar(40) NULL", script, StringComparison.Ordinal);

        // The server's own parser and Microsoft's ScriptDom agree that the generated script is valid T-SQL.
        Assert.Empty(await service.GetDiagnosticsAsync(new SqlDocument(script!), Token));
    }

    private static async Task<List<object?[]>> RowsAsync(ConnectionSession session, string sql)
    {
        var rows = new List<object?[]>();
        await foreach (var item in session.ExecuteAsync(new QueryRequest(sql), Token))
        {
            if (item is RowBatch batch)
            {
                rows.AddRange(batch.Rows);
            }
        }

        return rows;
    }

    private sealed class SingleProvider(IDatabaseProvider provider) : IExtensionHost
    {
        public IReadOnlyList<ExtensionLoadResult> Extensions => [];

        public IReadOnlyCollection<IDatabaseProvider> DatabaseProviders => [provider];

        public Task<IReadOnlyList<ExtensionLoadResult>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Extensions);

        public IDatabaseProvider? FindDatabaseProvider(string id) => id == provider.Id ? provider : null;
    }
}
