using System.Globalization;
using JetQueries.Core.Connections;
using JetQueries.Presentation.ViewModels;
using JetQueries.Provider.Sqlite;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>The data editor of a result grid: which SELECTs can be edited, and saving changes to the table.</summary>
public sealed class ResultEditingTests
{
    public ResultEditingTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("SELECT * FROM cards", "cards", true)]
    [InlineData("select top 100 * from dbo.Cards c where c.Id > 3 order by 1;", "dbo.Cards", true)]
    [InlineData("SELECT c.* FROM [Sales].[dbo].[Cards] AS c", "[Sales].[dbo].[Cards]", true)]
    [InlineData("SELECT id, card_no AS no, upper(name) AS name FROM \"public\".\"cards\" LIMIT 10", "\"public\".\"cards\"", false)]
    [InlineData("-- the cards\nSELECT /* all */ * FROM cards FETCH FIRST 5 ROWS ONLY", "cards", true)]
    public void A_select_of_one_table_is_editable(string sql, string table, bool all)
    {
        var shape = EditableSelect.Analyze(sql);

        Assert.NotNull(shape);
        Assert.Equal(table, shape.TableText);
        Assert.Equal(all, shape.AllColumns);
        Assert.InRange(shape.TableOffset, sql.LastIndexOf(table, StringComparison.Ordinal), sql.LastIndexOf(table, StringComparison.Ordinal) + table.Length);
    }

    [Fact]
    public void Plain_columns_are_mapped_and_computed_ones_are_not()
    {
        var shape = EditableSelect.Analyze("SELECT id, card_no AS no, c.balance b, upper(name) AS name, 'x' AS kind FROM cards c")!;

        Assert.Equal("id", shape.Plain["ID"]);
        Assert.Equal("card_no", shape.Plain["no"]);
        Assert.Equal("balance", shape.Plain["b"]);
        Assert.False(shape.Plain.ContainsKey("name"));
        Assert.False(shape.Plain.ContainsKey("kind"));
    }

    [Theory]
    [InlineData("SELECT * FROM a JOIN b ON a.id = b.id")]
    [InlineData("SELECT * FROM a, b")]
    [InlineData("SELECT DISTINCT name FROM a")]
    [InlineData("SELECT name, count(*) FROM a GROUP BY name")]
    [InlineData("SELECT * FROM a UNION SELECT * FROM b")]
    [InlineData("SELECT * FROM (SELECT * FROM a) x")]
    [InlineData("WITH x AS (SELECT 1 n) SELECT * FROM x")]
    [InlineData("UPDATE a SET n = 1")]
    [InlineData("SELECT 1")]
    [InlineData("SELECT * FROM a; SELECT * FROM b")]
    [InlineData("SELECT * INTO b FROM a")]
    public void Other_statements_are_read_only(string sql) => Assert.Null(EditableSelect.Analyze(sql));

    [Fact]
    public void Words_inside_strings_comments_and_subqueries_do_not_count()
    {
        Assert.NotNull(EditableSelect.Analyze("SELECT * FROM a WHERE note = 'x JOIN y' -- UNION\n AND id IN (SELECT id FROM b GROUP BY id)"));
    }

    [Fact]
    public async Task Cells_rows_added_and_rows_deleted_are_saved_in_one_transaction()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var (query, result) = await fixture.RunAsync("SELECT * FROM cards ORDER BY id");
        var editor = result.Editor!;
        Assert.Null(result.ReadOnlyReason);
        Assert.Equal("cards", editor.Table.Name);

        // A bad value is refused and says why; a good one is converted by the column's type.
        Assert.False(editor.SetText(1, 2, "on iki"));
        Assert.Contains("balance", editor.Error, StringComparison.Ordinal);
        Assert.True(editor.SetText(1, 2, "12,5"));
        Assert.Null(editor.Error);
        Assert.Equal(12.5m, result.GetValue(1, 2));
        Assert.True(editor.IsModified(1, 2));

        // Typing the value back is no change.
        Assert.True(editor.SetText(0, 1, "Değişti"));
        Assert.True(editor.SetText(0, 1, "Kart 1"));
        Assert.False(editor.IsModified(0, 1));
        Assert.True(editor.SetText(0, 1, "Müşteri İşlemleri"));

        editor.ToggleDelete([2]);
        Assert.True(editor.IsDeleted(2));
        Assert.False(editor.CanEdit(2, 1));

        var added = editor.AddRow();
        Assert.Equal(4, result.RowCount);
        Assert.True(editor.IsInserted(added));
        Assert.True(editor.SetText(added, 1, "Yeni kart"));
        Assert.True(editor.SetText(added, 3, "evet"));

        // An empty cell is NULL.
        Assert.True(editor.SetText(1, 3, string.Empty));
        Assert.Equal(4, editor.ChangeCount);
        Assert.True(query.SaveChangesCommand.CanExecute(result));

        await query.SaveChangesCommand.ExecuteAsync(result);

        Assert.Equal(0, editor.ChangeCount);
        Assert.DoesNotContain(query.Messages.Lines, static l => l.IsError);
        var rows = await fixture.ReadAsync("SELECT id, card_no, balance, active FROM cards ORDER BY id");
        Assert.Equal(3, rows.Count);
        Assert.Equal("Müşteri İşlemleri", rows[0][1]);
        Assert.Equal(12.5, Convert.ToDouble(rows[1][2], CultureInfo.InvariantCulture));
        Assert.Null(rows[1][3]);
        Assert.Equal("Yeni kart", rows[2][1]);
        Assert.Equal(1L, Convert.ToInt64(rows[2][3], CultureInfo.InvariantCulture));

        // The result was read again: the new row has the id the table gave it.
        await Until(() => result.RowCount == 3);
        Assert.Equal("Yeni kart", result.GetText(2, 1));
    }

    [Fact]
    public async Task A_row_changed_by_someone_else_rolls_every_change_back()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var (query, result) = await fixture.RunAsync("SELECT id, card_no FROM cards ORDER BY id");
        var editor = result.Editor!;
        Assert.True(editor.SetText(0, 1, "Kaydedilmemeli"));
        Assert.True(editor.SetText(1, 1, "Bulunamaz"));

        // The second row goes before the save: its update finds nothing, so the first is not kept either.
        await fixture.ReadAsync("DELETE FROM cards WHERE id = 2");
        await query.SaveChangesCommand.ExecuteAsync(result);

        Assert.Contains(query.Messages.Lines, static l => l.IsError && l.Text.Contains("id = 2", StringComparison.Ordinal));
        Assert.Equal(2, editor.ChangeCount);
        var rows = await fixture.ReadAsync("SELECT card_no FROM cards WHERE id = 1");
        Assert.Equal("Kart 1", rows[0][0]);
    }

    [Fact]
    public async Task Undo_brings_the_values_back_and_drops_added_rows()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var (_, result) = await fixture.RunAsync("SELECT * FROM cards");
        var editor = result.Editor!;
        editor.SetText(0, 1, "Geçici");
        editor.AddRow();
        editor.ToggleDelete([1]);
        Assert.Equal(3, editor.ChangeCount);

        editor.RevertCommand.Execute(null);

        Assert.Equal(0, editor.ChangeCount);
        Assert.Equal("Kart 1", result.GetText(0, 1));
        Assert.Equal(3, result.RowCount);
        Assert.False(editor.IsDeleted(1));

        // An added row deleted again simply goes.
        var added = editor.AddRow();
        editor.ToggleDelete([added]);
        Assert.Equal(3, result.RowCount);
        Assert.Equal(0, editor.ChangeCount);
    }

    [Fact]
    public async Task Joins_and_results_without_the_key_stay_read_only_and_say_why()
    {
        await using var fixture = await SqliteFixture.CreateAsync();
        var (_, joined) = await fixture.RunAsync("SELECT a.id, b.card_no FROM cards a JOIN cards b ON a.id = b.id");
        Assert.Null(joined.Editor);
        Assert.Equal(Resources.Strings.EditNotSingleTable, joined.ReadOnlyReason);

        var (_, noKey) = await fixture.RunAsync("SELECT card_no, balance FROM cards");
        Assert.Null(noKey.Editor);
        Assert.Equal(Resources.Strings.EditNoKey, noKey.ReadOnlyReason);
    }

    [Fact]
    public void Statements_bind_new_values_and_the_key_read_as_parameters()
    {
        var provider = new SqliteProvider();
        var update = new RowChange(RowChangeKind.Update, ["card_no", "balance"], ["x'; DROP TABLE cards; --", 1m], ["id"], [7L]);

        var (sql, parameters) = ResultEditor.Statement(provider, provider, new DatabaseObjectName(null, "main", "cards"), update);

        Assert.Equal("UPDATE \"cards\"\nSET \"card_no\" = @s0, \"balance\" = @s1\nWHERE \"id\" = @k0", sql);
        Assert.Equal(["s0", "s1", "k0"], parameters.Select(static p => p.Name));
        Assert.Equal("x'; DROP TABLE cards; --", parameters[0].Value);

        var (delete, keys) = ResultEditor.Statement(provider, provider, new DatabaseObjectName(null, "main", "cards"), new RowChange(RowChangeKind.Delete, [], [], ["a", "b"], [1, 2]));
        Assert.Equal("DELETE FROM \"cards\"\nWHERE \"a\" = @k0 AND \"b\" = @k1", delete);
        Assert.Equal(2, keys.Count);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, Token);
        }

        Assert.True(condition());
    }

    /// <summary>A SQLite file with a table of three cards, open in a query window.</summary>
    private sealed class SqliteFixture : IAsyncDisposable
    {
        private readonly TestEnvironment _env = new();
        private ConnectionManager _manager = null!;
        private ConnectionSession _session = null!;
        private ConnectionDefinition _definition = null!;

        public static async Task<SqliteFixture> CreateAsync()
        {
            var fixture = new SqliteFixture();
            var folder = Path.Combine(AppContext.BaseDirectory, "edit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            fixture._manager = new ConnectionManager(new FakeExtensions(new SqliteProvider()), new SessionCredentialStore(), fixture._env.Probe);
            fixture._definition = new ConnectionDefinition
            {
                Id = Guid.NewGuid(),
                Name = "cards",
                ProviderId = SqliteProvider.ProviderId,
                Settings = new Dictionary<string, string> { ["file"] = Path.Combine(folder, "cards.db"), ["mode"] = "create" },
            };
            fixture._session = (await fixture._manager.OpenAsync(fixture._definition, null, Token)).Session!;
            await fixture.ReadAsync("CREATE TABLE cards (id INTEGER PRIMARY KEY, card_no TEXT NOT NULL, balance NUMERIC, active BOOLEAN)");
            await fixture.ReadAsync("INSERT INTO cards (card_no, balance, active) VALUES ('Kart 1', 10, 1), ('Kart 2', 20.25, 0), ('Kart 3', NULL, 1)");
            return fixture;
        }

        public async Task<(QueryDocumentViewModel Query, QueryResultSet Result)> RunAsync(string sql)
        {
            var query = new QueryDocumentViewModel("Query 1", _manager, _env.Metadata, _env.History, _env.Jobs, _env.Dialogs, _env.Ui);
            query.SelectSession(_definition.Id);
            query.Text = sql;
            await query.RunCommand.ExecuteAsync(null);
            var result = Assert.IsType<QueryResultSet>(query.Tabs[0]);
            await Until(() => result.Editor is not null || result.ReadOnlyReason is not null);
            return (query, result);
        }

        public async Task<List<object?[]>> ReadAsync(string sql)
        {
            var rows = new List<object?[]>();
            await foreach (var e in _session.ExecuteAsync(new QueryRequest(sql), Token))
            {
                if (e is RowBatch batch)
                {
                    rows.AddRange(batch.Rows);
                }
            }

            return rows;
        }

        public async ValueTask DisposeAsync()
        {
            await _manager.DisposeAsync();
            await _env.DisposeAsync();
        }
    }
}
