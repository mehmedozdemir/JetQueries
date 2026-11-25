using System.Globalization;
using JetQueries.Core.Queries;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>Decimal scale in results, history wording, the caret indicator.</summary>
public sealed class UxReviewTests
{
    public UxReviewTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    [Theory]
    [InlineData("decimal(12,2)", 2)]
    [InlineData("numeric(10, 3)", 3)]
    [InlineData("NUMBER(12,2)", 2)]
    [InlineData("dec(5,0)", 0)]
    [InlineData("decimal(18)", null)]
    [InlineData("money", null)]
    [InlineData("nvarchar(50)", null)]
    [InlineData("", null)]
    public void The_scale_of_a_decimal_type(string type, int? scale) => Assert.Equal(scale, DecimalScale.Of(type));

    [Theory]
    [InlineData("1500", 2, "1500.00")]
    [InlineData("1462.5", 2, "1462.50")]
    [InlineData("12.345", 2, "12.35")]
    [InlineData("7.10", 2, "7.10")]
    [InlineData("-0.5", 3, "-0.500")]
    [InlineData("42.9", 0, "43")]
    public void A_value_gets_its_columns_scale(string value, int scale, string expected) =>
        Assert.Equal(expected, DecimalScale.Apply(decimal.Parse(value, CultureInfo.InvariantCulture), scale).ToString(CultureInfo.InvariantCulture));

    [Fact]
    public void A_result_shows_decimals_with_the_declared_scale()
    {
        var set = new QueryResultSet("Result 1", [new ResultColumn("Balance", "decimal(12,2)", typeof(decimal)), new ResultColumn("Ratio", "float", typeof(double))]);
        set.AddRows([[1500m, 1.5d], [1462.5m, 2d], [null, null]]);

        Assert.Equal("1500.00", set.GetText(0, 0));
        Assert.Equal("1462.50", set.GetText(1, 0));
        Assert.Null(set.GetText(2, 0));
        Assert.Equal(1.5d, set.GetValue(0, 1));
    }

    [Theory]
    [InlineData("SELECT * FROM t", "3 rows fetched")]
    [InlineData("  -- comment\n/* x */ with a as (select 1) select * from a", "3 rows fetched")]
    [InlineData("UPDATE t SET a = 1", "3 rows affected")]
    [InlineData("delete from t", "3 rows affected")]
    [InlineData("EXEC dbo.Run", "3 rows")]
    public void History_says_whether_rows_were_fetched_or_affected(string sql, string expected)
    {
        var entry = new HistoryEntryViewModel(new QueryHistoryEntry(1, DateTimeOffset.Now, Guid.NewGuid(), "c", "p", sql, TimeSpan.FromMilliseconds(5), QueryOutcome.Succeeded, RowCount: 3));
        Assert.Equal($"c · {expected} · 5 ms", entry.Detail);
    }

    [Fact]
    public async Task The_caret_shows_its_line_column_and_selection()
    {
        await using var env = new TestEnvironment();
        var query = env.Query();
        Assert.True(query.CanChangeSession);

        query.SetCaret(12, 5, 0);
        Assert.Equal("Ln 12, Col 5", query.CaretText);
        query.SetCaret(3, 1, 42);
        Assert.Equal("Ln 3, Col 1 · 42 selected", query.CaretText);

        query.HasOpenTransaction = true;
        Assert.False(query.CanChangeSession);
    }

    [Fact]
    public async Task The_database_list_is_narrowed_by_any_part_of_the_name()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        await using var env = new TestEnvironment();
        var query = env.Query();
        foreach (var name in new[] { "Cards", "İşlemler", "master", "CardsArchive", "msdb" })
        {
            query.Databases.Add(name);
        }

        query.DatabaseFilter = "card";
        Assert.Equal(["Cards", "CardsArchive"], query.ShownDatabases);
        query.DatabaseFilter = "işlem";
        Assert.Equal(["İşlemler"], query.ShownDatabases);
        query.DatabaseFilter = "zzz";
        Assert.Empty(query.ShownDatabases);
        query.DatabaseFilter = string.Empty;
        Assert.Equal(5, query.ShownDatabases.Count);
    }

    [Fact]
    public async Task The_explorer_starts_with_its_folders_closed_and_opens_them_to_reveal()
    {
        await using var env = new TestEnvironment();
        await env.Store.SaveAsync(TestEnvironment.Definition("Kartlar", "Müşteri A/Test"), TestContext.Current.CancellationToken);
        var connections = env.Connections();
        await connections.LoadAsync();

        var customer = Assert.IsType<FolderNodeViewModel>(connections.Roots[0]);
        var test = Assert.IsType<FolderNodeViewModel>(customer.Children[0]);
        Assert.False(customer.IsExpanded);
        Assert.False(test.IsExpanded);

        // Opened by the user, it stays open through a rebuild.
        customer.IsExpanded = true;
        await connections.LoadAsync();
        Assert.True(Assert.IsType<FolderNodeViewModel>(connections.Roots[0]).IsExpanded);

        // Revealing a connection opens the folders it is in.
        var folder = (FolderNodeViewModel)connections.Roots[0];
        var inner = (FolderNodeViewModel)folder.Children[0];
        var connection = inner.Children.OfType<ConnectionNodeViewModel>().Single();
        connections.Reveal(connection);
        Assert.True(inner.IsExpanded);
        Assert.Same(connection, connections.Selected);
    }
}
