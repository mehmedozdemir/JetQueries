using JetQueries.Core.Connections;
using JetQueries.Core.Extensions;
using JetQueries.Core.Metadata;
using JetQueries.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Testcontainers.Oracle;

namespace JetQueries.Provider.Oracle.Tests;

/// <summary>
/// Starts Oracle Database Free in Docker once for all integration tests, from the image that is already on the
/// developer's machine. Opt-in (<c>JETQUERIES_ORACLE_TESTS=Y</c>): the container takes about a minute to start,
/// too long for every test run. Without it, or without Docker, the tests are skipped.
/// </summary>
public sealed class OracleFixture : IAsyncLifetime
{
    public const string OptInVariable = "JETQUERIES_ORACLE_TESTS";
    public const string Image = "gvenzl/oracle-free:23-slim";

    private OracleContainer? _container;

    public string? SkipReason { get; private set; }

    public ConnectionProfile Profile { get; private set; } = null!;

    public ConnectionCredential Credential { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(OptInVariable), "Y", StringComparison.OrdinalIgnoreCase))
        {
            SkipReason = $"Set {OptInVariable}=Y to start an Oracle container and run the integration tests.";
            return;
        }

        try
        {
            _container = new OracleBuilder(Image).Build();
            await _container.StartAsync();
        }
#pragma warning disable CA1031 // Any failure to start Docker means "skip", whatever the exception type.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SkipReason = $"Docker is not available: {ex.Message}";
            return;
        }

        var builder = new OracleConnectionStringBuilder(_container.GetConnectionString());
        Credential = new ConnectionCredential(builder.UserID, builder.Password);
        Profile = new ConnectionProfile(Guid.NewGuid(), "container", OracleProvider.ProviderId, new Dictionary<string, string>
        {
            ["host"] = _container.Hostname,
            ["port"] = _container.GetMappedPublicPort(1521).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["serviceName"] = "FREEPDB1",
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[Trait("Category", "Integration")]
public sealed class OracleIntegrationTests(OracleFixture fixture) : IClassFixture<OracleFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_query_streams_columns_and_rows()
    {
        await using var connection = await OpenAsync();
        const string Sql = """
            SELECT LEVEL AS n, 'Müşteri ' || LEVEL AS name, CAST(NULL AS DATE) AS d,
                   12345678901234567890123456789012345678 AS huge, DATE '2026-01-15' AS issued
            FROM dual CONNECT BY LEVEL <= 5
            """;

        var events = await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(Sql) { BatchSize = 2 }, Token));

        var started = Assert.IsType<ResultSetStarted>(events[0]);
        Assert.Equal(["N", "NAME", "D", "HUGE", "ISSUED"], started.Columns.Select(static c => c.Name));
        Assert.Equal([2, 2, 1], events.OfType<RowBatch>().Select(static b => b.Rows.Count));
        Assert.Equal(new ResultSetCompleted(5, false), events.OfType<ResultSetCompleted>().Single());

        var rows = events.OfType<RowBatch>().SelectMany(static b => b.Rows).ToList();
        Assert.Equal(1m, Convert.ToDecimal(rows[0][0], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("Müşteri 5", rows[4][1]);
        Assert.Null(rows[0][2]);

        // 38 digits do not fit a .NET decimal: the value arrives as text instead of failing the result.
        Assert.Equal("12345678901234567890123456789012345678", rows[0][3]!.ToString());
        Assert.Equal(new DateTime(2026, 1, 15), rows[0][4]);
    }

    [Fact]
    public async Task Bind_variables_max_rows_and_errors()
    {
        await using var connection = await OpenAsync();
        var echo = new QueryRequest("SELECT :text AS echoed, :nothing AS missing FROM dual")
        {
            Parameters = [new QueryParameter("text", "'; DROP TABLE x; --"), new QueryParameter("nothing", null)],
        };
        var row = (await ToListAsync(connection.Executor.ExecuteAsync(echo, Token))).OfType<RowBatch>().Single().Rows.Single();
        Assert.Equal("'; DROP TABLE x; --", row[0]);
        Assert.Null(row[1]);

        var limited = await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("SELECT LEVEL FROM dual CONNECT BY LEVEL <= 1000") { MaxRows = 10 }, Token));
        Assert.Equal(new ResultSetCompleted(10, true), limited.OfType<ResultSetCompleted>().Single());

        var error = await Assert.ThrowsAsync<OracleException>(
            () => ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("SELECT * FROM table_that_does_not_exist"), Token)));
        Assert.Equal(942, error.Number);
    }

    [Fact]
    public async Task Cancel_stops_a_running_block_quickly()
    {
        await using var connection = await OpenAsync();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(500));
        var started = DateTime.UtcNow;

        // A block that computes for minutes. (A block that only waits — DBMS_SESSION.SLEEP, a row lock — is
        // not interrupted through Docker's port forwarding; see the note in OracleQueryExecutor.)
        const string Loop = "DECLARE x NUMBER := 0; BEGIN FOR i IN 1..2000000000 LOOP x := x + 1; END LOOP; END;";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(Loop), cancel.Token)));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "cancel took too long");
        var events = await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("SELECT 1 FROM dual"), Token));
        Assert.Single(events.OfType<RowBatch>());
    }

    [Fact]
    public async Task A_script_runs_piece_by_piece_with_output_counts_and_compile_errors()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await using var manager = new ConnectionManager(new SingleProvider(new OracleProvider()), new SessionCredentialStore(), new TcpReachabilityProbe());
        var definition = Definition();
        var session = (await manager.OpenAsync(definition, fixture.Credential.Password, Token)).Session!;

        const string Script = """
            SET SERVEROUTPUT ON
            CREATE TABLE jq_cards (id NUMBER(10) PRIMARY KEY, note VARCHAR2(40 CHAR));
            INSERT INTO jq_cards VALUES (1, 'bir;iki');
            INSERT INTO jq_cards SELECT LEVEL + 1, 'satır ' || LEVEL FROM dual CONNECT BY LEVEL <= 2;
            CREATE OR REPLACE PROCEDURE jq_report IS
              l_count NUMBER;
            BEGIN
              SELECT COUNT(*) INTO l_count FROM jq_cards;
              DBMS_OUTPUT.PUT_LINE('rows: ' || l_count);
              DBMS_OUTPUT.PUT_LINE('');
              DBMS_OUTPUT.PUT_LINE('Müşteri');
            END jq_report;
            /
            EXEC jq_report
            CREATE OR REPLACE PROCEDURE jq_broken IS
            BEGIN
              this is not plsql;
            END;
            /
            SELECT COUNT(*) AS n FROM jq_cards;
            """;
        var events = new List<QueryEvent>();
        await foreach (var item in session.ExecuteAsync(new QueryRequest(Script.ReplaceLineEndings("\n")), Token))
        {
            events.Add(item);
        }

        Assert.Equal([1L, 2L], events.OfType<StatementCompleted>().Where(static e => e.RowsAffected is not null).Select(static e => e.RowsAffected!.Value));
        var messages = events.OfType<QueryMessage>().ToList();
        Assert.Equal(["rows: 3", string.Empty, "Müşteri"], messages.Where(static m => m.Severity == QueryMessageSeverity.Info).Select(static m => m.Text));

        // The broken procedure "succeeds"; its errors come from the catalog, with their line in the whole script.
        var compile = Assert.Single(messages, static m => m.Severity == QueryMessageSeverity.Error);
        Assert.StartsWith("PROCEDURE JQ_BROKEN, line 3", compile.Text, StringComparison.Ordinal);
        Assert.Contains("PLS-", compile.Text, StringComparison.Ordinal);
        Assert.Equal(17, compile.Line);

        Assert.Equal(3m, Convert.ToDecimal(events.OfType<RowBatch>().Single().Rows.Single()[0], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Metadata_walks_schemas_objects_columns_and_definitions()
    {
        await using var connection = await OpenAsync();
        foreach (var sql in new[]
                 {
                     "CREATE TABLE \"Müşteri İşlemleri\" (id NUMBER(18) NOT NULL, ad VARCHAR2(50 CHAR) NOT NULL, tutar NUMBER(12,2), notlar CLOB, kayit TIMESTAMP(6), CONSTRAINT mi_pk PRIMARY KEY (id))",
                     "CREATE VIEW jq_ozet AS SELECT id, ad FROM \"Müşteri İşlemleri\"",
                     "CREATE SEQUENCE jq_seq START WITH 10",
                     "CREATE OR REPLACE PACKAGE jq_pkg AS\n  FUNCTION total RETURN NUMBER;\nEND jq_pkg;",
                     "CREATE OR REPLACE PACKAGE BODY jq_pkg AS\n  FUNCTION total RETURN NUMBER IS\n  BEGIN\n    RETURN 42;\n  END total;\nEND jq_pkg;",
                     "CREATE OR REPLACE TRIGGER jq_trg BEFORE INSERT ON \"Müşteri İşlemleri\" FOR EACH ROW BEGIN NULL; END;",
                 })
        {
            await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(sql), Token));
        }

        var metadata = connection.Metadata;
        var user = fixture.Credential.UserName!.ToUpperInvariant();

        var schemas = await ToListAsync(metadata.GetObjectsAsync(null, DatabaseObjectKinds.Schema, Token));
        var schema = Assert.Single(schemas, s => s.Name.Name == user);
        Assert.DoesNotContain(schemas, static s => s.Name.Name is "SYS" or "SYSTEM");

        var objects = await ToListAsync(metadata.GetObjectsAsync(schema, DatabaseObjectKinds.All, Token));
        var table = Assert.Single(objects, static o => o is { Kind: DatabaseObjectKinds.Table, Name.Name: "Müşteri İşlemleri" });
        Assert.Equal(new DatabaseObjectName(null, user, "Müşteri İşlemleri"), table.Name);
        var view = Assert.Single(objects, static o => o is { Kind: DatabaseObjectKinds.View, Name.Name: "JQ_OZET" });
        var package = Assert.Single(objects, static o => o is { Kind: DatabaseObjectKinds.Package, Name.Name: "JQ_PKG" });
        var body = Assert.Single(objects, static o => o is { Kind: DatabaseObjectKinds.PackageBody, Name.Name: "JQ_PKG" });
        Assert.Single(objects, static o => o is { Kind: DatabaseObjectKinds.Sequence, Name.Name: "JQ_SEQ" });
        Assert.DoesNotContain(objects, static o => o.Name.Name.StartsWith("SYS_", StringComparison.Ordinal) || o.Name.Name.StartsWith("BIN$", StringComparison.Ordinal));
        Assert.Equal([package], await ToListAsync(metadata.GetObjectsAsync(schema, DatabaseObjectKinds.Package, Token)));

        var columns = await metadata.GetColumnsAsync(table.Name, Token);
        Assert.Equal(
            [
                new ColumnInfo("ID", "NUMBER(18)", false, 1, true),
                new ColumnInfo("AD", "VARCHAR2(50 CHAR)", false, 2),
                new ColumnInfo("TUTAR", "NUMBER(12,2)", true, 3),
                new ColumnInfo("NOTLAR", "CLOB", true, 4),
                new ColumnInfo("KAYIT", "TIMESTAMP(6)", true, 5),
            ],
            columns);

        // Without a schema the current one is used.
        Assert.Equal(5, (await metadata.GetColumnsAsync(new DatabaseObjectName(null, null, "Müşteri İşlemleri"), Token)).Count);
        Assert.Empty(await metadata.GetColumnsAsync(new DatabaseObjectName(null, user, "YOK"), Token));

        var trigger = Assert.Single(await ToListAsync(metadata.GetObjectsAsync(table, DatabaseObjectKinds.Trigger, Token)));
        Assert.Equal("JQ_TRG", trigger.Name.Name);

        var viewSource = await metadata.GetDefinitionAsync(view, Token);
        Assert.StartsWith($"CREATE OR REPLACE VIEW \"{user}\".\"JQ_OZET\" AS", viewSource, StringComparison.Ordinal);
        Assert.Contains("\"Müşteri İşlemleri\"", viewSource, StringComparison.Ordinal);

        Assert.StartsWith("CREATE OR REPLACE PACKAGE jq_pkg AS", await metadata.GetDefinitionAsync(package, Token), StringComparison.Ordinal);
        var bodySource = await metadata.GetDefinitionAsync(body, Token);
        Assert.StartsWith("CREATE OR REPLACE PACKAGE BODY jq_pkg AS", bodySource, StringComparison.Ordinal);
        Assert.Contains("RETURN 42;", bodySource, StringComparison.Ordinal);
        Assert.EndsWith("END jq_pkg;\n/\n", bodySource, StringComparison.Ordinal);

        // Oracle writes the table's real DDL.
        var ddl = await metadata.GetDefinitionAsync(table, Token);
        Assert.Contains("CREATE TABLE", ddl, StringComparison.Ordinal);
        Assert.Contains("\"Müşteri İşlemleri\"", ddl, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_connection_manager_tests_and_explores_an_oracle_connection()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        var credentials = new SessionCredentialStore();
        await using var manager = new ConnectionManager(new SingleProvider(new OracleProvider()), credentials, new TcpReachabilityProbe());
        await using var cache = new SqliteMetadataCache(new MetadataCacheOptions(), TimeProvider.System, NullLogger<SqliteMetadataCache>.Instance);
        var definition = Definition();

        var ok = await manager.TestAsync(definition, fixture.Credential.Password, Token);
        Assert.True(ok.Outcome == ConnectionOutcome.Succeeded, ok.Detail);

        var wrong = await manager.TestAsync(definition, "definitely-not-the-password", Token);
        Assert.Equal(ConnectionOutcome.Failed, wrong.Outcome);
        Assert.Contains("ORA-01017", wrong.Detail, StringComparison.Ordinal);

        var closed = definition with { Settings = new Dictionary<string, string>(definition.Settings) { ["port"] = "1" } };
        Assert.Equal(ConnectionOutcome.Unreachable, (await manager.TestAsync(closed, "x", Token)).Outcome);

        // The explorer's first level for Oracle is the schema list, read on the second connection while a
        // block sleeps on the first.
        var session = (await manager.OpenAsync(definition, fixture.Credential.Password, Token)).Session!;
        var metadata = new MetadataService(cache, TimeProvider.System);
        var slow = Task.Run(
            async () =>
            {
                await foreach (var item in session.ExecuteAsync(new QueryRequest("BEGIN DBMS_SESSION.SLEEP(4); END;"), Token))
                {
                    _ = item;
                }
            },
            Token);
        await Task.Delay(300, Token);
        var schemas = await metadata.GetObjectsAsync(session, null, DatabaseObjectKinds.Schema, refresh: false, Token);
        Assert.False(slow.IsCompleted);
        Assert.Contains(schemas, s => string.Equals(s.Name.Name, fixture.Credential.UserName, StringComparison.OrdinalIgnoreCase));
        await slow;

        // A read-only session refuses DDL before anything reaches the server.
        var readOnly = (await manager.OpenAsync(definition with { Id = Guid.NewGuid(), IsReadOnly = true }, fixture.Credential.Password, Token)).Session!;
        await Assert.ThrowsAsync<JetQueries.Core.Queries.ReadOnlyConnectionException>(async () =>
        {
            await foreach (var item in readOnly.ExecuteAsync(new QueryRequest("CREATE TABLE should_not_exist (id NUMBER)"), Token))
            {
                _ = item;
            }
        });
    }

    private ConnectionDefinition Definition() => new()
    {
        Id = Guid.NewGuid(),
        Name = "container",
        ProviderId = OracleProvider.ProviderId,
        Settings = fixture.Profile.Settings,
        UserName = fixture.Credential.UserName,
    };

    private async Task<IDatabaseConnection> OpenAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        return await new OracleProvider().OpenConnectionAsync(fixture.Profile, fixture.Credential, Token);
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    private sealed class SingleProvider(IDatabaseProvider provider) : IExtensionHost
    {
        public IReadOnlyList<ExtensionLoadResult> Extensions => [];

        public IReadOnlyCollection<IDatabaseProvider> DatabaseProviders => [provider];

        public Task<IReadOnlyList<ExtensionLoadResult>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Extensions);

        public IDatabaseProvider? FindDatabaseProvider(string id) => id == provider.Id ? provider : null;
    }
}
