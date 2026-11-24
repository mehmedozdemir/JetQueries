using System.Globalization;
using JetQueries.Core.Connections;
using JetQueries.Provider.Sqlite;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>" = ''" after a column in a condition, and the hover card of tables and columns.</summary>
public sealed class EditorAssistValueTests
{
    public EditorAssistValueTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("SELECT * FROM Cards WHERE |CardNo", "varchar(19)", " = ''")]
    [InlineData("SELECT * FROM Cards c WHERE c.Id > 3 AND c.|Name", "nvarchar(50) · c", " = N''")]
    [InlineData("SELECT * FROM Cards WHERE Id = 1 OR (|City", "nvarchar(40)", " = N''")]
    [InlineData("SELECT * FROM A a JOIN B b ON |b.Code", "char(3)", " = ''")]
    [InlineData("SELECT * FROM Cards WHERE |IssuedAt", "datetime2(7)", " = ''")]
    [InlineData("SELECT * FROM Cards WHERE |Id2", "uniqueidentifier", " = ''")]
    [InlineData("UPDATE Cards SET |Name", "nvarchar(50)", " = N''")]
    [InlineData("UPDATE Cards\nSET Balance = 0,\n    |City", "VARCHAR2(40)", " = ''")]
    [InlineData("UPDATE Cards SET Name = (SELECT x FROM T WHERE |Code", "text", " = ''")]
    [InlineData("SELECT COUNT(*) FROM Cards GROUP BY City HAVING |City", "character varying(40)", " = ''")]
    public void A_quoted_column_in_a_condition_gets_a_comparison(string sql, string type, string expected)
    {
        var (text, start) = (sql.Replace("|", string.Empty, StringComparison.Ordinal), sql.IndexOf('|', StringComparison.Ordinal));
        var end = text.Length;

        var suffix = ValueSnippet.After(text, start, end, Column(type));

        Assert.NotNull(suffix);
        Assert.Equal(expected, suffix.Value.Text);

        // The caret goes between the quotes.
        var result = text + suffix.Value.Text;
        Assert.Equal("''", result.Substring(end + suffix.Value.Caret - 1, 2));
    }

    [Theory]
    [InlineData("SELECT * FROM Cards WHERE |Balance", "decimal(12,2)")]
    [InlineData("SELECT * FROM Cards WHERE |Active", "bit")]
    [InlineData("SELECT * FROM Cards WHERE |Photo", "varbinary(max)")]
    [InlineData("SELECT |Name", "nvarchar(50)")]
    [InlineData("SELECT Id, |Name FROM Cards", "nvarchar(50)")]
    [InlineData("SELECT * FROM Cards ORDER BY |Name", "nvarchar(50)")]
    [InlineData("INSERT INTO Cards (Id, |Name", "nvarchar(50)")]
    [InlineData("SELECT * FROM Cards WHERE Note = 'x AND |Name", "nvarchar(50)")]
    [InlineData("SELECT * FROM Cards WHERE |Name = 'a'", "nvarchar(50)")]
    public void Elsewhere_or_for_other_types_nothing_is_added(string sql, string type)
    {
        var text = sql.Replace("|", string.Empty, StringComparison.Ordinal);
        var start = sql.IndexOf('|', StringComparison.Ordinal);
        var end = start + Word(text, start);

        Assert.Null(ValueSnippet.After(text, start, end, Column(type)));
    }

    [Fact]
    public void Only_columns_get_it()
    {
        var table = new CompletionItem("Cards", CompletionItemKind.Table, new TextSpan(0, 0)) { Detail = "nvarchar(50)" };
        Assert.Null(ValueSnippet.After("SELECT * FROM X WHERE Cards", 22, 27, table));
    }

    [Fact]
    public async Task Hovering_a_table_an_alias_or_a_column_tells_what_it_is()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "hover-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await using var env = new TestEnvironment();
        await using var manager = new ConnectionManager(new FakeExtensions(new SqliteProvider()), new SessionCredentialStore(), env.Probe);
        var definition = new ConnectionDefinition
        {
            Id = Guid.NewGuid(),
            Name = "cards",
            ProviderId = SqliteProvider.ProviderId,
            Settings = new Dictionary<string, string> { ["file"] = Path.Combine(folder, "cards.db"), ["mode"] = "create" },
        };
        var session = (await manager.OpenAsync(definition, null, Token)).Session!;
        await foreach (var _ in session.ExecuteAsync(new QueryRequest("CREATE TABLE cards (id INTEGER PRIMARY KEY, card_no TEXT NOT NULL, city TEXT, balance NUMERIC)"), Token))
        {
        }

        var query = new QueryDocumentViewModel("Query 1", manager, env.Metadata, env.History, env.Jobs, env.Dialogs, env.Ui);
        query.SelectSession(definition.Id);
        const string Sql = "SELECT c.card_no FROM cards c WHERE city = 'x'";

        var table = await query.HoverAsync(Sql, Sql.IndexOf("cards c", StringComparison.Ordinal) + 2, Token);
        Assert.NotNull(table);
        Assert.Equal("cards", table.Title.Split('.')[^1]);
        Assert.Equal("Table", table.Kind);
        Assert.Equal(["id", "card_no", "city", "balance"], table.Columns.Select(static c => c.Name));
        Assert.True(table.Columns[0].IsPrimaryKey);
        Assert.Equal("4 columns", table.Subtitle);

        var alias = await query.HoverAsync(Sql, Sql.IndexOf("c.card_no", StringComparison.Ordinal), Token);
        Assert.Equal(table.Title, alias!.Title);

        var column = await query.HoverAsync(Sql, Sql.IndexOf("card_no", StringComparison.Ordinal) + 3, Token);
        Assert.Equal("card_no", column!.Title);
        Assert.Equal("Column", column.Kind);
        Assert.False(column.Column!.IsNullable);
        Assert.StartsWith("TEXT · ", column.Subtitle, StringComparison.OrdinalIgnoreCase);

        var bare = await query.HoverAsync(Sql, Sql.IndexOf("city", StringComparison.Ordinal) + 1, Token);
        Assert.Equal("city", bare!.Title);

        // A keyword or a string is nothing.
        Assert.Null(await query.HoverAsync(Sql, 2, Token));
        Assert.Null(await query.HoverAsync(Sql, Sql.Length - 2, Token));
    }

    private static CompletionItem Column(string detail) => new("x", CompletionItemKind.Column, new TextSpan(0, 0)) { Detail = detail };

    private static int Word(string text, int start)
    {
        var end = start;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }

        return end - start;
    }
}
