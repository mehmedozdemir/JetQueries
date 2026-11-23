using System.Diagnostics;
using JetQueries.Sdk;

namespace JetQueries.Provider.Sqlite.Tests;

/// <summary>Against real database files: SQLite needs no server, so these always run.</summary>
public sealed class SqliteDatabaseTests : IAsyncLifetime
{
    private const string Schema = """
        CREATE TABLE cards (
            id INTEGER PRIMARY KEY,
            card_no TEXT NOT NULL UNIQUE,
            balance NUMERIC(12,2),
            issued_at TEXT
        );
        CREATE TABLE "Müşteri İşlemleri" (id INTEGER PRIMARY KEY, card_id INTEGER REFERENCES cards(id), "Açıklama" TEXT, total REAL GENERATED ALWAYS AS (id * 2) VIRTUAL);
        CREATE INDEX ix_cards_issued ON cards (issued_at);
        CREATE VIEW card_summary AS SELECT card_no, balance FROM cards WHERE balance > 0;
        CREATE TRIGGER trg_cards AFTER INSERT ON cards BEGIN
            UPDATE cards SET balance = COALESCE(new.balance, 0) WHERE id = new.id;
        END;
        INSERT INTO cards (card_no, balance, issued_at) VALUES ('5200 0001', 1250.5, '2026-01-15'), ('5200 0002', NULL, '2026-02-03'), ('Müşteri', 0, NULL);
        """;

    private readonly string _path = Path.Combine(SqliteProviderTests.NewDirectory(), "cards.db");
    private IDatabaseConnection _connection = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connection = await new SqliteProvider().OpenConnectionAsync(SqliteProviderTests.Profile(new() { ["file"] = _path, ["mode"] = "create" }), null, Token);
        await RunScriptAsync(Schema);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task A_query_streams_columns_and_rows_with_sqlite_values()
    {
        var events = await RunAsync("SELECT id, card_no, balance, issued_at, x'0A0B' AS raw, 1.5 * 2 AS n FROM cards ORDER BY id");

        var started = Assert.Single(events.OfType<ResultSetStarted>());
        Assert.Equal(["id", "card_no", "balance", "issued_at", "raw", "n"], started.Columns.Select(static c => c.Name));
        Assert.Equal("INTEGER", started.Columns[0].DataType);
        // Microsoft.Data.Sqlite gives the declared type without its size.
        Assert.Equal("NUMERIC", started.Columns[2].DataType);

        var rows = events.OfType<RowBatch>().SelectMany(static b => b.Rows).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(1L, rows[0][0]);
        Assert.Equal(1250.5, rows[0][2]);
        Assert.Equal(0L, rows[1][2]);
        Assert.Null(rows[2][3]);
        Assert.Equal("Müşteri", rows[2][1]);
        Assert.Equal(new byte[] { 10, 11 }, rows[0][4]);
        Assert.Equal(3.0, rows[0][5]);
        Assert.Equal(3, events.OfType<ResultSetCompleted>().Single().RowCount);
    }

    [Fact]
    public async Task Data_changes_report_their_row_count_and_ddl_none()
    {
        Assert.Equal(1, Assert.Single((await RunAsync("UPDATE cards SET balance = balance + 1 WHERE balance > 0")).OfType<StatementCompleted>()).RowsAffected);
        Assert.Null(Assert.Single((await RunAsync("CREATE TABLE logs (id INTEGER)")).OfType<StatementCompleted>()).RowsAffected);
    }

    [Fact]
    public async Task Max_rows_cuts_a_result_and_says_so()
    {
        var events = await RunAsync("WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 1000) SELECT x FROM n", maxRows: 10);

        Assert.Equal(10, events.OfType<RowBatch>().Sum(static b => b.Rows.Count));
        var completed = events.OfType<ResultSetCompleted>().Single();
        Assert.True(completed.Truncated);
        Assert.Equal(10, completed.RowCount);
    }

    [Fact]
    public async Task An_error_carries_sqlites_message()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync("SELECT * FROM cardz"));
        Assert.Contains("no such table: cardz", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_interrupts_a_long_query()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            // Never returns a row: the whole time is spent inside one step of the statement.
            await foreach (var _ in _connection.Executor.ExecuteAsync(
                               new QueryRequest("WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n) SELECT count(*) FROM n"), cancel.Token))
            {
            }
        });

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"cancel took {watch.Elapsed}");

        // The connection is still usable.
        Assert.Single((await RunAsync("SELECT 1")).OfType<RowBatch>());
    }

    [Fact]
    public async Task The_explorer_sees_tables_views_and_columns()
    {
        var metadata = _connection.Metadata;
        var objects = await ListAsync(metadata.GetObjectsAsync(null, DatabaseObjectKinds.Table | DatabaseObjectKinds.View, Token));

        Assert.Equal(["card_summary", "cards", "Müşteri İşlemleri"], objects.Select(static o => o.Name.Name));
        Assert.Equal([DatabaseObjectKinds.View, DatabaseObjectKinds.Table, DatabaseObjectKinds.Table], objects.Select(static o => o.Kind));
        Assert.All(objects, static o => Assert.Equal("main", o.Name.Schema));

        var schemas = await ListAsync(metadata.GetObjectsAsync(null, DatabaseObjectKinds.Schema, Token));
        Assert.Contains(schemas, static s => s.Name.Name == "main");

        var columns = await metadata.GetColumnsAsync(new DatabaseObjectName(null, null, "CARDS"), Token);
        Assert.Equal(["id", "card_no", "balance", "issued_at"], columns.Select(static c => c.Name));
        Assert.True(columns[0].IsPrimaryKey);
        Assert.False(columns[1].IsNullable);
        Assert.Equal("NUMERIC(12,2)", columns[2].DataType);
        Assert.Equal(4, columns[3].Ordinal);

        var generated = await metadata.GetColumnsAsync(new DatabaseObjectName(null, "main", "Müşteri İşlemleri"), Token);
        Assert.Equal(["id", "card_id", "Açıklama", "total"], generated.Select(static c => c.Name));
    }

    [Fact]
    public async Task Definitions_are_the_stored_create_statements_with_a_tables_indexes_and_triggers()
    {
        var table = await _connection.Metadata.GetDefinitionAsync(new DatabaseObject(new DatabaseObjectName(null, null, "Cards"), DatabaseObjectKinds.Table), Token);
        Assert.NotNull(table);
        Assert.StartsWith("CREATE TABLE cards (", table, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX ix_cards_issued ON cards (issued_at);", table, StringComparison.Ordinal);
        Assert.Contains("CREATE TRIGGER trg_cards AFTER INSERT ON cards", table, StringComparison.Ordinal);

        var view = await _connection.Metadata.GetDefinitionAsync(new DatabaseObject(new DatabaseObjectName(null, "main", "card_summary"), DatabaseObjectKinds.View), Token);
        Assert.Equal("CREATE VIEW card_summary AS SELECT card_no, balance FROM cards WHERE balance > 0;\n", view);

        Assert.Null(await _connection.Metadata.GetDefinitionAsync(new DatabaseObject(new DatabaseObjectName(null, null, "nothing"), DatabaseObjectKinds.Table), Token));
        Assert.Null(await _connection.Metadata.GetDefinitionAsync(new DatabaseObject(new DatabaseObjectName(null, "nowhere", "cards"), DatabaseObjectKinds.Table), Token));
    }

    [Fact]
    public async Task Completion_and_go_to_definition_work_on_the_real_schema()
    {
        var service = new SqliteProvider().LanguageService!;

        var tables = await CompleteAsync(service, "SELECT * FROM |");
        Assert.Contains(tables, static i => i.Label == "cards" && i.Kind == CompletionItemKind.Table);
        Assert.Contains(tables, static i => i.Label == "Müşteri İşlemleri" && i.InsertText == "\"Müşteri İşlemleri\"");
        Assert.Contains(tables, static i => i.Label == "card_summary" && i.Kind == CompletionItemKind.View);

        var columns = await CompleteAsync(service, "SELECT c.| FROM cards c");
        Assert.Equal(["id", "card_no", "balance", "issued_at"], columns.Where(static i => i.Kind == CompletionItemKind.Column).Select(static i => i.Label));

        var quoted = await CompleteAsync(service, "SELECT m.| FROM [Müşteri İşlemleri] m");
        // SQLite accepts any letter unquoted.
        Assert.Contains(quoted, static i => i.Label == "Açıklama" && i.InsertText is null or "Açıklama");

        const string Sql = "SELECT * FROM card_summary";
        var definition = await service.GetDefinitionAsync(new SqlDocument(Sql, _connection.Metadata), Sql.Length - 3, Token);
        Assert.NotNull(definition?.Target);
        Assert.Equal(DatabaseObjectKinds.View, definition.Target.Kind);
        Assert.Equal("card_summary", definition.Target.Name.Name);
    }

    [Fact]
    public async Task A_read_only_connection_cannot_write()
    {
        await using var readOnly = await new SqliteProvider().OpenConnectionAsync(
            SqliteProviderTests.Profile(new() { ["file"] = _path, ["mode"] = "readOnly" }), null, Token);

        Assert.Single((await Collect(readOnly.Executor.ExecuteAsync(new QueryRequest("SELECT count(*) FROM cards"), Token))).OfType<RowBatch>());
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Collect(readOnly.Executor.ExecuteAsync(new QueryRequest("DELETE FROM cards"), Token)));
        Assert.Contains("readonly", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<CompletionItem>> CompleteAsync(ISqlLanguageService service, string sqlWithCaret)
    {
        var offset = sqlWithCaret.IndexOf('|', StringComparison.Ordinal);
        return [.. await service.GetCompletionsAsync(new SqlDocument(sqlWithCaret.Remove(offset, 1), _connection.Metadata), offset, Token)];
    }

    private async Task RunScriptAsync(string script)
    {
        foreach (var batch in new SqliteProvider().SplitBatches(script))
        {
            await RunAsync(batch.Text);
        }
    }

    private Task<List<QueryEvent>> RunAsync(string sql, int? maxRows = null) =>
        Collect(_connection.Executor.ExecuteAsync(new QueryRequest(sql) { MaxRows = maxRows }, Token));

    private static async Task<List<QueryEvent>> Collect(IAsyncEnumerable<QueryEvent> events)
    {
        var list = new List<QueryEvent>();
        await foreach (var item in events)
        {
            list.Add(item);
        }

        return list;
    }

    private static async Task<List<DatabaseObject>> ListAsync(IAsyncEnumerable<DatabaseObject> objects)
    {
        var list = new List<DatabaseObject>();
        await foreach (var item in objects)
        {
            list.Add(item);
        }

        return list;
    }
}
