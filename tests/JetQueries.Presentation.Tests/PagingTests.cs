using System.Globalization;
using JetQueries.Core.Queries;

namespace JetQueries.Presentation.Tests;

public sealed class PagingTests
{
    public PagingTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_first_page_is_fetched_and_the_cursor_stays_open_for_more()
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
        Assert.True(query.HasOpenCursor);
        Assert.False(query.IsRunning);
        Assert.Equal("50+", result.RowsText);
        Assert.StartsWith("50 rows fetched · ", result.FetchStatus, StringComparison.Ordinal);
        Assert.True(query.CancelCommand.CanExecute(null));
        Assert.Equal(50, Assert.Single(env.Provider.Executed).BatchSize);

        await query.FetchNextPageCommand.ExecuteAsync(result);
        await Until(() => result.RowCount == 100);
        Assert.True(result.HasMore);

        await query.FetchAllCommand.ExecuteAsync(result);
        await Until(() => result.RowCount == 120 && !result.HasMore);
        Assert.False(query.HasOpenCursor);
        Assert.Equal("120", result.RowsText);
        Assert.Equal(Enumerable.Range(1, 120).Select(static n => n.ToString(CultureInfo.InvariantCulture)), Enumerable.Range(0, 120).Select(r => result.GetText(r, 0)));
        Assert.Equal(0, env.Provider.ClosedEarly);

        // One history entry for the whole run.
        var entry = Assert.Single(await env.History.SearchAsync(null, 10, Token));
        Assert.Equal(QueryOutcome.Succeeded, entry.Outcome);
    }

    [Fact]
    public async Task A_result_that_fits_one_page_closes_its_cursor_at_once()
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
        Assert.False(query.HasOpenCursor);
        Assert.False(query.FetchNextPageCommand.CanExecute(result));
    }

    [Fact]
    public async Task Cancel_a_new_run_or_closing_the_tab_closes_an_open_cursor_and_keeps_its_rows()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        env.Provider.ReturnRows(1000);
        var query = env.Query();
        query.FetchSize = 100;
        query.Text = "SELECT n FROM numbers";

        await query.RunCommand.ExecuteAsync(null);
        var result = Assert.IsType<QueryResultSet>(query.Tabs[0]);
        await Until(() => query.HasOpenCursor);
        await query.CancelCommand.ExecuteAsync(null);
        Assert.False(query.HasOpenCursor);
        Assert.False(result.HasMore);
        Assert.Equal(100, result.RowCount);
        Assert.Equal(1, env.Provider.ClosedEarly);

        // Refresh runs the same SQL again; a new run closes the open cursor first.
        await query.RefreshCommand.ExecuteAsync(null);
        await Until(() => query.HasOpenCursor);
        Assert.Equal(2, env.Provider.Executed.Count);
        query.Text = "SELECT n FROM other";
        await query.RunCommand.ExecuteAsync(null);
        await Until(() => query.HasOpenCursor);
        Assert.Equal(2, env.Provider.ClosedEarly);

        await query.CloseAsync();
        Assert.Equal(3, env.Provider.ClosedEarly);
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
        await Until(() => query.HasOpenCursor);

        // The window's connection holds the open cursor: the count runs on a second connection.
        var opened = env.Provider.OpenCount;
        await query.CountRowsCommand.ExecuteAsync(result);
        Assert.Equal(12345, result.TotalRows);
        Assert.Equal("200 / 12,345", result.RowsText);
        Assert.Equal(opened + 1, env.Provider.OpenCount);
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
