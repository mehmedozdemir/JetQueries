using JetQueries.Sdk;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace JetQueries.Provider.SqlServer.Tests;

/// <summary>
/// Starts SQL Server in Docker once for all integration tests. Opt-in: the image is covered by Microsoft's
/// licence terms, so the container is only started when the developer has set
/// <c>JETQUERIES_MSSQL_ACCEPT_EULA=Y</c>. Without it, or without Docker, the tests are skipped.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string EulaVariable = "JETQUERIES_MSSQL_ACCEPT_EULA";

    private MsSqlContainer? _container;

    public string? SkipReason { get; private set; }

    public ConnectionProfile Profile { get; private set; } = null!;

    public ConnectionCredential Credential { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EulaVariable), "Y", StringComparison.OrdinalIgnoreCase))
        {
            SkipReason = $"Set {EulaVariable}=Y to accept the SQL Server container licence and run the integration tests.";
            return;
        }

        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();
        }
#pragma warning disable CA1031 // Any failure to start Docker means "skip", whatever the exception type.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SkipReason = $"Docker is not available: {ex.Message}";
            return;
        }

        var builder = new SqlConnectionStringBuilder(_container.GetConnectionString());
        Credential = new ConnectionCredential(builder.UserID, builder.Password);
        Profile = new ConnectionProfile(Guid.NewGuid(), "container", SqlServerProvider.ProviderId, new Dictionary<string, string>
        {
            ["server"] = builder.DataSource,

            // The container presents a self-signed certificate.
            ["trustServerCertificate"] = "true",
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
public sealed class SqlServerIntegrationTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rows_messages_and_counts_arrive_in_order()
    {
        await using var connection = await OpenAsync();
        const string Sql = """
            PRINT 'starting';
            CREATE TABLE #t (id int PRIMARY KEY, name nvarchar(50) NULL);
            INSERT INTO #t VALUES (1, N'Ayşe'), (2, NULL), (3, N'İlker');
            SELECT id, name FROM #t ORDER BY id;
            SELECT COUNT(*) AS n FROM #t;
            """;

        var events = await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(Sql) { BatchSize = 2 }, Token));

        Assert.Equal("starting", Assert.IsType<QueryMessage>(events[0]).Text);
        // One notification, for the INSERT; the two SELECTs report their rows through ResultSetCompleted only.
        Assert.Equal(new StatementCompleted(3), Assert.Single(events.OfType<StatementCompleted>()));

        var started = events.OfType<ResultSetStarted>().ToList();
        Assert.Equal(2, started.Count);
        Assert.Equal(["id", "name"], started[0].Columns.Select(static c => c.Name));
        Assert.Equal(typeof(int), started[0].Columns[0].ClrType);
        Assert.Equal("nvarchar", started[0].Columns[1].DataType);

        var first = events.IndexOf(started[0]);
        Assert.Equal(2, Assert.IsType<RowBatch>(events[first + 1]).Rows.Count);
        var lastRow = Assert.IsType<RowBatch>(events[first + 2]).Rows;
        Assert.Equal([3, "İlker"], Assert.Single(lastRow));
        Assert.Equal(new ResultSetCompleted(3, false), events[first + 3]);

        var rows = events.OfType<RowBatch>().SelectMany(static b => b.Rows).ToList();
        Assert.Null(rows[1][1]);
        Assert.Equal(3, rows[^1][0]);
    }

    [Fact]
    public async Task Parameters_are_sent_as_values()
    {
        await using var connection = await OpenAsync();
        var request = new QueryRequest("SELECT @text AS echoed, @missing AS nothing")
        {
            Parameters = [new QueryParameter("text", "'; DROP TABLE x; --"), new QueryParameter("missing", null)],
        };

        var row = (await ToListAsync(connection.Executor.ExecuteAsync(request, Token))).OfType<RowBatch>().Single().Rows.Single();

        Assert.Equal("'; DROP TABLE x; --", row[0]);
        Assert.Null(row[1]);
    }

    [Fact]
    public async Task Max_rows_truncates_a_result_set_and_later_ones_still_arrive()
    {
        await using var connection = await OpenAsync();
        var request = new QueryRequest("SELECT TOP (1000) object_id FROM sys.all_objects; SELECT 42 AS answer;") { MaxRows = 10 };

        var events = await ToListAsync(connection.Executor.ExecuteAsync(request, Token));

        Assert.Equal(new ResultSetCompleted(10, true), events.OfType<ResultSetCompleted>().First());
        Assert.Equal(42, events.OfType<RowBatch>().Last().Rows.Single()[0]);
    }

    [Fact]
    public async Task Cancel_stops_a_running_statement_quickly()
    {
        await using var connection = await OpenAsync();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(300));
        var started = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("WAITFOR DELAY '00:00:30'; SELECT 1;"), cancel.Token)));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "cancel took too long");

        // The connection is usable again afterwards.
        var events = await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("SELECT 1"), Token));
        Assert.Single(events.OfType<RowBatch>());
    }

    [Fact]
    public async Task Stopping_enumeration_early_does_not_read_the_rest()
    {
        await using var connection = await OpenAsync();
        var request = new QueryRequest("SELECT a.object_id FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c") { BatchSize = 100 };
        var started = DateTime.UtcNow;

        await foreach (var item in connection.Executor.ExecuteAsync(request, Token))
        {
            if (item is RowBatch)
            {
                break;
            }
        }

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "abandoning a large result took too long");
    }

    [Fact]
    public async Task Database_errors_surface_as_exceptions()
    {
        await using var connection = await OpenAsync();

        var error = await Assert.ThrowsAsync<SqlException>(
            () => ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("SELECT * FROM table_that_does_not_exist"), Token)));

        Assert.Equal(208, error.Number);
    }

    [Fact]
    public async Task Metadata_walks_databases_schemas_objects_columns_and_definitions()
    {
        await using var connection = await OpenAsync();
        var database = "jq_" + Guid.NewGuid().ToString("N")[..8];
        await RunAsync(connection, $"CREATE DATABASE [{database}];");
        await RunAsync(connection, $"""
            EXEC [{database}].sys.sp_executesql N'CREATE SCHEMA kart';
            EXEC [{database}].sys.sp_executesql N'CREATE TABLE kart.[Müşteri İşlemleri] (
                Id bigint NOT NULL PRIMARY KEY, Ad nvarchar(50) NOT NULL, Tutar decimal(18,2) NULL, Notlar varchar(max) NULL)';
            EXEC [{database}].sys.sp_executesql N'CREATE VIEW kart.Ozet AS SELECT Id, Ad FROM kart.[Müşteri İşlemleri]';
            EXEC [{database}].sys.sp_executesql N'CREATE PROCEDURE kart.Temizle AS BEGIN SET NOCOUNT ON; END';
            EXEC [{database}].sys.sp_executesql N'CREATE TRIGGER kart.Izle ON kart.[Müşteri İşlemleri] AFTER INSERT AS BEGIN SET NOCOUNT ON; END';
            """);
        var metadata = connection.Metadata;

        // The connection stays in master: everything below is read across databases.
        var databases = await ToListAsync(metadata.GetObjectsAsync(null, DatabaseObjectKinds.Database, Token));
        var db = Assert.Single(databases, d => d.Name.Name == database);
        Assert.Contains(databases, static d => d.Name.Name == "master");

        var schemas = await ToListAsync(metadata.GetObjectsAsync(db, DatabaseObjectKinds.Schema, Token));
        var schema = Assert.Single(schemas, static s => s.Name.Name == "kart");
        Assert.Equal(database, schema.Name.Database);
        Assert.DoesNotContain(schemas, static s => s.Name.Name is "sys" or "db_owner");

        var objects = await ToListAsync(metadata.GetObjectsAsync(schema, DatabaseObjectKinds.All, Token));
        var table = Assert.Single(objects, static o => o.Kind == DatabaseObjectKinds.Table);
        Assert.Equal(new DatabaseObjectName(database, "kart", "Müşteri İşlemleri"), table.Name);
        var view = Assert.Single(objects, static o => o.Kind == DatabaseObjectKinds.View);
        Assert.Single(objects, static o => o.Kind == DatabaseObjectKinds.Procedure);
        Assert.Equal([table], await ToListAsync(metadata.GetObjectsAsync(schema, DatabaseObjectKinds.Table, Token)));

        var columns = await metadata.GetColumnsAsync(table.Name, Token);
        Assert.Equal(
            [
                new ColumnInfo("Id", "bigint", false, 1, true),
                new ColumnInfo("Ad", "nvarchar(50)", false, 2),
                new ColumnInfo("Tutar", "decimal(18,2)", true, 3),
                new ColumnInfo("Notlar", "varchar(max)", true, 4),
            ],
            columns);
        Assert.Empty(await metadata.GetColumnsAsync(new DatabaseObjectName(database, "kart", "yok"), Token));

        var trigger = Assert.Single(await ToListAsync(metadata.GetObjectsAsync(table, DatabaseObjectKinds.Trigger, Token)));
        Assert.Equal("Izle", trigger.Name.Name);

        Assert.Contains("CREATE VIEW kart.Ozet", await metadata.GetDefinitionAsync(view, Token), StringComparison.Ordinal);
        // A table has no stored source: a script is written from the catalog and says what it leaves out.
        var script = await metadata.GetDefinitionAsync(table, Token);
        Assert.StartsWith("-- Written by JetQueries from the catalog", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [kart].[Müşteri İşlemleri]", script, StringComparison.Ordinal);
        Assert.Contains("    [Tutar] decimal(18,2) NULL,", script, StringComparison.Ordinal);
        Assert.Contains("    PRIMARY KEY ([Id])", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Object_details_list_columns_keys_references_indexes_triggers_and_dependencies()
    {
        await using var connection = await OpenAsync();
        var database = "jq_" + Guid.NewGuid().ToString("N")[..8];
        await RunAsync(connection, $"CREATE DATABASE [{database}];");
        await RunAsync(connection, $"""
            EXEC [{database}].sys.sp_executesql N'CREATE TABLE dbo.Kart (
                Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Kart PRIMARY KEY, No varchar(19) NOT NULL CONSTRAINT UQ_Kart_No UNIQUE,
                Bakiye decimal(12,2) NOT NULL CONSTRAINT DF_Kart_Bakiye DEFAULT (0) CONSTRAINT CK_Kart_Bakiye CHECK (Bakiye >= 0),
                Toplam AS (Bakiye * 2))';
            EXEC [{database}].sys.sp_executesql N'CREATE TABLE dbo.Hareket (
                Id int NOT NULL PRIMARY KEY, KartId int NOT NULL CONSTRAINT FK_Hareket_Kart REFERENCES dbo.Kart (Id) ON DELETE CASCADE, Tutar money NULL)';
            EXEC [{database}].sys.sp_executesql N'CREATE INDEX IX_Hareket_Kart ON dbo.Hareket (KartId DESC) INCLUDE (Tutar) WHERE Tutar IS NOT NULL';
            EXEC [{database}].sys.sp_executesql N'CREATE TRIGGER dbo.Kart_Izle ON dbo.Kart AFTER INSERT, UPDATE AS BEGIN SET NOCOUNT ON; END';
            EXEC [{database}].sys.sp_executesql N'CREATE VIEW dbo.KartOzet AS SELECT k.Id, k.No FROM dbo.Kart k';
            EXEC [{database}].sys.sp_executesql N'CREATE PROCEDURE dbo.KartBul @no varchar(19), @adet int = 5 OUTPUT AS SELECT * FROM dbo.Kart WHERE No = @no';
            EXEC [{database}].sys.sp_executesql N'CREATE SEQUENCE dbo.Sira START WITH 100 INCREMENT BY 5';
            """);
        var details = (IObjectDetailsProvider)connection.Metadata;
        var kart = new DatabaseObject(new DatabaseObjectName(database, "dbo", "Kart"), DatabaseObjectKinds.Table);

        var table = await details.GetDetailsAsync(kart, Token);
        Assert.NotNull(table);
        Assert.Equal("USER TABLE", table.Properties.Single(static p => p.Key == ObjectDetailKeys.ObjectType).Value);
        Assert.Equal("0", table.Properties.Single(static p => p.Key == ObjectDetailKeys.Rows).Value);
        ObjectDetailSection Section(ObjectDetails d, string key) => d.Sections.Single(s => s.Key == key);
        string?[] Row(ObjectDetailSection section, string first) => [.. section.Rows.Single(r => r.Cells[0] == first || r.Cells[1] == first).Cells];

        var columns = Section(table, ObjectDetailKeys.Columns);
        Assert.Equal("1|Id|int|||IDENTITY(1, 1)|PK||", string.Join('|', Row(columns, "Id")));
        Assert.Equal("((0))", Row(columns, "Bakiye")[4]);
        Assert.Equal("AS ([Bakiye]*(2))", Row(columns, "Toplam")[5]);

        var constraints = Section(table, ObjectDetailKeys.Constraints);
        Assert.Equal(["PK_Kart", "UQ_Kart_No", "CK_Kart_Bakiye", "DF_Kart_Bakiye"], constraints.Rows.Select(static r => r.Cells[0]));
        Assert.Equal("PRIMARY KEY", Row(constraints, "PK_Kart")[1]);
        Assert.Equal("([Bakiye]>=(0))", Row(constraints, "CK_Kart_Bakiye")[3]);

        var references = Section(table, ObjectDetailKeys.References);
        var reference = Assert.Single(references.Rows);
        Assert.Equal("FK_Hareket_Kart|dbo.Hareket|KartId|Id|CASCADE|NO ACTION", string.Join('|', reference.Cells));
        Assert.Equal(new DatabaseObject(new DatabaseObjectName(database, "dbo", "Hareket"), DatabaseObjectKinds.Table), reference.Target);
        Assert.DoesNotContain(table.Sections, static s => s.Key == ObjectDetailKeys.ForeignKeys);

        var trigger = Assert.Single(Section(table, ObjectDetailKeys.Triggers).Rows);
        Assert.Equal("Kart_Izle|AFTER|INSERT, UPDATE|enabled", string.Join('|', trigger.Cells));
        Assert.Contains("CREATE TRIGGER dbo.Kart_Izle", trigger.Source, StringComparison.Ordinal);
        Assert.Contains(Section(table, ObjectDetailKeys.Dependencies).Rows, static r => r.Cells[0] == "used by" && r.Cells[1] == "dbo.KartOzet");

        var hareket = await details.GetDetailsAsync(new DatabaseObject(new DatabaseObjectName(database, "dbo", "Hareket"), DatabaseObjectKinds.Table), Token);
        var foreignKey = Assert.Single(Section(hareket!, ObjectDetailKeys.ForeignKeys).Rows);
        Assert.Equal("FK_Hareket_Kart|KartId|dbo.Kart|Id|CASCADE|NO ACTION|", string.Join('|', foreignKey.Cells));
        Assert.Equal(kart, foreignKey.Target);
        var index = Row(Section(hareket!, ObjectDetailKeys.Indexes), "IX_Hareket_Kart");
        Assert.Equal("IX_Hareket_Kart|NONCLUSTERED||KartId DESC|Tutar|([Tutar] IS NOT NULL)|", string.Join('|', index));

        var procedure = await details.GetDetailsAsync(new DatabaseObject(new DatabaseObjectName(database, "dbo", "KartBul"), DatabaseObjectKinds.Procedure), Token);
        var parameters = Section(procedure!, ObjectDetailKeys.Parameters);
        Assert.Equal(["1|@no|varchar(19)|IN|", "2|@adet|int|OUT|"], parameters.Rows.Select(static r => string.Join('|', r.Cells)));
        Assert.Contains(Section(procedure!, ObjectDetailKeys.Dependencies).Rows, static r => r.Cells[0] == "uses" && r.Cells[1] == "dbo.Kart");

        // Signature help reads the same parameters.
        var signature = Assert.Single(await ((IRoutineMetadataProvider)connection.Metadata).GetSignaturesAsync(
            new DatabaseObject(new DatabaseObjectName(database, "dbo", "KartBul"), DatabaseObjectKinds.Procedure), Token));
        Assert.Equal(["@no varchar(19) ", "@adet int OUT"], signature.Parameters.Select(static p => $"{p.Name} {p.DataType} {p.Direction}"));
        Assert.Empty(await ((IRoutineMetadataProvider)connection.Metadata).GetSignaturesAsync(
            new DatabaseObject(new DatabaseObjectName(database, "dbo", "Kart"), DatabaseObjectKinds.Procedure), Token));

        var sequence = await details.GetDetailsAsync(new DatabaseObject(new DatabaseObjectName(database, "dbo", "Sira"), DatabaseObjectKinds.Sequence), Token);
        Assert.Equal("100", sequence!.Properties.Single(static p => p.Key == "start").Value);
        Assert.Equal("5", sequence.Properties.Single(static p => p.Key == "increment").Value);

        Assert.Null(await details.GetDetailsAsync(new DatabaseObject(new DatabaseObjectName(database, "dbo", "Yok"), DatabaseObjectKinds.Table), Token));

        // The explorer's ALTER of the view runs as written.
        var view = new DatabaseObject(new DatabaseObjectName(database, "dbo", "KartOzet"), DatabaseObjectKinds.View);
        var alter = new SqlServerProvider().ScriptAlter(view, (await connection.Metadata.GetDefinitionAsync(view, Token))!);
        Assert.StartsWith("ALTER VIEW dbo.KartOzet", alter, StringComparison.Ordinal);
        await RunAsync(connection, $"EXEC [{database}].sys.sp_executesql N'{alter!.Replace("'", "''", StringComparison.Ordinal)}'");
    }

    [Fact]
    public async Task An_open_cursor_on_a_large_result_closes_quickly_after_a_page()
    {
        await using var connection = await OpenAsync();
        using var cancel = new CancellationTokenSource();
        const string Sql = "SELECT TOP (2000000) a.object_id, b.name FROM sys.all_objects a CROSS JOIN sys.all_objects b";
        var events = connection.Executor.ExecuteAsync(new QueryRequest(Sql) { BatchSize = 200 }, cancel.Token).GetAsyncEnumerator(cancel.Token);
        var rows = 0;
        while (rows < 200 && await events.MoveNextAsync())
        {
            rows += events.Current is RowBatch batch ? batch.Rows.Count : 0;
        }

        Assert.Equal(200, rows);

        // What the query window does on Cancel: cancel first, then close; the rest is not read.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await cancel.CancelAsync();
        try
        {
            await events.DisposeAsync();
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"closing took {watch.Elapsed}");
        await RunAsync(connection, "SELECT 1");
    }

    /// <summary>
    /// What the query window relies on: after a page is read and the statement is closed (canceled, then
    /// disposed), the session holds nothing that blocks others — here, DDL on the same table. For contrast, a
    /// reader left open blocks the same DDL (the schema-stability lock), which is why the window never leaves one.
    /// </summary>
    [Fact]
    public async Task A_page_read_and_closed_leaves_no_lock_that_blocks_ddl()
    {
        await using var setup = await OpenAsync();
        var table = "paging_" + Guid.NewGuid().ToString("N")[..8];
        await RunAsync(setup, $"CREATE TABLE dbo.{table} (id int NOT NULL, pad char(200) NOT NULL DEFAULT 'x')");
        await RunAsync(setup, $"INSERT INTO dbo.{table} (id) SELECT TOP (300000) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) FROM sys.all_objects a CROSS JOIN sys.all_objects b");

        // The window's way: first page, then cancel and close.
        await using (var reader = await OpenAsync())
        {
            await ReadPageAsync(reader, $"SELECT * FROM dbo.{table}", close: true);
            await RunAsync(setup, $"SET LOCK_TIMEOUT 2000; ALTER TABLE dbo.{table} ADD added1 int NULL");
        }

        // A reader left open after its page: the same DDL waits for it and times out.
        await using (var reader = await OpenAsync())
        {
            var open = await ReadPageAsync(reader, $"SELECT * FROM dbo.{table}", close: false);
            var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(setup, $"SET LOCK_TIMEOUT 2000; ALTER TABLE dbo.{table} ADD added2 int NULL"));
            Assert.Contains("1222", error.ToString(), StringComparison.Ordinal);
            await open.DisposeAsync();
        }

        await RunAsync(setup, $"DROP TABLE dbo.{table}");
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

    [Theory]
    [InlineData("SELECT name FROM sys.databases ORDER BY name", true)]
    [InlineData("SELECT TOP (3) name FROM sys.databases ORDER BY name;", true)]
    [InlineData("-- comment ; here\nSELECT name FROM sys.databases WHERE name <> 'a;b'", true)]
    [InlineData("WITH d AS (SELECT name FROM sys.databases) SELECT * FROM d", false)]
    [InlineData("SELECT 1; SELECT 2", false)]
    [InlineData("SELECT name INTO #t FROM sys.databases", false)]
    [InlineData("EXEC sp_who", false)]
    public async Task Row_counts_are_computed_by_the_server(string sql, bool countable)
    {
        var provider = new SqlServerProvider();
        var count = provider.BuildCountQuery(sql.Replace("\\n", "\n", StringComparison.Ordinal));
        Assert.Equal(countable, count is not null);
        if (count is null)
        {
            return;
        }

        // The server is the judge: an ORDER BY left where SQL Server forbids it would fail here.
        await using var connection = await OpenAsync();
        var rows = await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(count), Token));
        Assert.True(Convert.ToInt64(rows.OfType<RowBatch>().Single().Rows[0][0], System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    private const string Numbers = "SELECT v.n, v.n % 3 AS m FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9)) v(n)";

    [Theory]
    [InlineData(Numbers + " ORDER BY n DESC", "m = 0", "9,6,3")]
    [InlineData(Numbers + " ORDER BY v.n DESC;", "m = 0", "9,6,3")]
    [InlineData(Numbers + " ORDER BY m, n DESC", "n > 6", "9,7,8")]
    [InlineData("SELECT TOP (4) v.n FROM (VALUES (1),(2),(3),(4),(5),(6)) v(n) ORDER BY v.n", "n % 2 = 0", "2,4")]
    [InlineData(Numbers + " ORDER BY v.n OFFSET 2 ROWS FETCH NEXT 3 ROWS ONLY", "n <> 4", "3,5")]
    [InlineData("-- numbers\n" + Numbers, "n IN (SELECT 2 UNION SELECT 8) AND CAST(m AS nvarchar(5)) <> N'İ'", "2,8")]
    [InlineData(Numbers + " ORDER BY n", "m = 0; DROP TABLE x", null)]
    [InlineData(Numbers + " ORDER BY n", "m = 0 -- the rest", null)]
    [InlineData(Numbers + " ORDER BY n", "(m = 0", null)]
    [InlineData(Numbers + " ORDER BY n", "m = 'never ends", null)]
    public async Task A_sql_filter_narrows_the_query_on_the_server_and_keeps_its_order(string sql, string condition, string? expected)
    {
        var filtered = new SqlServerProvider().BuildFilterQuery(sql.Replace("\\n", "\n", StringComparison.Ordinal), condition);
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
    public async Task A_formatted_query_returns_the_same_rows_on_the_server()
    {
        const string Sql = "with d as (select top (10) name, database_id from sys.databases order by database_id) select name, " +
            "case when database_id <= 4 then N'sistem' else N'kullanıcı' end as kind from d where name not like 'x%' " +
            "and database_id between 1 and 100 order by name";
        var formatted = ((ISqlFormatter)new SqlServerProvider().LanguageService!).Format(Sql, SqlFormatOptions.Default);
        Assert.Contains("\n", formatted, StringComparison.Ordinal);

        await using var connection = await OpenAsync();
        Assert.Equal(await RowsAsync(connection, Sql), await RowsAsync(connection, formatted));

        static async Task<string> RowsAsync(IDatabaseConnection connection, string sql) =>
            string.Join("|", (await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(sql), Token))).OfType<RowBatch>()
                .SelectMany(static b => b.Rows).Select(static r => string.Join(",", r)));
    }

    [Fact]
    public async Task A_transaction_is_begun_committed_and_rolled_back_on_request()
    {
        await using var connection = await OpenAsync();
        var control = Assert.IsAssignableFrom<ITransactionControl>(connection);
        await RunTx(connection, "CREATE TABLE #tx (id int)");
        Assert.False(await control.HasOpenTransactionAsync(Token));

        // Rolled back: the row is gone.
        await control.BeginAsync(Token);
        await RunTx(connection, "INSERT INTO #tx VALUES (1)");
        Assert.True(await control.HasOpenTransactionAsync(Token));
        await control.BeginAsync(Token);
        await control.RollbackAsync(Token);
        Assert.False(await control.HasOpenTransactionAsync(Token));
        Assert.Equal(0L, await CountTx(connection));

        // Committed: the row stays.
        await control.BeginAsync(Token);
        await RunTx(connection, "INSERT INTO #tx VALUES (1)");
        await control.CommitAsync(Token);
        Assert.False(await control.HasOpenTransactionAsync(Token));
        Assert.Equal(1L, await CountTx(connection));

        // A BEGIN TRAN typed by the user is seen too, and rolled back.
        await RunTx(connection, "BEGIN TRAN; INSERT INTO #tx VALUES (2)");
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
        await foreach (var item in connection.Executor.ExecuteAsync(new QueryRequest("SELECT COUNT(*) FROM #tx"), Token))
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
        var provider = new SqlServerProvider();
        var sql = provider.SelectRows("sys.databases", ["name", "database_id", "state", "[collation_name]"], 2);
        var events = await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(sql), Token));
        Assert.Equal(2, events.OfType<RowBatch>().Sum(static b => b.Rows.Count));
    }

    [Fact]
    public async Task The_current_database_can_be_read_listed_and_switched()
    {
        await using var connection = await OpenAsync();
        var context = Assert.IsAssignableFrom<IDatabaseContext>(connection);
        Assert.Equal(DatabaseContextKind.Database, context.Kind);

        var start = await context.GetCurrentAsync(Token);
        Assert.False(string.IsNullOrEmpty(start));
        var databases = await context.GetAvailableAsync(Token);
        Assert.Contains("master", databases);
        Assert.Contains("tempdb", databases);

        await context.ChangeAsync("tempdb", Token);
        Assert.Equal("tempdb", await context.GetCurrentAsync(Token));

        // A USE in a script is seen too.
        await RunAsync(connection, "USE master");
        Assert.Equal("master", await context.GetCurrentAsync(Token));
        await Assert.ThrowsAnyAsync<Exception>(() => context.ChangeAsync("no_such_database_here", Token));
        Assert.Equal("master", await context.GetCurrentAsync(Token));
    }

    [Fact]
    public async Task The_execute_script_of_a_procedure_and_a_function_runs()
    {
        await using var connection = await OpenAsync();
        foreach (var sql in new[]
                 {
                     "CREATE OR ALTER PROCEDURE dbo.jq_exec_test @a int, @name nvarchar(20) = N'x', @twice int OUTPUT AS BEGIN SET @twice = @a * 2 + LEN(@name); RETURN 7; END",
                     "CREATE OR ALTER FUNCTION dbo.jq_exec_fn(@a int, @b int = 10) RETURNS int AS BEGIN RETURN @a + @b; END",
                 })
        {
            await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(sql), Token));
        }

        var routines = (IRoutineMetadataProvider)connection.Metadata;
        var procedure = new DatabaseObject(new DatabaseObjectName(null, "dbo", "jq_exec_test"), DatabaseObjectKinds.Procedure);
        var script = new SqlServerProvider().ScriptExecute(
            procedure,
            Assert.Single(await routines.GetSignaturesAsync(procedure, Token)),
            [new RoutineArgument("@a", RoutineArgumentKind.Value, "20"), new RoutineArgument("@name", RoutineArgumentKind.Default)]);
        var row = (await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(script!), Token))).OfType<RowBatch>().Single().Rows[0];
        Assert.Equal([7, 41], row.Select(static v => Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)));

        var function = new DatabaseObject(new DatabaseObjectName(null, "dbo", "jq_exec_fn"), DatabaseObjectKinds.Function);
        var functionScript = new SqlServerProvider().ScriptExecute(
            function,
            Assert.Single(await routines.GetSignaturesAsync(function, Token)),
            [new RoutineArgument("@a", RoutineArgumentKind.Value, "1"), new RoutineArgument("@b", RoutineArgumentKind.Default)]);
        var result = (await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(functionScript!), Token))).OfType<RowBatch>().Single().Rows[0][0];
        Assert.Equal(11, Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_multi_row_insert_takes_typed_parameters()
    {
        await using var connection = await OpenAsync();
        await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("DROP TABLE IF EXISTS dbo.jq_import; CREATE TABLE dbo.jq_import (id int, ad nvarchar(40), tutar decimal(12,2), tarih datetime2, aktif bit)"), Token));
        var provider = new SqlServerProvider();
        var table = new DatabaseObjectName(null, "dbo", "jq_import");
        string[] columns = ["id", "ad", "tutar", "tarih", "aktif"];
        Assert.Equal(400, provider.RowsPerStatement(5));
        var sql = provider.ScriptInsert(table, columns, 2);
        QueryParameter[] parameters =
        [
            new("p0_0", 1L), new("p0_1", "İstanbul'da"), new("p0_2", 1250.5m), new("p0_3", new DateTime(2026, 1, 15)), new("p0_4", true),
            new("p1_0", 2L), new("p1_1", null), new("p1_2", null), new("p1_3", null), new("p1_4", false),
        ];
        await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(sql) { Parameters = parameters }, Token));
        var rows = (await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("SELECT ad, tutar, aktif FROM dbo.jq_import ORDER BY id"), Token))).OfType<RowBatch>().SelectMany(static b => b.Rows).ToList();
        Assert.Equal(["İstanbul'da", 1250.50m, true], rows[0]);
        Assert.Null(rows[1][0]);
    }

    [Fact]
    public async Task A_row_is_updated_and_deleted_by_its_key()
    {
        await using var connection = await OpenAsync();
        await RunAsync(connection, "DROP TABLE IF EXISTS dbo.jq_edit; CREATE TABLE dbo.jq_edit (id int NOT NULL, kod char(2) NOT NULL, ad nvarchar(40), PRIMARY KEY (id, kod)); INSERT dbo.jq_edit VALUES (1, 'TR', N'Bir'), (2, 'TR', N'İki')");
        var provider = new SqlServerProvider();
        var table = new DatabaseObjectName(null, "dbo", "jq_edit");

        var update = provider.ScriptUpdate(table, ["ad"], ["id", "kod"]);
        Assert.Equal("UPDATE [dbo].[jq_edit]\nSET [ad] = @s0\nWHERE [id] = @k0 AND [kod] = @k1", update);
        var updated = (await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(update) { Parameters = [new("s0", "Müşteri İşlemleri"), new("k0", 1), new("k1", "TR")] }, Token)))
            .OfType<StatementCompleted>().First().RowsAffected;
        var deleted = (await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(provider.ScriptDelete(table, ["id", "kod"])) { Parameters = [new("k0", 2), new("k1", "TR")] }, Token)))
            .OfType<StatementCompleted>().First().RowsAffected;

        Assert.Equal(1, updated);
        Assert.Equal(1, deleted);
        var rows = (await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("SELECT ad FROM dbo.jq_edit"), Token))).OfType<RowBatch>().SelectMany(static b => b.Rows).ToList();
        Assert.Equal("Müşteri İşlemleri", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task The_monitor_sees_who_blocks_whom_and_ends_the_blocker()
    {
        await using var holder = await OpenAsync();
        await using var waiter = await OpenAsync();
        await using var monitor = await OpenAsync();
        var activity = Assert.IsAssignableFrom<IActivityMonitor>(monitor);

        // The holder keeps a row locked in an open transaction; the waiter's SELECT waits for it.
        await RunAsync(holder, "CREATE TABLE ##jq_activity (id int PRIMARY KEY, n int); INSERT INTO ##jq_activity VALUES (1, 0);");
        await RunAsync(holder, "BEGIN TRANSACTION; UPDATE ##jq_activity SET n = 1 WHERE id = 1;");
        var holderId = await SpidAsync(holder);
        var waiterId = await SpidAsync(waiter);
        var waiting = RunAsync(waiter, "SELECT n FROM ##jq_activity WHERE id = 1");

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
        Assert.StartsWith("LCK_M_", blocked.WaitType, StringComparison.Ordinal);
        Assert.Contains("##jq_activity", blocked.Sql, StringComparison.Ordinal);
        Assert.Null(snapshot!.Limitation);
        Assert.Equal(await SpidAsync(monitor), snapshot.OwnSessionId);
        var blocker = snapshot.Sessions.Single(s => s.Id == holderId);
        Assert.Equal(1, blocker.OpenTransactions);
        Assert.True(blocker.IsUserSession);

        // Ending the holder rolls its transaction back and lets the waiter through.
        await activity.KillSessionAsync(holderId, Token);
        await waiting.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.DoesNotContain((await activity.GetActivityAsync(Token)).Sessions, s => s.Id == holderId && s.OpenTransactions > 0);
        await Assert.ThrowsAsync<ArgumentException>(() => activity.KillSessionAsync("1; DROP TABLE x", Token));
    }

    [Fact]
    public async Task The_advisor_finds_heavy_queries_missing_unused_and_duplicate_indexes_and_fragmentation()
    {
        await using var connection = await OpenAsync();
        var advisor = Assert.IsAssignableFrom<IPerformanceAdvisor>(connection);
        await RunAsync(connection, "IF DB_ID('JqPerf') IS NULL CREATE DATABASE JqPerf;");
        await RunAsync(connection, """
            USE JqPerf;
            IF OBJECT_ID('dbo.Orders') IS NOT NULL DROP TABLE dbo.Orders;
            CREATE TABLE dbo.Orders (Id int IDENTITY PRIMARY KEY, CustomerId int NOT NULL, Status int NOT NULL, Total decimal(12, 2) NOT NULL, Note nvarchar(100) NULL);
            INSERT INTO dbo.Orders (CustomerId, Status, Total, Note)
            SELECT TOP (20000) ABS(CHECKSUM(NEWID())) % 500, ABS(CHECKSUM(NEWID())) % 5, ABS(CHECKSUM(NEWID())) % 10000, N'not'
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            CREATE INDEX IX_Orders_Customer ON dbo.Orders (CustomerId);
            CREATE INDEX IX_Orders_Customer_Total ON dbo.Orders (CustomerId) INCLUDE (Total);
            CREATE INDEX IX_Orders_Status ON dbo.Orders (Status);
            IF OBJECT_ID('dbo.Scattered') IS NOT NULL DROP TABLE dbo.Scattered;
            CREATE TABLE dbo.Scattered (Id uniqueidentifier NOT NULL DEFAULT NEWID() PRIMARY KEY, Payload char(400) NOT NULL DEFAULT 'x');
            """);
        // Random keys in many small inserts: pages split all over the index.
        await RunAsync(connection, "USE JqPerf; DECLARE @i int = 0; WHILE @i < 400 BEGIN INSERT INTO dbo.Scattered (Payload) SELECT TOP (50) 'x' FROM sys.all_objects; SET @i += 1; END;");

        // A query the optimizer wants an index for, run a few times in the database; one that reads the duplicate index.
        await ((IDatabaseContext)connection).ChangeAsync("JqPerf", Token);
        for (var i = 0; i < 4; i++)
        {
            await RunAsync(connection, "SELECT Id, Note FROM dbo.Orders WHERE Total = 1234.00 AND Note = N'not';");
            await RunAsync(connection, "SELECT CustomerId, Total FROM dbo.Orders WHERE CustomerId = 42;");
        }

        var heavy = await advisor.GetHeavyQueriesAsync(new HeavyQueryRequest(HeavyQueryOrder.Executions, 50, "JqPerf"), Token);
        var wanted = Assert.Single(heavy.Queries, q => q.Sql.Contains("WHERE Total = 1234.00", StringComparison.Ordinal) || q.Sql.Contains("[Total]=@", StringComparison.Ordinal));
        Assert.Equal("JqPerf", wanted.Database);
        Assert.True(wanted.Executions >= 4);
        Assert.True(wanted.TotalReads > 0);
        Assert.NotNull(heavy.CountingSince);
        Assert.All(heavy.Queries, static q => Assert.Equal("JqPerf", q.Database));
        Assert.DoesNotContain(heavy.Queries, static q => q.Sql.Contains("dm_exec_query_stats", StringComparison.Ordinal));

        var missing = await advisor.GetMissingIndexesAsync("JqPerf", Token);
        var proposal = Assert.Single(missing, m => m.Table == "dbo.Orders" && m.EqualityColumns.Contains("Total"));
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_Orders_Total", proposal.CreateScript, StringComparison.Ordinal);
        Assert.Contains("ON [JqPerf].[dbo].[Orders] ([Total]", proposal.CreateScript, StringComparison.Ordinal);
        Assert.True(proposal.ImpactPercent > 0);

        var usage = await advisor.GetIndexUsageAsync("JqPerf", Token);
        var primary = Assert.Single(usage.Indexes, static i => i.Table == "dbo.Orders" && i.IsClustered);
        Assert.True(primary.IsConstraint);
        Assert.Null(primary.DropScript);
        var customer = Assert.Single(usage.Indexes, static i => i.Name == "IX_Orders_Customer");
        Assert.Equal(["CustomerId"], customer.KeyColumns);
        Assert.Equal("DROP INDEX [IX_Orders_Customer] ON [JqPerf].[dbo].[Orders];", customer.DropScript);
        var covering = Assert.Single(usage.Indexes, static i => i.Name == "IX_Orders_Customer_Total");
        Assert.Equal(["Total"], covering.IncludedColumns);
        Assert.True(usage.Indexes.Single(static i => i.Name == "IX_Orders_Customer_Total").Reads + customer.Reads > 0);
        Assert.Equal(0, usage.Indexes.Single(static i => i.Name == "IX_Orders_Status").Reads);
        Assert.True(covering.SizeKb > 0);
        Assert.Equal(20000, covering.Rows);

        var fragmentation = await advisor.GetFragmentationAsync("JqPerf", 10, Token);
        var scattered = Assert.Single(fragmentation, static f => f.Table == "dbo.Scattered");
        Assert.True(scattered.FragmentationPercent >= 30, scattered.FragmentationPercent.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(scattered.Rebuild);
        Assert.StartsWith("ALTER INDEX [PK__Scattere", scattered.MaintenanceScript, StringComparison.Ordinal);
        Assert.EndsWith("ON [JqPerf].[dbo].[Scattered] REBUILD;", scattered.MaintenanceScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_dashboard_reads_what_the_server_is_and_its_load()
    {
        await using var connection = await OpenAsync();
        var dashboard = Assert.IsAssignableFrom<IServerDashboard>(connection);

        var overview = await dashboard.GetOverviewAsync(Token);
        Assert.StartsWith("SQL Server 20", overview.Product, StringComparison.Ordinal);
        Assert.StartsWith("16.", overview.Version, StringComparison.Ordinal);
        Assert.NotNull(overview.Edition);
        Assert.True(overview.CpuCount > 0);
        Assert.True(overview.PhysicalMemoryMb > 0);
        Assert.True(overview.StartedAt < DateTimeOffset.Now);
        var master = Assert.Single(overview.Databases, static d => d.Name == "master");
        Assert.Equal("ONLINE", master.State);
        Assert.True(master.DataMb > 0 && master.LogMb > 0);
        Assert.True(master.BackupsKnown);
        Assert.Null(master.LastFullBackup);
        Assert.Contains(overview.Databases[^1].Name, (string[])["master", "model", "msdb", "tempdb"]);

        var first = await dashboard.GetSampleAsync(Token);
        await RunAsync(connection, "SELECT COUNT(*) FROM sys.all_objects a CROSS JOIN sys.all_objects b WHERE a.object_id > b.object_id;");
        var second = await dashboard.GetSampleAsync(Token);
        Assert.True(second.BatchRequests > first.BatchRequests);
        Assert.True(second.MemoryUsedMb > 0 && second.MemoryTargetMb > 0);
        Assert.True(second.AvailableMemoryMb > 0);
        Assert.True(second.Connections >= 1);
        Assert.True(second.PageLifeExpectancy >= 0);
        Assert.True(second.BytesRead > 0 && second.IoOperations > 0);
        Assert.NotEmpty(second.Waits);
        Assert.DoesNotContain(second.Waits, static w => w.Type is "LAZYWRITER_SLEEP" or "SLEEP_TASK" or "XE_TIMER_EVENT");
        Assert.Equal(0, second.BlockedRequests);
    }

    [Fact]
    public async Task The_health_check_finds_what_a_fresh_server_and_a_neglected_database_get_wrong()
    {
        await using var connection = await OpenAsync();
        var health = Assert.IsAssignableFrom<IHealthCheck>(connection);
        await RunAsync(connection, "IF DB_ID('JqHealth') IS NOT NULL BEGIN ALTER DATABASE JqHealth SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE JqHealth; END;");
        await RunAsync(connection, "CREATE DATABASE JqHealth;");
        await RunAsync(connection, """
            ALTER DATABASE JqHealth SET AUTO_SHRINK ON;
            ALTER DATABASE JqHealth SET PAGE_VERIFY TORN_PAGE_DETECTION;
            ALTER DATABASE JqHealth SET AUTO_UPDATE_STATISTICS OFF;
            ALTER DATABASE JqHealth SET RECOVERY FULL;
            ALTER DATABASE JqHealth MODIFY FILE (NAME = N'JqHealth', FILEGROWTH = 10%);
            """);

        var findings = await health.CheckAsync(Token);
        Assert.Equal(findings.Count, findings.Select(static f => f.RuleId).Distinct().Count());
        HealthFinding Rule(string id) => Assert.Single(findings, f => f.RuleId == id);

        // A container as it comes: memory unlimited, the default cost threshold, sa enabled, nothing backed up or checked.
        Assert.Equal(HealthSeverity.Warning, Rule("MaxServerMemory").Severity);
        Assert.Equal("2147483647", Rule("MaxServerMemory").Current);
        Assert.Contains("sp_configure N'max server memory (MB)'", Rule("MaxServerMemory").FixScript, StringComparison.Ordinal);
        Assert.Equal(HealthSeverity.Warning, Rule("CostThreshold").Severity);
        Assert.Equal(HealthSeverity.Warning, Rule("SaEnabled").Severity);
        Assert.Equal(HealthSeverity.Critical, Rule("FullBackup").Severity);
        Assert.Contains(Rule("FullBackup").Objects, static o => o.StartsWith("JqHealth (—)", StringComparison.Ordinal));
        Assert.DoesNotContain(Rule("FullBackup").Objects, static o => o.StartsWith("tempdb", StringComparison.Ordinal));
        Assert.Equal(HealthSeverity.Critical, Rule("LogBackup").Severity);
        Assert.Equal(HealthSeverity.Info, Rule("Uptime").Severity);
        Assert.Equal(HealthSeverity.Ok, Rule("PriorityBoost").Severity);
        Assert.Equal(HealthSeverity.Ok, Rule("SuspectPages").Severity);

        // The neglected database.
        Assert.Equal(["JqHealth"], Rule("AutoShrink").Objects);
        Assert.Equal("ALTER DATABASE [JqHealth] SET AUTO_SHRINK OFF;", Rule("AutoShrink").FixScript);
        Assert.Equal(["JqHealth (TORN_PAGE_DETECTION)"], Rule("PageVerify").Objects);
        Assert.Contains("JqHealth", Rule("AutoStats").Objects[0], StringComparison.Ordinal);
        Assert.Contains(Rule("FileGrowth").Objects, static o => o == "JqHealth · JqHealth (10 %)");
        Assert.Contains("MODIFY FILE (NAME = [JqHealth], FILEGROWTH = 512MB);", Rule("FileGrowth").FixScript, StringComparison.Ordinal);
        Assert.Equal(HealthSeverity.Ok, Rule("AutoClose").Severity);
    }

    [Fact]
    public async Task A_database_is_read_in_depth_without_changing_the_connections_database()
    {
        await using var connection = await OpenAsync();
        var details = Assert.IsAssignableFrom<IDatabaseDetails>(connection);
        await RunAsync(connection, "IF DB_ID('JqDetail') IS NOT NULL BEGIN ALTER DATABASE JqDetail SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE JqDetail; END;");
        await RunAsync(connection, "CREATE DATABASE JqDetail;");
        await RunAsync(connection, """
            ALTER DATABASE JqDetail SET RECOVERY FULL;
            ALTER DATABASE JqDetail SET AUTO_SHRINK ON;
            """);
        await RunAsync(connection, """
            USE JqDetail;
            CREATE TABLE dbo.Big (Id int IDENTITY PRIMARY KEY, Payload char(500) NOT NULL DEFAULT 'x', Code int NOT NULL DEFAULT 1);
            CREATE INDEX IX_Big_Code ON dbo.Big (Code);
            INSERT INTO dbo.Big (Payload) SELECT TOP (5000) 'x' FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            CREATE TABLE dbo.Small (Id int PRIMARY KEY);
            INSERT INTO dbo.Small VALUES (1), (2);
            USE master;
            """);
        await RunAsync(connection, "BACKUP DATABASE JqDetail TO DISK = N'/var/opt/mssql/data/JqDetail.bak' WITH INIT, COPY_ONLY;");

        var detail = await details.GetDatabaseDetailAsync("JqDetail", Token);
        Assert.Equal("ONLINE", detail.State);
        Assert.Equal("FULL", detail.RecoveryModel);
        Assert.True(detail.CompatibilityLevel >= 160);
        Assert.NotNull(detail.Collation);
        Assert.Equal("sa", detail.Owner);
        Assert.Equal("ON", detail.Options.Single(static o => o.Key == "autoShrink").Value);
        Assert.Equal("CHECKSUM", detail.Options.Single(static o => o.Key == "pageVerify").Value);
        Assert.True(detail.DataMb > 0 && detail.DataUsedMb > 0 && detail.LogMb > 0);
        Assert.InRange(detail.LogUsedPercent ?? -1, 0, 100);
        Assert.True(detail.VirtualLogFiles > 0);
        Assert.Equal(2, detail.TableCount);
        Assert.Equal(5002, detail.TotalRows);
        var big = detail.Tables[0];
        Assert.Equal("dbo.Big", big.Name);
        Assert.Equal(5000, big.Rows);
        Assert.True(big.DataMb > 2 && big.IndexMb > 0 && big.ReservedMb >= big.DataMb + big.IndexMb);
        Assert.Contains(detail.Files, static f => f.Type == "ROWS" && f.Filegroup == "PRIMARY" && f.UsedMb > 0);
        Assert.Contains(detail.Files, static f => f.Type == "LOG" && f.Filegroup is null);
        Assert.True(detail.BackupsKnown);
        var backup = Assert.Single(detail.Backups);
        Assert.Equal(BackupKind.Full, backup.Kind);
        Assert.True(backup.CopyOnly);
        Assert.EndsWith("JqDetail.bak", backup.Device, StringComparison.Ordinal);

        // The connection stays in its own database.
        Assert.Equal("master", await ((IDatabaseContext)connection).GetCurrentAsync(Token));
        await Assert.ThrowsAsync<ArgumentException>(() => details.GetDatabaseDetailAsync("NoSuchDatabase", Token));
    }

    private static async Task<string> SpidAsync(IDatabaseConnection connection) =>
        Convert.ToString((await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest("SELECT @@SPID"), Token))).OfType<RowBatch>().Single().Rows[0][0],
            System.Globalization.CultureInfo.InvariantCulture)!;

    private async Task<IDatabaseConnection> OpenAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        return await new SqlServerProvider().OpenConnectionAsync(fixture.Profile, fixture.Credential, Token);
    }

    private static async Task RunAsync(IDatabaseConnection connection, string sql) =>
        await ToListAsync(connection.Executor.ExecuteAsync(new QueryRequest(sql), Token));

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
