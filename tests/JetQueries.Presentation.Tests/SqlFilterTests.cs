using System.Globalization;

namespace JetQueries.Presentation.Tests;

/// <summary>The SQL filter over the grid runs the query again on the server, wrapped in a WHERE, and pages like it.</summary>
public sealed class SqlFilterTests
{
    public SqlFilterTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string Query = "SELECT n FROM numbers";
    private const string Filtered = "SELECT * FILTER(SELECT n FROM numbers WHERE n > 10)";

    [Fact]
    public async Task A_filter_runs_the_query_again_and_pages_counts_and_refreshes_with_it()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        env.Provider.ReturnRows(500);
        var query = env.Query();
        query.FetchSize = 50;
        query.Text = Query;
        await query.RunCommand.ExecuteAsync(null);
        var result = Assert.IsType<QueryResultSet>(query.Tabs[0]);
        await Until(() => result.RowCount == 50);
        Assert.True(result.CanFilter);
        Assert.False(result.IsFiltered);
        Assert.True(query.ApplyFilterCommand.CanExecute(result));

        result.SqlFilter = "  n > 10 ";
        await query.ApplyFilterCommand.ExecuteAsync(result);
        Assert.Equal(Filtered, env.Provider.Executed[^1].Sql);
        Assert.Equal("n > 10", result.AppliedFilter);
        Assert.True(result.IsFiltered);
        Assert.Equal(50, result.RowCount);
        Assert.True(result.HasMore);

        // Nothing stays open: the filtered page was closed like any other.
        Assert.Equal(2, env.Provider.ClosedEarly);

        // Next page and the count use the filtered query.
        await query.FetchNextPageCommand.ExecuteAsync(result);
        Assert.Equal(Filtered, env.Provider.Executed[^1].Sql);
        Assert.Equal(100, result.RowCount);
        await query.CountRowsCommand.ExecuteAsync(result);
        Assert.Equal("COUNT(" + Filtered + ")", env.Provider.Executed[^1].Sql);

        // Refresh keeps the filter; the editor text is not involved.
        query.Text = "something else";
        await query.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(Filtered, env.Provider.Executed[^1].Sql);
        var refreshed = Assert.IsType<QueryResultSet>(query.Tabs[0]);
        Assert.Equal("n > 10", refreshed.AppliedFilter);
        Assert.Equal("n > 10", refreshed.SqlFilter);

        // Clearing runs the plain query again.
        await query.ClearFilterCommand.ExecuteAsync(refreshed);
        Assert.Equal(Query, env.Provider.Executed[^1].Sql);
        Assert.Null(refreshed.AppliedFilter);
        Assert.Equal(string.Empty, refreshed.SqlFilter);
    }

    [Fact]
    public async Task A_filter_that_is_not_one_condition_is_refused_and_the_rows_stay()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        env.Provider.ReturnRows(500);
        var query = env.Query();
        query.FetchSize = 50;
        query.Text = Query;
        await query.RunCommand.ExecuteAsync(null);
        var result = Assert.IsType<QueryResultSet>(query.Tabs[0]);
        await Until(() => result.RowCount == 50);
        var executed = env.Provider.Executed.Count;

        result.SqlFilter = "1 = 1; DROP TABLE numbers";
        await query.ApplyFilterCommand.ExecuteAsync(result);

        Assert.Equal(executed, env.Provider.Executed.Count);
        Assert.Null(result.AppliedFilter);
        Assert.Equal(50, result.RowCount);
        Assert.StartsWith("The filter must be one SQL condition", query.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_result_of_a_single_select_can_be_filtered()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        env.Provider.ReturnRows(30);
        var query = env.Query();
        query.Text = "EXEC dbo.Numbers";
        await query.RunCommand.ExecuteAsync(null);
        var result = Assert.IsType<QueryResultSet>(query.Tabs[0]);
        await Until(() => result.RowCount == 30);

        Assert.False(result.CanFilter);
        Assert.False(query.ApplyFilterCommand.CanExecute(result));

        // A SELECT that fits one page can still be filtered.
        query.Text = Query;
        await query.RunCommand.ExecuteAsync(null);
        var select = Assert.IsType<QueryResultSet>(query.Tabs[0]);
        await Until(() => select.RowCount == 30);
        Assert.False(select.HasMore);
        Assert.True(select.CanFilter);
        Assert.True(query.ApplyFilterCommand.CanExecute(select));
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Token);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task The_filter_box_offers_the_columns_then_the_words_of_a_condition()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var query = env.Query();
        var set = new QueryResultSet(
            "r",
            [new JetQueries.Sdk.ResultColumn("Balance", "decimal(12,2)", typeof(decimal)), new JetQueries.Sdk.ResultColumn("City Name", "nvarchar(40)", typeof(string)), new JetQueries.Sdk.ResultColumn("Bank", "int", typeof(int))]);

        List<string> Labels(string marked) =>
            [.. query.FilterCompletions(set, marked.Replace("|", string.Empty, StringComparison.Ordinal), marked.IndexOf('|', StringComparison.Ordinal)).Select(static i => i.Label)];

        // At the start and after AND / OR / NOT / "(": the columns.
        Assert.Equal(["Balance", "City Name", "Bank"], Labels("|"));
        Assert.Equal(["Balance", "City Name", "Bank"], Labels("Balance > 1 AND |"));
        Assert.Equal(["Balance", "City Name", "Bank"], Labels("(|"));

        // After a column or a value: the words.
        Assert.Equal(["AND", "OR", "NOT", "LIKE", "IN", "IS NULL", "IS NOT NULL", "BETWEEN"], Labels("Balance > 100 |"));

        // A prefix narrows both, ignoring case; nothing inside a string.
        Assert.Equal(["Balance", "Bank", "BETWEEN"], Labels("b|"));
        Assert.Empty(Labels("City Name LIKE 'b|"));

        // The type is shown; a name that needs it is quoted by the provider.
        var items = query.FilterCompletions(set, "Ci", 2);
        var city = Assert.Single(items, static i => i.Label == "City Name");
        Assert.Equal("nvarchar(40)", city.Detail);
        Assert.Equal(new JetQueries.Sdk.TextSpan(0, 2), city.ReplaceSpan);
        Assert.Equal(env.Provider.QuoteIdentifier("City Name"), city.InsertText);
        Assert.Equal("Balance", Assert.Single(query.FilterCompletions(set, "Bal", 3)).InsertText);
    }
}
