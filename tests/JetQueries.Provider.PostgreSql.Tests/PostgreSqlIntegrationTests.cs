using System.Diagnostics;
using System.Globalization;
using JetQueries.Core.Connections;
using JetQueries.Core.Extensions;
using JetQueries.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace JetQueries.Provider.PostgreSql.Tests;

/// <summary>
/// Starts PostgreSQL in Docker once for all integration tests, from the image that is already on the
/// developer's machine. Opt-in (<c>JETQUERIES_POSTGRES_TESTS=Y</c>), like the other database tests, so that a
/// plain test run never downloads an image. Without it, or without Docker, the tests are skipped.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    public const string OptInVariable = "JETQUERIES_POSTGRES_TESTS";
    public const string Image = "postgres:16-alpine";

    private PostgreSqlContainer? _container;

    public string? SkipReason { get; private set; }

    public ConnectionProfile Profile { get; private set; } = null!;

    public ConnectionCredential Credential { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(OptInVariable), "Y", StringComparison.OrdinalIgnoreCase))
        {
            SkipReason = $"Set {OptInVariable}=Y to start a PostgreSQL container and run the integration tests.";
            return;
        }

        try
        {
            _container = new PostgreSqlBuilder(Image).Build();
            await _container.StartAsync();
        }
#pragma warning disable CA1031 // Any failure to start Docker means "skip", whatever the exception type.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SkipReason = $"Docker is not available: {ex.Message}";
            return;
        }

        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
        Credential = new ConnectionCredential(builder.Username, builder.Password);
        Profile = new ConnectionProfile(Guid.NewGuid(), "container", PostgreSqlProvider.ProviderId, new Dictionary<string, string>
        {
            ["host"] = _container.Hostname,
            ["port"] = _container.GetMappedPublicPort(5432).ToString(CultureInfo.InvariantCulture),
            ["database"] = builder.Database!,
            ["sslMode"] = "disable",
        });

        await using var connection = await new PostgreSqlProvider().OpenConnectionAsync(Profile, Credential, CancellationToken.None);
        foreach (var batch in new PostgreSqlProvider().SplitBatches(Schema))
        {
            await foreach (var _ in connection.Executor.ExecuteAsync(new QueryRequest(batch.Text), CancellationToken.None))
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private const string Schema = """
        CREATE SCHEMA kart;
        CREATE TYPE kart.card_state AS ENUM ('active', 'blocked', 'it''s closed');
        CREATE DOMAIN kart.amount AS numeric(12,2) CHECK (VALUE >= 0);
        CREATE TABLE kart.cards (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            card_no varchar(19) NOT NULL UNIQUE,
            "Balance" kart.amount DEFAULT 0,
            state kart.card_state NOT NULL DEFAULT 'active',
            issued_at timestamptz,
            tags text[]
        );
        CREATE INDEX ix_cards_issued ON kart.cards (issued_at DESC);
        CREATE TABLE kart."Müşteri İşlemleri" (id int PRIMARY KEY, card_id bigint REFERENCES kart.cards (id), "Açıklama" text);
        CREATE VIEW kart.card_summary AS SELECT card_no, "Balance" FROM kart.cards WHERE "Balance" > 0;
        CREATE SEQUENCE kart.batch_no START 100 INCREMENT 5;
        CREATE FUNCTION kart.balance_of(p_card bigint) RETURNS numeric LANGUAGE sql STABLE AS $$ SELECT "Balance" FROM kart.cards WHERE id = p_card $$;
        CREATE FUNCTION kart.balance_of(p_card_no text) RETURNS numeric LANGUAGE sql STABLE AS $$ SELECT "Balance" FROM kart.cards WHERE card_no = p_card_no $$;
        CREATE PROCEDURE kart.purge_old_logs(p_days int) LANGUAGE plpgsql AS $$
        BEGIN
            RAISE NOTICE 'purging older than % days', p_days;
        END;
        $$;
        INSERT INTO kart.cards (card_no, "Balance", issued_at, tags) VALUES
            ('5200 0001', 1250.50, '2026-01-15 10:30:00+03', ARRAY['gold', 'tr']),
            ('5200 0002', 0, NULL, NULL),
            ('Müşteri', 10, '2026-02-03 14:30:05.12+00', '{}');
        """;
}

[Trait("Category", "Integration")]
public sealed class PostgreSqlIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_query_streams_postgresql_values_the_grid_can_show()
    {
        await using var connection = await OpenAsync();
        const string Sql = """
            SELECT id, card_no, "Balance", state, issued_at, tags,
                   123456789012345678901234567890::numeric AS huge, interval '1 year 2 months 3 days 04:05:06' AS span,
                   '{"a": 1}'::jsonb AS doc, gen_random_uuid() IS NOT NULL AS has_uuid, '\x0a0b'::bytea AS raw
            FROM kart.cards ORDER BY id
            """;

        var events = await Collect(connection.Executor.ExecuteAsync(new QueryRequest(Sql), Token));

        var columns = Assert.Single(events.OfType<ResultSetStarted>()).Columns;
        Assert.Equal("timestamp with time zone", columns[4].DataType);
        var rows = events.OfType<RowBatch>().SelectMany(static b => b.Rows).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(1250.50m, rows[0][2]);
        Assert.Equal("active", rows[0][3]);
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 7, 30, 0, TimeSpan.Zero), rows[0][4]);
        Assert.Equal("{gold,tr}", rows[0][5]);
        Assert.Null(rows[1][5]);
        Assert.Equal("{}", rows[2][5]);
        Assert.Equal("Müşteri", rows[2][1]);
        Assert.Equal("123456789012345678901234567890", rows[0][6]);
        Assert.Equal("1 year 2 mons 3 days 04:05:06", rows[0][7]);
        Assert.Equal("{\"a\": 1}", rows[0][8]);
        Assert.Equal(true, rows[0][9]);
        Assert.Equal(new byte[] { 10, 11 }, rows[0][10]);
    }

    [Fact]
    public async Task Notices_arrive_as_messages_and_row_counts_only_for_data_changes()
    {
        await using var connection = await OpenAsync();

        var call = await Collect(connection.Executor.ExecuteAsync(new QueryRequest("CALL kart.purge_old_logs(30)"), Token));
        var notice = Assert.Single(call.OfType<QueryMessage>());
        Assert.Equal("purging older than 30 days", notice.Text);
        Assert.Equal(QueryMessageSeverity.Info, notice.Severity);

        var warning = await Collect(connection.Executor.ExecuteAsync(new QueryRequest("DO $$ BEGIN RAISE WARNING 'careful' USING HINT = 'look'; END $$"), Token));
        Assert.Equal("careful\nHint: look", Assert.Single(warning.OfType<QueryMessage>()).Text);
        Assert.Equal(QueryMessageSeverity.Warning, warning.OfType<QueryMessage>().Single().Severity);
        Assert.Null(Assert.Single(warning.OfType<StatementCompleted>()).RowsAffected);

        var update = await Collect(connection.Executor.ExecuteAsync(new QueryRequest("UPDATE kart.cards SET tags = tags WHERE \"Balance\" >= 0"), Token));
        Assert.Equal(3, Assert.Single(update.OfType<StatementCompleted>()).RowsAffected);
    }

    [Fact]
    public async Task An_error_says_where_it_is_in_the_statement()
    {
        await using var connection = await OpenAsync();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => Collect(connection.Executor.ExecuteAsync(new QueryRequest("SELECT id\nFROM kart.cardz"), Token)));
        Assert.Equal("ERROR 42P01: relation \"kart.cardz\" does not exist (statement line 2, column 6)", error.Message);

        var check = await Assert.ThrowsAnyAsync<Exception>(() => Collect(connection.Executor.ExecuteAsync(new QueryRequest("INSERT INTO kart.cards (card_no, \"Balance\") VALUES ('x', -1)"), Token)));
        Assert.StartsWith("ERROR 23514: value for domain kart.amount violates check constraint", check.Message, StringComparison.Ordinal);

        // The session is usable after the error.
        Assert.Single((await Collect(connection.Executor.ExecuteAsync(new QueryRequest("SELECT 1"), Token))).OfType<RowBatch>());
    }

    [Fact]
    public async Task Cancel_stops_a_running_statement_on_the_server()
    {
        await using var connection = await OpenAsync();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(connection.Executor.ExecuteAsync(new QueryRequest("SELECT pg_sleep(30)"), cancel.Token)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"cancel took {watch.Elapsed}");
        Assert.Single((await Collect(connection.Executor.ExecuteAsync(new QueryRequest("SELECT 1"), Token))).OfType<RowBatch>());
    }

    [Fact]
    public async Task The_explorer_sees_schemas_objects_and_columns()
    {
        await using var connection = await OpenAsync();
        var metadata = connection.Metadata;

        var schemas = await List(metadata.GetObjectsAsync(null, DatabaseObjectKinds.Schema, Token));
        Assert.Equal(["kart", "public"], schemas.Select(static s => s.Name.Name));

        var kart = schemas[0];
        var all = DatabaseObjectKinds.Table | DatabaseObjectKinds.View | DatabaseObjectKinds.Function | DatabaseObjectKinds.Procedure |
                  DatabaseObjectKinds.Sequence | DatabaseObjectKinds.Type;
        var objects = await List(metadata.GetObjectsAsync(kart, all, Token));
        Assert.Equal(
            [
                ("Müşteri İşlemleri", DatabaseObjectKinds.Table), ("batch_no", DatabaseObjectKinds.Sequence), ("card_summary", DatabaseObjectKinds.View),
                ("cards", DatabaseObjectKinds.Table), ("cards_id_seq", DatabaseObjectKinds.Sequence), ("balance_of", DatabaseObjectKinds.Function),
                ("purge_old_logs", DatabaseObjectKinds.Procedure), ("amount", DatabaseObjectKinds.Type), ("card_state", DatabaseObjectKinds.Type),
            ],
            objects.Select(static o => (o.Name.Name, o.Kind)));

        var columns = await metadata.GetColumnsAsync(new DatabaseObjectName(null, "kart", "cards"), Token);
        Assert.Equal(["id", "card_no", "Balance", "state", "issued_at", "tags"], columns.Select(static c => c.Name));
        Assert.Equal(["bigint", "character varying(19)", "kart.amount", "kart.card_state", "timestamp with time zone", "text[]"], columns.Select(static c => c.DataType));
        Assert.True(columns[0].IsPrimaryKey);
        Assert.False(columns[1].IsNullable);
        Assert.True(columns[4].IsNullable);

        // Without a schema the search path decides; kart is not on it.
        Assert.Empty(await metadata.GetColumnsAsync(new DatabaseObjectName(null, null, "cards"), Token));
        await Collect(connection.Executor.ExecuteAsync(new QueryRequest("SET search_path = kart, public"), Token));
        Assert.Equal(6, (await metadata.GetColumnsAsync(new DatabaseObjectName(null, null, "cards"), Token)).Count);
    }

    [Fact]
    public async Task Definitions_are_written_from_the_catalog()
    {
        await using var connection = await OpenAsync();
        var metadata = connection.Metadata;

        var table = await metadata.GetDefinitionAsync(new DatabaseObject(new DatabaseObjectName(null, "kart", "cards"), DatabaseObjectKinds.Table), Token);
        Assert.Equal(
            """
            CREATE TABLE "kart"."cards" (
                "id" bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
                "card_no" character varying(19) NOT NULL,
                "Balance" kart.amount DEFAULT 0,
                "state" kart.card_state DEFAULT 'active'::kart.card_state NOT NULL,
                "issued_at" timestamp with time zone,
                "tags" text[],
                CONSTRAINT "cards_pkey" PRIMARY KEY (id),
                CONSTRAINT "cards_card_no_key" UNIQUE (card_no)
            );

            CREATE INDEX ix_cards_issued ON kart.cards USING btree (issued_at DESC);

            """.ReplaceLineEndings("\n"),
            table);

        var view = await metadata.GetDefinitionAsync(new DatabaseObject(new DatabaseObjectName(null, "kart", "card_summary"), DatabaseObjectKinds.View), Token);
        Assert.StartsWith("CREATE OR REPLACE VIEW \"kart\".\"card_summary\" AS\nSELECT card_no,", view, StringComparison.Ordinal);

        var overloads = await metadata.GetDefinitionAsync(new DatabaseObject(new DatabaseObjectName(null, "kart", "balance_of"), DatabaseObjectKinds.Function), Token);
        Assert.Equal(2, overloads!.Split("CREATE OR REPLACE FUNCTION kart.balance_of(").Length - 1);

        Assert.Equal(
            "CREATE SEQUENCE \"kart\".\"batch_no\" AS bigint\n    START WITH 100 INCREMENT BY 5\n    MINVALUE 1 MAXVALUE 9223372036854775807\n    CACHE 1 NO CYCLE;\n",
            await metadata.GetDefinitionAsync(new DatabaseObject(new DatabaseObjectName(null, "kart", "batch_no"), DatabaseObjectKinds.Sequence), Token));
        Assert.Equal(
            "CREATE TYPE \"kart\".\"card_state\" AS ENUM ('active', 'blocked', 'it''s closed');\n",
            await metadata.GetDefinitionAsync(new DatabaseObject(new DatabaseObjectName(null, "kart", "card_state"), DatabaseObjectKinds.Type), Token));
        Assert.Equal(
            "CREATE DOMAIN \"kart\".\"amount\" AS numeric(12,2)\n    CONSTRAINT \"amount_check\" CHECK (VALUE >= 0::numeric);\n",
            await metadata.GetDefinitionAsync(new DatabaseObject(new DatabaseObjectName(null, "kart", "amount"), DatabaseObjectKinds.Type), Token));
        Assert.Null(await metadata.GetDefinitionAsync(new DatabaseObject(new DatabaseObjectName(null, "kart", "nothing"), DatabaseObjectKinds.Table), Token));
    }

    [Fact]
    public async Task Details_show_columns_keys_indexes_references_triggers_and_dependencies()
    {
        await using var connection = await OpenAsync();
        foreach (var sql in new[]
                 {
                     "DROP SCHEMA IF EXISTS det CASCADE",
                     "CREATE SCHEMA det",
                     "CREATE TABLE det.parent (id int PRIMARY KEY, code text NOT NULL, CONSTRAINT parent_code_key UNIQUE (code), CONSTRAINT parent_code_check CHECK (length(code) > 1))",
                     "COMMENT ON TABLE det.parent IS 'Parents'",
                     "CREATE TABLE det.child (id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, parent_id int REFERENCES det.parent (id) ON DELETE CASCADE, note text COLLATE \"C\", amount numeric(10,2) DEFAULT 0)",
                     "CREATE INDEX ix_child_note ON det.child (lower(note)) INCLUDE (amount) WHERE amount > 0",
                     "CREATE FUNCTION det.touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$",
                     "CREATE TRIGGER trg_child BEFORE INSERT OR UPDATE ON det.child FOR EACH ROW EXECUTE FUNCTION det.touch()",
                     "CREATE VIEW det.child_view AS SELECT id, note FROM det.child",
                     "CREATE SEQUENCE det.seq START 5 INCREMENT 2",
                     "CREATE FUNCTION det.add(a int, b int DEFAULT 1, OUT total int) LANGUAGE sql AS $$ SELECT a + b $$",
                 })
        {
            await Collect(connection.Executor.ExecuteAsync(new QueryRequest(sql), Token));
        }

        // The schema is dropped again: the explorer test lists every schema.
        try
        {
            var details = Assert.IsAssignableFrom<IObjectDetailsProvider>(connection.Metadata);
            static DatabaseObject Object(string name, DatabaseObjectKinds kind) => new(new DatabaseObjectName(null, "det", name), kind);

            var parent = await details.GetDetailsAsync(Object("parent", DatabaseObjectKinds.Table), Token);
            Assert.NotNull(parent);
            Assert.Contains(parent.Properties, static p => p is { Key: ObjectDetailKeys.Comment, Value: "Parents" });
            Assert.Contains(parent.Properties, static p => p.Key == ObjectDetailKeys.Size);
            Assert.Equal(
                [ObjectDetailKeys.Columns, ObjectDetailKeys.Constraints, ObjectDetailKeys.References, ObjectDetailKeys.Indexes],
                parent.Sections.Select(static s => s.Key));
            var constraints = Section(parent, ObjectDetailKeys.Constraints);
            Assert.Equal(["parent_pkey", "PRIMARY KEY", "id", "PRIMARY KEY (id)", "VALID"], constraints.Rows[0].Cells);
            Assert.Equal(["parent_code_check", "CHECK", "code", "CHECK (length(code) > 1)", "VALID"], constraints.Rows[2].Cells);
            var reference = Assert.Single(Section(parent, ObjectDetailKeys.References).Rows);
            Assert.Equal(["child_parent_id_fkey", "child", "parent_id", "id", "CASCADE", "NO ACTION", "VALID"], reference.Cells);
            Assert.Equal(Object("child", DatabaseObjectKinds.Table), reference.Target);

            var child = await details.GetDetailsAsync(Object("child", DatabaseObjectKinds.Table), Token);
            Assert.NotNull(child);
            var columns = Section(child, ObjectDetailKeys.Columns);
            Assert.Equal(["1", "id", "bigint", null, null, "GENERATED BY DEFAULT AS IDENTITY", "PK", null, null], columns.Rows[0].Cells);
            Assert.Equal("FK", Cell(columns, 1, ObjectDetailKeys.Key));
            Assert.Equal("C", Cell(columns, 2, ObjectDetailKeys.Collation));
            Assert.Equal("0", Cell(columns, 3, ObjectDetailKeys.Default));
            var foreignKey = Assert.Single(Section(child, ObjectDetailKeys.ForeignKeys).Rows);
            Assert.Equal(["child_parent_id_fkey", "parent_id", "parent", "id", "CASCADE", "NO ACTION", "VALID"], foreignKey.Cells);
            Assert.Equal(Object("parent", DatabaseObjectKinds.Table), foreignKey.Target);
            var indexes = Section(child, ObjectDetailKeys.Indexes);
            Assert.Equal(["child_pkey", "BTREE (PRIMARY KEY)", "✓", "id", null, null, "VALID"], indexes.Rows[0].Cells);
            Assert.Equal(["ix_child_note", "BTREE", null, "lower(note)", "amount", "amount > 0::numeric", "VALID"], indexes.Rows[1].Cells);
            var trigger = Assert.Single(Section(child, ObjectDetailKeys.Triggers).Rows);
            Assert.Equal(["trg_child", "INSERT OR UPDATE", "BEFORE EACH ROW", "ENABLED"], trigger.Cells);
            Assert.StartsWith("CREATE TRIGGER trg_child BEFORE INSERT OR UPDATE ON det.child", trigger.Source, StringComparison.Ordinal);
            var usedBy = Assert.Single(Section(child, ObjectDetailKeys.Dependencies).Rows);
            Assert.Equal(["Used by", "child_view", "VIEW"], usedBy.Cells);
            Assert.Equal(Object("child_view", DatabaseObjectKinds.View), usedBy.Target);

            var view = await details.GetDetailsAsync(Object("child_view", DatabaseObjectKinds.View), Token);
            Assert.NotNull(view);
            Assert.Equal(["id", "note"], Section(view, ObjectDetailKeys.Columns).Rows.Select(static r => r.Cells[1]));
            Assert.Equal(["Uses", "child", "TABLE"], Assert.Single(Section(view, ObjectDetailKeys.Dependencies).Rows).Cells);

            var sequence = await details.GetDetailsAsync(Object("seq", DatabaseObjectKinds.Sequence), Token);
            Assert.Contains(sequence!.Properties, static p => p is { Key: "start", Value: "5" });
            Assert.Contains(sequence.Properties, static p => p is { Key: "increment", Value: "2" });
            Assert.Empty(sequence.Sections);

            var add = await details.GetDetailsAsync(Object("add", DatabaseObjectKinds.Function), Token);
            Assert.NotNull(add);
            Assert.Contains(add.Properties, static p => p is { Key: "language", Value: "sql" });
            Assert.Contains(add.Properties, static p => p is { Key: "returns", Value: "integer" });
            var parameters = Section(add, ObjectDetailKeys.Parameters);
            Assert.Equal(["1", "a", "integer", "IN", null], parameters.Rows[0].Cells);
            Assert.Equal(["2", "b", "integer", "IN", "1"], parameters.Rows[1].Cells);
            Assert.Equal(["3", "total", "integer", "OUT", null], parameters.Rows[2].Cells);

            // Overloads are told apart by their signature.
            var overloads = await details.GetDetailsAsync(new DatabaseObject(new DatabaseObjectName(null, "kart", "balance_of"), DatabaseObjectKinds.Function), Token);
            var overloadRows = Section(overloads!, ObjectDetailKeys.Parameters).Rows;
            Assert.Equal(2, overloadRows.Count);
            Assert.Equal(["balance_of(p_card bigint)", "1", "p_card", "bigint", "IN", null], overloadRows[0].Cells);

            Assert.Null(await details.GetDetailsAsync(Object("nothing", DatabaseObjectKinds.Table), Token));
            Assert.Null(await details.GetDetailsAsync(Object("child_view", DatabaseObjectKinds.Table), Token));
            Assert.Null(await details.GetDetailsAsync(Object("nothing", DatabaseObjectKinds.Function), Token));
        }
        finally
        {
            await Collect(connection.Executor.ExecuteAsync(new QueryRequest("DROP SCHEMA det CASCADE"), Token));
        }
    }

    private static ObjectDetailSection Section(ObjectDetails details, string key) => Assert.Single(details.Sections, s => s.Key == key);

    private static string? Cell(ObjectDetailSection section, int row, string key) =>
        section.Rows[row].Cells[section.Columns.Select(static c => c.Key).ToList().IndexOf(key)];

    [Fact]
    public async Task A_script_runs_through_the_session_statement_by_statement()
    {
        if (fixture.SkipReason is { } reason)
        {
            Assert.Skip(reason);
        }

        var credentials = new SessionCredentialStore();
        var definition = new ConnectionDefinition
        {
            Id = Guid.NewGuid(),
            Name = "pg",
            ProviderId = PostgreSqlProvider.ProviderId,
            Settings = new Dictionary<string, string>(fixture.Profile.Settings),
            UserName = fixture.Credential.UserName,
        };
        await using var manager = new ConnectionManager(new SingleProvider(new PostgreSqlProvider()), credentials, new NoProbe());
        var attempt = await manager.OpenAsync(definition, fixture.Credential.Password, Token);
        Assert.Equal(ConnectionOutcome.Succeeded, attempt.Outcome);
        var session = attempt.Session!;

        const string Script = """
            CREATE TEMP TABLE t (id int);
            INSERT INTO t VALUES (1), (2);
            DO $$ BEGIN RAISE NOTICE 'two rows'; END $$;
            SELECT count(*) AS n FROM t;
            """;
        var events = await Collect(session.ExecuteAsync(new QueryRequest(Script), Token));

        Assert.Equal([null, 2L, null], events.OfType<StatementCompleted>().Select(static s => s.RowsAffected));
        Assert.Equal("two rows", Assert.Single(events.OfType<QueryMessage>()).Text);
        Assert.Equal(2L, events.OfType<RowBatch>().Single().Rows[0][0]);
    }

    private const string Numbers = "SELECT v.n, v.n % 3 AS m FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9)) v(n)";

    [Theory]
    [InlineData(Numbers + " ORDER BY n DESC", "m = 0", "9,6,3")]
    [InlineData(Numbers + " ORDER BY v.n DESC;", "m = 0", "9,6,3")]
    [InlineData(Numbers + " ORDER BY m, n DESC", "n > 6", "9,7,8")]
    [InlineData(Numbers + " ORDER BY n LIMIT 4", "n % 2 = 0", "2,4")]
    [InlineData("WITH RECURSIVE s(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM s WHERE n < 9) SELECT n, n % 3 AS m FROM s ORDER BY n DESC", "m = 0 AND 'İ' <> 'i'", "9,6,3")]
    [InlineData(Numbers + " ORDER BY n", "m = 0; DROP TABLE x", null)]
    [InlineData(Numbers + " ORDER BY n", "m = 0 -- the rest", null)]
    [InlineData(Numbers + " ORDER BY n", "(m = 0", null)]
    [InlineData(Numbers + " ORDER BY n", "m = 'never ends", null)]
    public async Task A_sql_filter_narrows_the_query_on_the_server_and_keeps_its_order(string sql, string condition, string? expected)
    {
        var filtered = new PostgreSqlProvider().BuildFilterQuery(sql, condition);
        if (expected is null)
        {
            Assert.Null(filtered);
            return;
        }

        Assert.NotNull(filtered);
        await using var connection = await OpenAsync();
        var events = await Collect(connection.Executor.ExecuteAsync(new QueryRequest(filtered), Token));
        Assert.Equal(expected, string.Join(",", events.OfType<RowBatch>().SelectMany(static b => b.Rows).Select(static r => Convert.ToInt64(r[0], System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public async Task A_transaction_is_begun_committed_and_rolled_back_on_request()
    {
        await using var connection = await OpenAsync();
        var control = Assert.IsAssignableFrom<ITransactionControl>(connection);
        await RunTx(connection, "CREATE TEMP TABLE tx (id int)");
        Assert.False(await control.HasOpenTransactionAsync(Token));

        // Rolled back: the row is gone.
        await control.BeginAsync(Token);
        await RunTx(connection, "INSERT INTO tx VALUES (1)");
        Assert.True(await control.HasOpenTransactionAsync(Token));
        await control.BeginAsync(Token);
        await control.RollbackAsync(Token);
        Assert.False(await control.HasOpenTransactionAsync(Token));
        Assert.Equal(0L, await CountTx(connection));

        // Committed: the row stays.
        await control.BeginAsync(Token);
        await RunTx(connection, "INSERT INTO tx VALUES (1)");
        await control.CommitAsync(Token);
        Assert.False(await control.HasOpenTransactionAsync(Token));
        Assert.Equal(1L, await CountTx(connection));

        // A transaction that failed stays open until it is rolled back.
        await control.BeginAsync(Token);
        await Assert.ThrowsAnyAsync<Exception>(() => RunTx(connection, "SELECT 1/0"));
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
        await foreach (var item in connection.Executor.ExecuteAsync(new QueryRequest("SELECT count(*) FROM tx"), Token))
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
        var provider = new PostgreSqlProvider();
        var sql = provider.SelectRows("pg_catalog.pg_database", ["datname"], 2);
        var events = await Collect(connection.Executor.ExecuteAsync(new QueryRequest(sql), Token));
        Assert.Equal(2, events.OfType<RowBatch>().Sum(static b => b.Rows.Count));
    }

    [Fact]
    public async Task Row_counts_and_an_early_closed_cursor_work_on_the_server()
    {
        await using var connection = await OpenAsync();
        var provider = new PostgreSqlProvider();
        var count = provider.BuildCountQuery("WITH c AS (SELECT * FROM kart.cards) SELECT * FROM c ORDER BY id;");
        Assert.NotNull(count);
        var rows = await Collect(connection.Executor.ExecuteAsync(new QueryRequest(count), Token));
        Assert.Equal(3L, rows.OfType<RowBatch>().Single().Rows[0][0]);
        Assert.Null(provider.BuildCountQuery("SELECT 1; SELECT 2"));
        Assert.Null(provider.BuildCountQuery("DO $$ BEGIN END $$"));

        using var cancel = new CancellationTokenSource();
        var events = connection.Executor.ExecuteAsync(new QueryRequest("SELECT g FROM generate_series(1, 5000000) g") { BatchSize = 200 }, cancel.Token)
            .GetAsyncEnumerator(cancel.Token);
        var fetched = 0;
        while (fetched < 200 && await events.MoveNextAsync())
        {
            fetched += events.Current is RowBatch batch ? batch.Rows.Count : 0;
        }

        var watch = Stopwatch.StartNew();
        await cancel.CancelAsync();
        try
        {
            await events.DisposeAsync();
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"closing took {watch.Elapsed}");
        Assert.Single((await Collect(connection.Executor.ExecuteAsync(new QueryRequest("SELECT 1"), Token))).OfType<RowBatch>());
    }

    /// <summary>
    /// After a page is read and the statement closed, the session holds no table lock (DDL goes through) and no
    /// snapshot (its backend_xmin is gone, so vacuum is not held back). For contrast, a reader left open blocks
    /// the DDL.
    /// </summary>
    [Fact]
    public async Task A_page_read_and_closed_leaves_no_lock_and_no_snapshot()
    {
        await using var setup = await OpenAsync();
        var table = "paging_" + Guid.NewGuid().ToString("N")[..8];
        await Collect(setup.Executor.ExecuteAsync(new QueryRequest($"CREATE TABLE {table} AS SELECT g AS id, repeat('x', 200) AS pad FROM generate_series(1, 300000) g"), Token));

        await using (var reader = await OpenAsync())
        {
            var pid = (int)(await Collect(reader.Executor.ExecuteAsync(new QueryRequest("SELECT pg_backend_pid()"), Token))).OfType<RowBatch>().Single().Rows[0][0]!;
            await ReadPageAsync(reader, $"SELECT * FROM {table}", close: true);

            await Collect(setup.Executor.ExecuteAsync(new QueryRequest($"SET lock_timeout = '2s'"), Token));
            await Collect(setup.Executor.ExecuteAsync(new QueryRequest($"ALTER TABLE {table} ADD COLUMN added1 int"), Token));
            var state = (await Collect(setup.Executor.ExecuteAsync(
                new QueryRequest($"SELECT state, backend_xmin IS NULL FROM pg_stat_activity WHERE pid = {pid}"), Token))).OfType<RowBatch>().Single().Rows[0];
            Assert.Equal("idle", state[0]);
            Assert.Equal(true, state[1]);
        }

        await using (var reader = await OpenAsync())
        {
            var open = await ReadPageAsync(reader, $"SELECT * FROM {table}", close: false);
            var error = await Assert.ThrowsAnyAsync<Exception>(async () => await Collect(setup.Executor.ExecuteAsync(new QueryRequest($"ALTER TABLE {table} ADD COLUMN added2 int"), Token)));
            Assert.Contains("55P03", error.Message, StringComparison.Ordinal);
            await open.DisposeAsync();
        }

        await Collect(setup.Executor.ExecuteAsync(new QueryRequest($"DROP TABLE {table}"), Token));
    }

    private sealed class OpenReader(CancellationTokenSource cancel, IAsyncEnumerator<QueryEvent> events) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await cancel.CancelAsync();
            try
            {
                await events.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
            }

            cancel.Dispose();
        }
    }

    private static async Task<OpenReader> ReadPageAsync(IDatabaseConnection connection, string sql, bool close)
    {
        var cancel = new CancellationTokenSource();
        var events = connection.Executor.ExecuteAsync(new QueryRequest(sql) { BatchSize = 200 }, cancel.Token).GetAsyncEnumerator(cancel.Token);
        var rows = 0;
        while (rows < 200 && await events.MoveNextAsync())
        {
            rows += events.Current is RowBatch batch ? batch.Rows.Count : 0;
        }

        Assert.Equal(200, rows);
        var reader = new OpenReader(cancel, events);
        if (close)
        {
            await reader.DisposeAsync();
        }

        return reader;
    }

    [Fact]
    public async Task Switching_database_reconnects_the_window_to_the_other_database()
    {
        await using var connection = await OpenAsync();
        var context = Assert.IsAssignableFrom<IDatabaseContext>(connection);
        var start = await context.GetCurrentAsync(Token);
        var databases = await context.GetAvailableAsync(Token);
        Assert.Contains("postgres", databases);
        Assert.Contains(start!, databases);

        await context.ChangeAsync("postgres", Token);
        Assert.Equal("postgres", await context.GetCurrentAsync(Token));
        var rows = (await Collect(connection.Executor.ExecuteAsync(new QueryRequest("SELECT current_database()"), Token))).OfType<RowBatch>().Single();
        Assert.Equal("postgres", rows.Rows[0][0]);

        // A database that does not exist: the window stays where it was.
        await Assert.ThrowsAnyAsync<Exception>(() => context.ChangeAsync("no_such_database_here", Token));
        Assert.Equal("postgres", await context.GetCurrentAsync(Token));
        Assert.Single((await Collect(connection.Executor.ExecuteAsync(new QueryRequest("SELECT 1"), Token))).OfType<RowBatch>());
    }

    private async Task<IDatabaseConnection> OpenAsync()
    {
        if (fixture.SkipReason is { } reason)
        {
            Assert.Skip(reason);
        }

        return await new PostgreSqlProvider().OpenConnectionAsync(fixture.Profile, fixture.Credential, Token);
    }

    private static async Task<List<QueryEvent>> Collect(IAsyncEnumerable<QueryEvent> events)
    {
        var list = new List<QueryEvent>();
        await foreach (var item in events)
        {
            list.Add(item);
        }

        return list;
    }

    private static async Task<List<DatabaseObject>> List(IAsyncEnumerable<DatabaseObject> objects)
    {
        var list = new List<DatabaseObject>();
        await foreach (var item in objects)
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

    private sealed class NoProbe : IReachabilityProbe
    {
        public Task<string?> ProbeAsync(NetworkEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
