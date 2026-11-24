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
    public async Task Cancel_interrupts_a_statement_that_is_waiting()
    {
        await using var connection = await OpenAsync();
        var connectionString = OracleConnectionSettings.BuildConnectionString(fixture.Profile, fixture.Credential);

        // Without ALTER SYSTEM the server cannot be asked: said, not thrown.
        Assert.False(await OracleDatabaseConnection.CancelOnServerAsync(connectionString, "1", 1, Token));

        // The user gets the privilege (from SYSTEM, whose password the container shares).
        var system = new OracleConnection(OracleConnectionSettings.BuildConnectionString(fixture.Profile, new ConnectionCredential("SYSTEM", fixture.Credential.Password)));
        await using (system)
        {
            await system.OpenAsync(Token);
            var grant = system.CreateCommand();
            grant.CommandText = "GRANT ALTER SYSTEM TO " + fixture.Credential.UserName;
            await grant.ExecuteNonQueryAsync(Token);
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(500));
        var started = DateTime.UtcNow;

        // Waiting, not computing: as a row lock does. The break must reach the server through Docker's port
        // forwarding (as through NAT and VPN tunnels), which drops TCP urgent data.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("BEGIN DBMS_SESSION.SLEEP(20); END;"), cancel.Token)));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8), $"cancel took {(DateTime.UtcNow - started).TotalSeconds:N1} s");
        var events = await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("SELECT 1 FROM dual"), Token));
        Assert.Single(events.OfType<RowBatch>());

        // An UPDATE waiting on a row lock: canceled, and it never runs, also once the lock is released.
        await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("BEGIN EXECUTE IMMEDIATE 'DROP TABLE jq_lock'; EXCEPTION WHEN OTHERS THEN NULL; END;"), Token));
        await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("CREATE TABLE jq_lock (id NUMBER, v NUMBER)"), Token));
        await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("INSERT INTO jq_lock VALUES (1, 0)"), Token));
        await using var holder = await OpenAsync();
        var lockControl = (ITransactionControl)holder;
        await lockControl.BeginAsync(Token);
        await ToListAsync(holder.Executor.ExecuteAsync(new QueryRequest("UPDATE jq_lock SET v = 1 WHERE id = 1"), Token));

        using var cancelUpdate = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancelUpdate.CancelAfter(TimeSpan.FromMilliseconds(500));
        started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("UPDATE jq_lock SET v = 99 WHERE id = 1"), cancelUpdate.Token)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(8), "the update waiting on the lock was not canceled");

        await lockControl.RollbackAsync(Token);
        await Task.Delay(500, Token);
        var value = await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("SELECT v FROM jq_lock WHERE id = 1"), Token));
        Assert.Equal(0m, Convert.ToDecimal(value.OfType<RowBatch>().Single().Rows[0][0], System.Globalization.CultureInfo.InvariantCulture));
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
        // Other tests of this class create packages in the same schema too: only packages, this one among them.
        var packages = await ToListAsync(metadata.GetObjectsAsync(schema, DatabaseObjectKinds.Package, Token));
        Assert.Contains(package, packages);
        Assert.All(packages, static p => Assert.Equal(DatabaseObjectKinds.Package, p.Kind));

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
    public async Task Details_show_columns_keys_indexes_references_triggers_and_dependencies()
    {
        await using var connection = await OpenAsync();
        foreach (var sql in new[]
                 {
                     "CREATE TABLE jq_parent (id NUMBER(10) PRIMARY KEY, code VARCHAR2(20) NOT NULL, CONSTRAINT jq_parent_code_uk UNIQUE (code), CONSTRAINT jq_parent_code_ck CHECK (LENGTH(code) > 1))",
                     "COMMENT ON TABLE jq_parent IS 'Parents'",
                     "COMMENT ON COLUMN jq_parent.code IS 'Business code'",
                     "CREATE TABLE jq_child (id NUMBER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, parent_id NUMBER(10), note VARCHAR2(100 CHAR) DEFAULT 'x', twice NUMBER GENERATED ALWAYS AS (id * 2) VIRTUAL, CONSTRAINT jq_child_parent_fk FOREIGN KEY (parent_id) REFERENCES jq_parent (id) ON DELETE CASCADE)",
                     "CREATE INDEX jq_child_note_ix ON jq_child (note DESC, parent_id)",
                     "CREATE OR REPLACE TRIGGER jq_child_trg BEFORE INSERT OR UPDATE ON jq_child FOR EACH ROW BEGIN :new.note := UPPER(:new.note); END;",
                     "CREATE VIEW jq_child_v AS SELECT id, note FROM jq_child",
                     "CREATE SEQUENCE jq_det_seq START WITH 5 INCREMENT BY 2 MAXVALUE 1000 CYCLE",
                     "CREATE OR REPLACE FUNCTION jq_add(a IN NUMBER, b IN NUMBER DEFAULT 1) RETURN NUMBER IS BEGIN RETURN a + b; END;",
                     "CREATE OR REPLACE PACKAGE jq_det_pkg AS\n  PROCEDURE run(p_id IN NUMBER, p_out OUT VARCHAR2);\n  FUNCTION total RETURN NUMBER;\nEND jq_det_pkg;",
                     "BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER, 'JQ_PARENT'); END;",
                 })
        {
            await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(sql), Token));
        }

        var details = Assert.IsAssignableFrom<IObjectDetailsProvider>(connection.Metadata);
        var user = fixture.Credential.UserName!.ToUpperInvariant();
        DatabaseObject Object(string name, DatabaseObjectKinds kind) => new(new DatabaseObjectName(null, user, name), kind);

        // Without a schema, the current one.
        var parent = await details.GetDetailsAsync(new DatabaseObject(new DatabaseObjectName(null, null, "JQ_PARENT"), DatabaseObjectKinds.Table), Token);
        Assert.NotNull(parent);
        Assert.Equal(Object("JQ_PARENT", DatabaseObjectKinds.Table), parent.Target);
        Assert.Contains(parent.Properties, static p => p is { Key: ObjectDetailKeys.Comment, Value: "Parents" });
        Assert.Contains(parent.Properties, static p => p is { Key: ObjectDetailKeys.Rows, Value: "0" });
        Assert.Contains(parent.Properties, static p => p is { Key: ObjectDetailKeys.Status, Value: "VALID" });
        Assert.Contains(parent.Properties, static p => p.Key == ObjectDetailKeys.Created);
        Assert.Equal(
            [ObjectDetailKeys.Columns, ObjectDetailKeys.Constraints, ObjectDetailKeys.References, ObjectDetailKeys.Indexes],
            parent.Sections.Select(static s => s.Key));
        var parentColumns = Section(parent, ObjectDetailKeys.Columns);
        Assert.Equal(["1", "ID", "NUMBER(10)", null, null, null, "PK", null], parentColumns.Rows[0].Cells);
        Assert.Equal("Business code", Cell(parentColumns, 1, ObjectDetailKeys.Comment));
        var constraints = Section(parent, ObjectDetailKeys.Constraints);
        Assert.Equal(["PRIMARY KEY", "ID", null, "ENABLED"], constraints.Rows[0].Cells.Skip(1));
        Assert.Equal(["JQ_PARENT_CODE_UK", "UNIQUE", "CODE", null, "ENABLED"], constraints.Rows[1].Cells);
        Assert.Equal(["JQ_PARENT_CODE_CK", "CHECK", "CODE", "LENGTH(code) > 1", "ENABLED"], constraints.Rows[2].Cells);
        Assert.Equal(3, constraints.Rows.Count);
        var reference = Assert.Single(Section(parent, ObjectDetailKeys.References).Rows);
        Assert.Equal(["JQ_CHILD_PARENT_FK", "JQ_CHILD", "PARENT_ID", "ID", "CASCADE", "ENABLED"], reference.Cells);
        Assert.Equal(Object("JQ_CHILD", DatabaseObjectKinds.Table), reference.Target);

        // Read in steps: the columns come first; the returned value is the final state.
        var steps = new List<ObjectDetails>();
        var child = await Assert.IsAssignableFrom<IProgressiveObjectDetailsProvider>(details)
            .GetDetailsAsync(Object("JQ_CHILD", DatabaseObjectKinds.Table), new Recorder(steps), Token);
        Assert.NotNull(child);
        Assert.Equal([ObjectDetailKeys.Columns], steps[0].Sections.Select(static s => s.Key));
        Assert.True(steps.Count >= 3);
        Assert.Subset(child.Sections.Select(static s => s.Key).ToHashSet(), steps[^1].Sections.Select(static s => s.Key).ToHashSet());
        var columns = Section(child, ObjectDetailKeys.Columns);
        Assert.Equal(["1", "ID", "NUMBER", null, null, "IDENTITY", "PK", null], columns.Rows[0].Cells);
        Assert.Equal("FK", Cell(columns, 1, ObjectDetailKeys.Key));
        Assert.Equal("'x'", Cell(columns, 2, ObjectDetailKeys.Default));
        Assert.Equal("VARCHAR2(100 CHAR)", Cell(columns, 2, ObjectDetailKeys.DataType));
        Assert.Equal("VIRTUAL", Cell(columns, 3, ObjectDetailKeys.Identity));
        var foreignKey = Assert.Single(Section(child, ObjectDetailKeys.ForeignKeys).Rows);
        Assert.Equal(["JQ_CHILD_PARENT_FK", "PARENT_ID", "JQ_PARENT", "ID", "CASCADE", "ENABLED"], foreignKey.Cells);
        Assert.Equal(Object("JQ_PARENT", DatabaseObjectKinds.Table), foreignKey.Target);
        var index = Assert.Single(Section(child, ObjectDetailKeys.Indexes).Rows, static r => r.Cells[0] == "JQ_CHILD_NOTE_IX");
        Assert.Null(index.Cells[2]);
        Assert.Equal("VALID", index.Cells[5]);
        Assert.StartsWith("FUNCTION-BASED", index.Cells[1], StringComparison.Ordinal);
        var trigger = Assert.Single(Section(child, ObjectDetailKeys.Triggers).Rows);
        Assert.Equal(["JQ_CHILD_TRG", "INSERT OR UPDATE", "BEFORE EACH ROW", "ENABLED"], trigger.Cells);
        Assert.Contains("UPPER(:new.note)", trigger.Source, StringComparison.Ordinal);
        Assert.Equal(Object("JQ_CHILD_TRG", DatabaseObjectKinds.Trigger), trigger.Target);
        var dependencies = Section(child, ObjectDetailKeys.Dependencies);
        Assert.Contains(dependencies.Rows, r => r.Cells.SequenceEqual(["Used by", "JQ_CHILD_V", "VIEW"]) && r.Target == Object("JQ_CHILD_V", DatabaseObjectKinds.View));

        var view = await details.GetDetailsAsync(Object("JQ_CHILD_V", DatabaseObjectKinds.View), Token);
        Assert.NotNull(view);
        Assert.Equal(["ID", "NOTE"], Section(view, ObjectDetailKeys.Columns).Rows.Select(static r => r.Cells[1]));
        Assert.Contains(Section(view, ObjectDetailKeys.Dependencies).Rows, static r => r.Cells.SequenceEqual(["Uses", "JQ_CHILD", "TABLE"]));

        var sequence = await details.GetDetailsAsync(Object("JQ_DET_SEQ", DatabaseObjectKinds.Sequence), Token);
        Assert.Contains(sequence!.Properties, static p => p is { Key: "increment", Value: "2" });
        Assert.Contains(sequence.Properties, static p => p is { Key: "maxValue", Value: "1000" });
        Assert.Contains(sequence.Properties, static p => p is { Key: "cycle", Value: "YES" });

        var add = await details.GetDetailsAsync(Object("JQ_ADD", DatabaseObjectKinds.Function), Token);
        Assert.NotNull(add);
        var parameters = Section(add, ObjectDetailKeys.Parameters);
        Assert.Equal(["0", "RETURN", "NUMBER", "RETURN", null], parameters.Rows[0].Cells);
        Assert.Equal(["1", "A", "NUMBER", "IN", null], parameters.Rows[1].Cells);
        Assert.Equal(["2", "B", "NUMBER", "IN", "✓"], parameters.Rows[2].Cells);

        var package = await details.GetDetailsAsync(Object("JQ_DET_PKG", DatabaseObjectKinds.Package), Token);
        Assert.NotNull(package);
        var packageParameters = Section(package, ObjectDetailKeys.Parameters);
        Assert.Equal(["RUN", "1", "P_ID", "NUMBER", "IN", null], packageParameters.Rows[0].Cells);
        Assert.Equal(["RUN", "2", "P_OUT", "VARCHAR2", "OUT", null], packageParameters.Rows[1].Cells);
        Assert.Equal(["TOTAL", "0", "RETURN", "NUMBER", "RETURN", null], packageParameters.Rows[2].Cells);

        Assert.Null(await details.GetDetailsAsync(Object("NOTHING", DatabaseObjectKinds.Table), Token));
        Assert.Null(await details.GetDetailsAsync(Object("JQ_CHILD_V", DatabaseObjectKinds.Table), Token));
        Assert.NotNull(await details.GetDetailsAsync(new DatabaseObject(new DatabaseObjectName(null, null, user), DatabaseObjectKinds.Schema), Token));
    }

    [Fact]
    public async Task A_package_member_has_details_signatures_and_source_of_its_own()
    {
        await using var connection = await OpenAsync();
        foreach (var sql in new[]
                 {
                     "CREATE OR REPLACE PACKAGE jq_pay AS\n  -- pays one card\n  PROCEDURE pay(p_card IN NUMBER, p_amount IN NUMBER DEFAULT 0);\n  PROCEDURE pay(p_card IN VARCHAR2);\n  FUNCTION total RETURN NUMBER;\nEND jq_pay;",
                     "CREATE OR REPLACE PACKAGE BODY jq_pay AS\n  PROCEDURE pay(p_card IN NUMBER, p_amount IN NUMBER DEFAULT 0) IS\n  BEGIN\n    NULL;\n  END pay;\n  PROCEDURE pay(p_card IN VARCHAR2) IS\n  BEGIN\n    NULL;\n  END pay;\n  FUNCTION total RETURN NUMBER IS\n  BEGIN\n    RETURN 42;\n  END total;\nEND jq_pay;",
                 })
        {
            await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(sql), Token));
        }

        var user = fixture.Credential.UserName!.ToUpperInvariant();
        var package = new DatabaseObject(new DatabaseObjectName(null, user, "JQ_PAY"), DatabaseObjectKinds.Package);

        // The explorer's members carry their package.
        var members = await ToListAsync(connection.Metadata.GetObjectsAsync(package, DatabaseObjectKinds.Procedure | DatabaseObjectKinds.Function, Token));
        var pay = Assert.Single(members, static m => m.Name.Name == "PAY");
        Assert.Equal("JQ_PAY", pay.Name.Container);
        Assert.Equal($"{user}.JQ_PAY.PAY", pay.Name.ToString());
        var total = Assert.Single(members, static m => m.Name.Name == "TOTAL");
        Assert.Equal(DatabaseObjectKinds.Function, total.Kind);

        // Details: the package's owner and status, the routine's parameters with the overload.
        var details = await Assert.IsAssignableFrom<IObjectDetailsProvider>(connection.Metadata).GetDetailsAsync(pay, Token);
        Assert.NotNull(details);
        Assert.Equal("JQ_PAY", details.Target.Name.Container);
        Assert.Contains(details.Properties, static p => p is { Key: "package", Value: "JQ_PAY" });
        var parameters = Assert.Single(details.Sections, static s => s.Key == ObjectDetailKeys.Parameters);
        Assert.Equal("overload", parameters.Columns[0].Key);
        Assert.Equal(3, parameters.Rows.Count);
        Assert.Equal(["1", "1", "P_CARD", "NUMBER", "IN", null], parameters.Rows[0].Cells);
        Assert.Equal("✓", parameters.Rows[1].Cells[5]);
        Assert.Null(await ((IObjectDetailsProvider)connection.Metadata).GetDetailsAsync(pay with { Name = pay.Name with { Name = "NOPE" } }, Token));

        // Signatures: one per overload.
        Assert.Equal(2, (await ((IRoutineMetadataProvider)connection.Metadata).GetSignaturesAsync(pay, Token)).Count);

        // Source: the declarations from the specification and the bodies, nothing of the other routines.
        var source = await connection.Metadata.GetDefinitionAsync(pay, Token);
        Assert.NotNull(source);
        Assert.Contains($"-- PACKAGE {user}.JQ_PAY", source, StringComparison.Ordinal);
        Assert.Contains($"-- PACKAGE BODY {user}.JQ_PAY", source, StringComparison.Ordinal);
        Assert.Contains("PROCEDURE pay(p_card IN VARCHAR2);", source, StringComparison.Ordinal);
        Assert.Contains("END pay;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("total", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RETURN 42", await connection.Metadata.GetDefinitionAsync(total, Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_execute_script_of_a_package_procedure_and_a_function_runs()
    {
        await using var connection = await OpenAsync();
        foreach (var sql in new[]
                 {
                     "CREATE OR REPLACE PACKAGE jq_exec AS\n  PROCEDURE twice(p_a IN NUMBER, p_note IN VARCHAR2 DEFAULT 'x', p_out OUT NUMBER, p_when IN DATE DEFAULT NULL);\nEND jq_exec;",
                     "CREATE OR REPLACE PACKAGE BODY jq_exec AS\n  PROCEDURE twice(p_a IN NUMBER, p_note IN VARCHAR2 DEFAULT 'x', p_out OUT NUMBER, p_when IN DATE DEFAULT NULL) IS\n  BEGIN\n    p_out := p_a * 2 + LENGTH(p_note) + NVL(EXTRACT(DAY FROM p_when), 0);\n  END twice;\nEND jq_exec;",
                     "CREATE OR REPLACE FUNCTION jq_exec_fn(p_a IN NUMBER) RETURN VARCHAR2 IS BEGIN RETURN 'n=' || p_a; END;",
                 })
        {
            await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(sql), Token));
        }

        var provider = new OracleProvider();
        var routines = (IRoutineMetadataProvider)connection.Metadata;
        var user = fixture.Credential.UserName!.ToUpperInvariant();
        async Task<List<string>> RunAsync(DatabaseObject routine, IReadOnlyList<RoutineArgument> arguments)
        {
            var script = provider.ScriptExecute(routine, Assert.Single(await routines.GetSignaturesAsync(routine, Token)), arguments);
            Assert.NotNull(script);
            var messages = new List<string>();
            foreach (var batch in provider.SplitBatches(script))
            {
                messages.AddRange((await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(batch.Text), Token))).OfType<QueryMessage>().Select(static m => m.Text));
            }

            return messages;
        }

        var twice = new DatabaseObject(new DatabaseObjectName(null, user, "TWICE") { Container = "JQ_EXEC" }, DatabaseObjectKinds.Procedure);
        var output = await RunAsync(twice, [new RoutineArgument("P_A", RoutineArgumentKind.Value, "20"), new RoutineArgument("P_NOTE", RoutineArgumentKind.Default), new RoutineArgument("P_WHEN", RoutineArgumentKind.Value, "2026-01-05")]);
        Assert.Contains(output, static m => m.Contains("P_OUT = 46", StringComparison.Ordinal));

        var function = new DatabaseObject(new DatabaseObjectName(null, user, "JQ_EXEC_FN"), DatabaseObjectKinds.Function);
        Assert.Contains(await RunAsync(function, [new RoutineArgument("P_A", RoutineArgumentKind.Value, "5")]), static m => m.Contains("Result = n=5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tns_alias_from_a_tnsnames_file_connects()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        var folder = Path.Combine(AppContext.BaseDirectory, "tns-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var host = fixture.Profile.Settings["host"];
        var port = fixture.Profile.Settings["port"];
        await File.WriteAllTextAsync(
            Path.Combine(folder, "tnsnames.ora"),
            $"""
            # test
            JQ_TEST =
              (DESCRIPTION = (ADDRESS = (PROTOCOL = TCP)(HOST = {host})(PORT = {port}))
                (CONNECT_DATA = (SERVICE_NAME = FREEPDB1)))
            """,
            Token);
        var provider = new OracleProvider();
        var settings = new Dictionary<string, string> { ["connectionType"] = "tns", ["tnsAlias"] = "JQ_TEST", ["tnsAdmin"] = folder };

        Assert.Equal("JQ_TEST", Assert.Single(await provider.GetSuggestionsAsync("tnsAlias", settings, Token)).Value);
        await using var connection = await provider.OpenConnectionAsync(new ConnectionProfile(Guid.NewGuid(), "tns", OracleProvider.ProviderId, settings), fixture.Credential, Token);
        var rows = (await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("SELECT 42 AS n FROM dual"), Token))).OfType<RowBatch>().Single();
        Assert.Equal(42m, Convert.ToDecimal(rows.Rows[0][0], System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class Recorder(List<ObjectDetails> steps) : IProgress<ObjectDetails>
    {
        public void Report(ObjectDetails value) => steps.Add(value);
    }

    private static ObjectDetailSection Section(ObjectDetails details, string key) => Assert.Single(details.Sections, s => s.Key == key);

    private static string? Cell(ObjectDetailSection section, int row, string key) =>
        section.Rows[row].Cells[section.Columns.Select(static c => c.Key).ToList().IndexOf(key)];

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

    [Fact]
    public async Task Completion_and_navigation_read_the_real_dictionary()
    {
        await using var connection = await OpenAsync();
        foreach (var sql in new[]
                 {
                     "CREATE TABLE jq2_cards (id NUMBER(18) PRIMARY KEY, \"Açıklama\" VARCHAR2(40 CHAR), balance NUMBER(12,2))",
                     "CREATE OR REPLACE PACKAGE jq2_ops AS PROCEDURE open_card(p_id IN NUMBER); FUNCTION balance_of(p_id IN NUMBER) RETURN NUMBER; " +
                     "FUNCTION balance_of(p_no IN VARCHAR2) RETURN NUMBER; END jq2_ops;",
                 })
        {
            await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(sql), Token));
        }

        var service = new OracleProvider().LanguageService!;
        var metadata = connection.Metadata;

        var tables = await service.GetCompletionsAsync(new SqlDocument("SELECT * FROM jq2", metadata), 17, Token);
        Assert.Contains(tables, static t => t is { Label: "JQ2_CARDS", Kind: CompletionItemKind.Table });

        var columns = await service.GetCompletionsAsync(new SqlDocument("SELECT c. FROM jq2_cards c", metadata), 9, Token);
        Assert.Equal(["ID", "Açıklama", "BALANCE"], columns.Select(static c => c.Label));
        Assert.Equal("\"Açıklama\"", columns[1].InsertText);
        Assert.Equal("NUMBER(18) · PK", columns[0].Detail);

        // The overloaded function appears once; the procedure and the function are told apart.
        var members = await service.GetCompletionsAsync(new SqlDocument("BEGIN jq2_ops.", metadata), 14, Token);
        Assert.Equal(["BALANCE_OF", "OPEN_CARD"], members.Select(static m => m.Label));
        Assert.Equal(CompletionItemKind.Function, members[0].Kind);
        Assert.Equal(CompletionItemKind.Procedure, members[1].Kind);

        var definition = await service.GetDefinitionAsync(new SqlDocument("BEGIN jq2_ops.open_card(1); END;", metadata), 16, Token);
        Assert.Equal(DatabaseObjectKinds.Package, definition!.Target!.Kind);
        Assert.Equal("OPEN_CARD", definition.MemberName);
        Assert.Contains("PROCEDURE open_card(p_id IN NUMBER);", await metadata.GetDefinitionAsync(definition.Target, Token), StringComparison.Ordinal);

        // Unqualified names resolve in the current schema; with the schema written out they resolve the same way.
        var user = fixture.Credential.UserName!.ToUpperInvariant();
        var qualified = await service.GetCompletionsAsync(new SqlDocument($"SELECT * FROM {user}.jq2", metadata), 15 + user.Length + 3, Token);
        Assert.Contains(qualified, static t => t.Label == "JQ2_CARDS");
    }

    [Fact]
    public async Task Signatures_of_routines_and_package_members_come_from_the_dictionary()
    {
        await using var connection = await OpenAsync();
        foreach (var sql in new[]
                 {
                     "CREATE OR REPLACE FUNCTION jq3_add(a IN NUMBER, b IN NUMBER DEFAULT 1) RETURN NUMBER IS BEGIN RETURN a + b; END;",
                     "CREATE OR REPLACE PROCEDURE jq3_ping IS BEGIN NULL; END;",
                     "CREATE OR REPLACE PACKAGE jq3_ops AS PROCEDURE open_card(p_id IN NUMBER, p_no OUT VARCHAR2, p_log IN OUT CLOB); " +
                     "FUNCTION balance_of(p_id IN NUMBER) RETURN NUMBER; FUNCTION balance_of(p_no IN VARCHAR2, p_at IN DATE DEFAULT SYSDATE) RETURN NUMBER; END jq3_ops;",
                 })
        {
            await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(sql), Token));
        }

        var routines = Assert.IsAssignableFrom<IRoutineMetadataProvider>(connection.Metadata);
        var user = fixture.Credential.UserName!.ToUpperInvariant();
        Task<IReadOnlyList<SqlSignature>> Signatures(string? schema, string name) =>
            routines.GetSignaturesAsync(new DatabaseObject(new DatabaseObjectName(null, schema, name), DatabaseObjectKinds.Function), Token);

        // A standalone function, in the current schema and with the schema written out.
        var add = Assert.Single(await Signatures(null, "JQ3_ADD"));
        Assert.Equal("JQ3_ADD", add.Name);
        Assert.Equal("NUMBER", add.ReturnType);
        Assert.Equal(["A", "B"], add.Parameters.Select(static p => p.Name));
        Assert.False(add.Parameters[0].IsOptional);
        Assert.True(add.Parameters[1].IsOptional);
        Assert.Single(await Signatures(user, "JQ3_ADD"));

        // A procedure without parameters still has a signature.
        var ping = Assert.Single(await Signatures(null, "JQ3_PING"));
        Assert.Empty(ping.Parameters);
        Assert.Null(ping.ReturnType);

        // pkg.member( in the editor: the "schema" is the package. Overloads each get a signature.
        var open = Assert.Single(await Signatures("JQ3_OPS", "OPEN_CARD"));
        Assert.Equal("JQ3_OPS.OPEN_CARD", open.Name);
        Assert.Equal([null, "OUT", "IN OUT"], open.Parameters.Select(static p => p.Direction));
        Assert.Equal(["NUMBER", "VARCHAR2", "CLOB"], open.Parameters.Select(static p => p.DataType));

        var balance = await Signatures(user, "JQ3_OPS.BALANCE_OF");
        Assert.Equal(2, balance.Count);
        Assert.All(balance, static s => Assert.Equal("NUMBER", s.ReturnType));
        Assert.Equal(["P_NO", "P_AT"], balance[1].Parameters.Select(static p => p.Name));
        Assert.True(balance[1].Parameters[1].IsOptional);

        Assert.Empty(await Signatures(null, "JQ3_MISSING"));

        // Through the language service: the help for a package member call.
        var service = (ISignatureHelpProvider)new OracleProvider().LanguageService!;
        const string Text = "BEGIN jq3_ops.open_card(1, ";
        var help = await service.GetSignatureHelpAsync(new SqlDocument(Text, connection.Metadata), Text.Length, Token);
        Assert.NotNull(help);
        Assert.Equal(1, help.ActiveParameter);
        Assert.Equal("P_NO", help.Signatures[0].Parameters[1].Name);
    }

    private ConnectionDefinition Definition() => new()
    {
        Id = Guid.NewGuid(),
        Name = "container",
        ProviderId = OracleProvider.ProviderId,
        Settings = fixture.Profile.Settings,
        UserName = fixture.Credential.UserName,
    };

    [Fact]
    public async Task The_current_schema_can_be_read_listed_and_switched()
    {
        await using var connection = await OpenAsync();
        var context = Assert.IsAssignableFrom<IDatabaseContext>(connection);
        Assert.Equal(DatabaseContextKind.Schema, context.Kind);

        var user = await context.GetCurrentAsync(Token);
        Assert.Equal(fixture.Credential.UserName!.ToUpperInvariant(), user);
        Assert.Contains(user!, await context.GetAvailableAsync(Token));

        // SYS is not offered, but any schema can be made current; names go through the quoting helper.
        await context.ChangeAsync("SYS", Token);
        Assert.Equal("SYS", await context.GetCurrentAsync(Token));
        await Assert.ThrowsAnyAsync<Exception>(() => context.ChangeAsync("NO_SUCH_SCHEMA_HERE", Token));
        Assert.Equal("SYS", await context.GetCurrentAsync(Token));
        await context.ChangeAsync(user!, Token);
        Assert.Equal(user, await context.GetCurrentAsync(Token));
    }

    private const string Numbers = "SELECT LEVEL n, MOD(LEVEL, 3) m FROM dual CONNECT BY LEVEL <= 9";

    [Theory]
    [InlineData(Numbers + " ORDER BY n DESC", "m = 0", "9,6,3")]
    [InlineData("SELECT t.n, t.m FROM (" + Numbers + ") t ORDER BY t.n DESC;", "m = 0", "9,6,3")]
    [InlineData(Numbers + " ORDER BY m, n DESC\n/", "n > 6", "9,7,8")]
    [InlineData(Numbers + " ORDER BY n FETCH FIRST 4 ROWS ONLY", "MOD(n, 2) = 0", "2,4")]
    [InlineData(Numbers + " ORDER BY n", "m = 0; DROP TABLE x", null)]
    [InlineData(Numbers + " ORDER BY n", "m = 0 -- the rest", null)]
    [InlineData(Numbers + " ORDER BY n", "(m = 0", null)]
    [InlineData(Numbers + " ORDER BY n", "m = 'never ends", null)]
    public async Task A_sql_filter_narrows_the_query_on_the_server_and_keeps_its_order(string sql, string condition, string? expected)
    {
        var filtered = new OracleProvider().BuildFilterQuery(sql.Replace("\\n", "\n", StringComparison.Ordinal), condition);
        if (expected is null)
        {
            Assert.Null(filtered);
            return;
        }

        Assert.NotNull(filtered);
        await using var connection = await OpenAsync();
        var events = await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(filtered), Token));
        Assert.Equal(expected, string.Join(",", events.OfType<RowBatch>().SelectMany(static b => b.Rows).Select(static r => Convert.ToInt64(r[0], System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public async Task A_transaction_is_begun_committed_and_rolled_back_on_request()
    {
        await using var connection = await OpenAsync();
        var control = Assert.IsAssignableFrom<ITransactionControl>(connection);
        await RunTx(connection, "BEGIN EXECUTE IMMEDIATE 'DROP TABLE jq_tx'; EXCEPTION WHEN OTHERS THEN NULL; END;");
        await RunTx(connection, "CREATE TABLE jq_tx (id NUMBER)");
        Assert.False(await control.HasOpenTransactionAsync(Token));

        // Rolled back: the row is gone.
        await control.BeginAsync(Token);
        await RunTx(connection, "INSERT INTO jq_tx VALUES (1)");
        Assert.True(await control.HasOpenTransactionAsync(Token));
        await control.BeginAsync(Token);
        await control.RollbackAsync(Token);
        Assert.False(await control.HasOpenTransactionAsync(Token));
        Assert.Equal(0L, await CountTx(connection));

        // Committed: the row stays.
        await control.BeginAsync(Token);
        await RunTx(connection, "INSERT INTO jq_tx VALUES (1)");
        await control.CommitAsync(Token);
        Assert.False(await control.HasOpenTransactionAsync(Token));
        Assert.Equal(1L, await CountTx(connection));

        // Begun but nothing changed yet: Oracle has no transaction to speak of.
        await control.BeginAsync(Token);
        Assert.False(await control.HasOpenTransactionAsync(Token));
        await RunTx(connection, "UPDATE jq_tx SET id = 2");
        Assert.True(await control.HasOpenTransactionAsync(Token));
        await control.RollbackAsync(Token);
        Assert.False(await control.HasOpenTransactionAsync(Token));
    }

    private static async Task RunTx(IDatabaseConnection connection, string sql)
    {
        await foreach (var _ in connection.Executor.ExecuteAsync(new QueryRequest(sql), Token))
        {
        }
    }

    private static async Task<long> CountTx(IDatabaseConnection connection)
    {
        await foreach (var item in connection.Executor.ExecuteAsync(new QueryRequest("SELECT COUNT(*) FROM jq_tx"), Token))
        {
            if (item is RowBatch { Rows: [var row, ..] })
            {
                return Convert.ToInt64(row[0], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return -1;
    }

    [Fact]
    public async Task The_first_rows_select_runs_in_the_dialect()
    {
        await using var connection = await OpenAsync();
        var provider = new OracleProvider();
        var sql = provider.SelectRows("all_users", ["username", "user_id", "created", "common"], 2);
        var events = await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(sql), Token));
        Assert.Equal(2, events.OfType<RowBatch>().Sum(static b => b.Rows.Count));
    }

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
