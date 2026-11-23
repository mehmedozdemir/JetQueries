using System.Globalization;

namespace JetQueries.Presentation.Tests;

/// <summary>DBeaver's "Execute in new tab": a run that keeps the results shown and numbers its own after them.</summary>
public sealed class RunInNewTabTests
{
    public RunInNewTabTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Results_of_a_run_in_a_new_tab_follow_the_ones_shown_and_each_pages_its_own_query()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        env.Provider.ReturnRows(300);
        var query = env.Query();
        query.FetchSize = 100;
        query.Text = "SELECT a FROM first_table";
        await query.RunCommand.ExecuteAsync(null);
        var first = Assert.IsType<QueryResultSet>(query.Tabs[0]);
        await Until(() => first.RowCount == 100);

        query.Text = "SELECT b FROM second_table";
        await query.RunInNewTabCommand.ExecuteAsync(null);
        var second = Assert.IsType<QueryResultSet>(query.Tabs[1]);
        await Until(() => second.RowCount == 100);
        Assert.Equal(["Result 1", "Result 2", "Messages"], query.Tabs.Select(static t => t.Title));
        Assert.Same(second, query.SelectedTab);

        // The first result still pages over its own query.
        await query.FetchNextPageCommand.ExecuteAsync(first);
        Assert.Equal("SELECT a FROM first_table", env.Provider.Executed[^1].Sql);
        Assert.Equal(200, first.RowCount);
        await query.CountRowsCommand.ExecuteAsync(first);
        Assert.Equal("COUNT(SELECT a FROM first_table)", env.Provider.Executed[^1].Sql);

        // Refresh of a result reads its own first page again in place.
        await query.RefreshCommand.ExecuteAsync(second);
        Assert.Equal("SELECT b FROM second_table", env.Provider.Executed[^1].Sql);
        Assert.Same(second, query.Tabs[1]);
        Assert.Equal(100, second.RowCount);

        // Closing a result keeps the others; the numbering goes on.
        query.CloseResultCommand.Execute(first);
        Assert.Equal(["Result 2", "Messages"], query.Tabs.Select(static t => t.Title));
        Assert.False(query.CloseResultCommand.CanExecute(query.Messages));
        await query.RunInNewTabCommand.ExecuteAsync(null);
        Assert.Equal(["Result 2", "Result 3", "Messages"], query.Tabs.Select(static t => t.Title));

        // A plain run replaces them all and starts again from 1.
        await query.RunCommand.ExecuteAsync(null);
        Assert.Equal(["Result 1", "Messages"], query.Tabs.Select(static t => t.Title));
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
