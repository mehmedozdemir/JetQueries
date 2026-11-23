using System.Globalization;
using JetQueries.Core.Queries;

namespace JetQueries.Presentation.Tests;

/// <summary>
/// Paged results never leave anything open on the server: a single SELECT is closed right after its page and run
/// again for more; anything else is read to its end.
/// </summary>
public sealed class PagingTests
{
    public PagingTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_select_is_closed_after_its_page_and_run_again_for_more()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        env.Provider.ReturnRows(120);
        var query = env.Query();
        query.FetchSize = 50;
        query.Text = "SELECT n FROM numbers";

        await query.RunCommand.ExecuteAsync(null);
        var result = Assert.IsType<QueryResultSet>(query.Tabs[0]);
        await Until(() => result.RowCount == 50 && result.HasMore);
        Assert.False(query.IsRunning);
        Assert.False(query.CancelCommand.CanExecute(null));
        Assert.Equal("50+", result.RowsText);
        Assert.StartsWith("50 rows fetched · ", result.FetchStatus, StringComparison.Ordinal);
        Assert.Equal(50, Assert.Single(env.Provider.Executed).BatchSize);

        // The statement was closed (canceled) right after the page.
        Assert.Equal(1, env.Provider.ClosedEarly);

        // Next page: the query runs again, is read to row 100 and closed again; rows come from that one run.
        await query.FetchNextPageCommand.ExecuteAsync(result);
        Assert.Equal(2, env.Provider.Executed.Count);
        Assert.Equal(100, result.RowCount);
        Assert.True(result.HasMore);
        Assert.Equal(2, env.Provider.ClosedEarly);

        // Fetch all: run again and read to the end.
        await query.FetchAllCommand.ExecuteAsync(result);
        Assert.Equal(3, env.Provider.Executed.Count);
        Assert.Equal(120, result.RowCount);
        Assert.False(result.HasMore);
        Assert.Equal("120", result.RowsText);
        Assert.Equal(2, env.Provider.ClosedEarly);
        Assert.False(query.FetchNextPageCommand.CanExecute(result));
        Assert.Equal(Enumerable.Range(1, 120).Select(static n => n.ToString(CultureInfo.InvariantCulture)), Enumerable.Range(0, 120).Select(r => result.GetText(r, 0)));

        // One history entry for the run.
        Assert.Equal(QueryOutcome.Succeeded, Assert.Single(await env.History.SearchAsync(null, 10, Token)).Outcome);
    }

    [Fact]
    public async Task A_result_that_fits_one_page_is_read_to_its_end()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        env.Provider.ReturnRows(30);
        var query = env.Query();
        query.FetchSize = 50;
        query.Text = "SELECT n FROM numbers";

        await query.RunCommand.ExecuteAsync(null);

        var result = Assert.IsType<QueryResultSet>(query.Tabs[0]);
        await Until(() => result.RowCount == 30);
        Assert.False(result.HasMore);
        Assert.Equal(0, env.Provider.ClosedEarly);
        Assert.False(query.FetchNextPageCommand.CanExecute(result));
    }

    [Fact]
    public async Task Text_that_must_not_run_twice_is_read_to_its_end_without_pages()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        env.Provider.ReturnRows(1000);
        var query = env.Query();
        query.FetchSize = 100;

        // A procedure call cannot be run again for a second page: it is read like SSMS reads it.
        query.Text = "EXEC dbo.Numbers";
        await query.RunCommand.ExecuteAsync(null);
        var result = Assert.IsType<QueryResultSet>(query.Tabs[0]);
        await Until(() => result.RowCount == 1000);
        Assert.False(result.HasMore);
        Assert.Single(env.Provider.Executed);
        Assert.Equal(0, env.Provider.ClosedEarly);
    }

    [Fact]
    public async Task Refresh_runs_the_same_select_again_from_its_first_page()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        env.Provider.ReturnRows(1000);
        var query = env.Query();
        query.FetchSize = 100;
        query.Text = "SELECT n FROM numbers";
        await query.RunCommand.ExecuteAsync(null);
        query.Text = "something else being typed";

        await query.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(["SELECT n FROM numbers", "SELECT n FROM numbers"], env.Provider.Executed.Select(static r => r.Sql));
        var result = Assert.IsType<QueryResultSet>(query.Tabs[0]);
        await Until(() => result.RowCount == 100);
        Assert.Equal(2, env.Provider.ClosedEarly);
    }

    [Fact]
    public async Task The_total_row_count_is_computed_without_fetching_the_rows()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        env.Provider.ReturnRows(12345);
        var query = env.Query();
        query.FetchSize = 200;
        query.Text = "SELECT n FROM numbers";
        await query.RunCommand.ExecuteAsync(null);
        var result = Assert.IsType<QueryResultSet>(query.Tabs[0]);
        await Until(() => result.HasMore);

        // Nothing is open after the page: the count runs on the window's own connection.
        var opened = env.Provider.OpenCount;
        await query.CountRowsCommand.ExecuteAsync(result);
        Assert.Equal(12345, result.TotalRows);
        Assert.Equal("200 / 12,345", result.RowsText);
        Assert.Equal(opened, env.Provider.OpenCount);
        Assert.Equal("COUNT(SELECT n FROM numbers)", env.Provider.Executed[^1].Sql);
        Assert.Equal(200, result.RowCount);

        // Something that cannot be counted says so.
        query.Text = "EXEC dbo.PurgeOldLogs";
        await query.RunCommand.ExecuteAsync(null);
        await query.CountRowsCommand.ExecuteAsync(Assert.IsType<QueryResultSet>(query.Tabs[0]));
        Assert.Equal("Only a single SELECT can be counted.", query.Status);
    }

    [Fact]
    public async Task New_tabs_take_the_fetch_size_from_the_settings()
    {
        await using var env = new TestEnvironment();
        var settings = new FixedSettings(new AppearanceSettings { FetchSize = 500 });
        var documents = new DocumentsViewModel(env.Manager, env.Metadata, env.History, env.Jobs, env.Dialogs, env.Ui, null, settings);
        Assert.Equal(500, documents.Selected!.FetchSize);
        documents.NewQueryCommand.Execute(null);
        Assert.Equal(500, documents.Selected!.FetchSize);
    }

    private sealed class FixedSettings(AppearanceSettings settings) : IAppearanceService
    {
        public AppearanceSettings Current { get; private set; } = settings;

        public void Apply(AppearanceSettings value) => Current = value;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Token);
        }

        Assert.True(condition());
    }
}
