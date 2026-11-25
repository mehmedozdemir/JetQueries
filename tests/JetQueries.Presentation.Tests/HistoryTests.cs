using System.Globalization;
using System.Runtime.CompilerServices;
using JetQueries.Core.Queries;
using JetQueries.Presentation.ViewModels;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

public sealed class HistoryTests
{
    public HistoryTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_run_that_was_sent_is_recorded_with_how_it_ended()
    {
        await using var env = new TestEnvironment();
        var cards = await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var query = env.Query();

        query.Text = "SELECT * FROM cards";
        await query.RunCommand.ExecuteAsync(null);

        env.Provider.Execute = static (_, token) => Failing("Invalid object name 'cardz'.", token);
        query.Text = "SELECT * FROM cardz";
        await query.RunCommand.ExecuteAsync(null);

        var started = new TaskCompletionSource();
        env.Provider.Execute = (_, token) => Endless(started, token);
        query.Text = "WAITFOR DELAY '00:10:00'";
        var run = query.RunCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        query.CancelCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(5), Token);

        var entries = await env.History.SearchAsync(null, 10, Token);
        Assert.Equal(["WAITFOR DELAY '00:10:00'", "SELECT * FROM cardz", "SELECT * FROM cards"], entries.Select(static e => e.Sql));
        Assert.Equal([QueryOutcome.Canceled, QueryOutcome.Failed, QueryOutcome.Succeeded], entries.Select(static e => e.Outcome));
        Assert.Equal("Invalid object name 'cardz'.", entries[1].Error);
        Assert.Equal(3, entries[2].RowCount);
        Assert.Null(entries[1].RowCount);
        Assert.All(entries, e =>
        {
            Assert.Equal(cards.Id, e.ConnectionId);
            Assert.Equal("Cards", e.ConnectionName);
            Assert.Equal("fake", e.ProviderId);
            Assert.True(e.ExecutedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        });
    }

    [Fact]
    public async Task What_was_never_sent_is_not_recorded_and_only_the_selection_is_kept()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards") with { IsReadOnly = true });
        var query = env.Query();

        query.Text = "DELETE FROM cards WHERE id = 1";
        await query.RunCommand.ExecuteAsync(null);
        Assert.Empty(await env.History.SearchAsync(null, 10, Token));

        query.Text = "SELECT 1; SELECT 2";
        query.SelectedText = "SELECT 2";
        await query.RunCommand.ExecuteAsync(null);
        Assert.Equal("SELECT 2", Assert.Single(await env.History.SearchAsync(null, 10, Token)).Sql);
    }

    [Fact]
    public async Task The_panel_lists_runs_newest_first_and_follows_new_ones()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var documents = env.Documents();
        var panel = env.HistoryPanel(documents);
        await panel.LoadAsync();
        Assert.True(panel.IsEmpty);
        Assert.False(panel.ClearCommand.CanExecute(null));
        Assert.False(panel.OpenSelectedCommand.CanExecute(null));

        var query = documents.Selected!;
        query.RefreshSessions();
        query.Text = "-- cards issued this year\n\n   SELECT * FROM cards";
        await query.RunCommand.ExecuteAsync(null);
        await Until(() => panel.Entries.Count == 1);

        env.Provider.Execute = static (_, token) => Failing("Invalid object name 'cardz'.", token);
        query.Text = "SELECT * FROM cardz";
        await query.RunCommand.ExecuteAsync(null);
        await Until(() => panel.Entries.Count == 2);

        Assert.False(panel.IsEmpty);
        Assert.True(panel.ClearCommand.CanExecute(null));
        var failed = panel.Entries[0];
        Assert.Equal("SELECT * FROM cardz", failed.Summary);
        Assert.True(failed.IsFailed);
        Assert.Equal("Cards · failed", failed.Detail);
        Assert.StartsWith("Invalid object name 'cardz'.", failed.ToolTip, StringComparison.Ordinal);

        var succeeded = panel.Entries[1];
        Assert.Equal("SELECT * FROM cards", succeeded.Summary);
        Assert.False(succeeded.IsFailed);
        Assert.StartsWith("Cards · 3 rows fetched · ", succeeded.Detail, StringComparison.Ordinal);
        Assert.Matches("^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}$", succeeded.When);
    }

    [Fact]
    public async Task Search_narrows_the_list_and_an_empty_result_is_not_an_empty_history()
    {
        await using var env = new TestEnvironment();
        await Seed(env, "SELECT * FROM cards", "SELECT * FROM transactions", "UPDATE cards SET balance = 0 WHERE id = 1");
        var panel = env.HistoryPanel(env.Documents());
        await panel.LoadAsync();
        Assert.Equal(3, panel.Entries.Count);

        // The connection name "Cards" would match all three.
        panel.SearchText = "select * FROM";
        await Until(() => panel.Entries.Count == 2);

        panel.SearchText = "no such text";
        await Until(() => panel.Entries.Count == 0);
        Assert.False(panel.IsEmpty);

        panel.SearchText = string.Empty;
        await Until(() => panel.Entries.Count == 3);
    }

    [Fact]
    public async Task Opening_an_entry_puts_its_text_in_a_new_tab_on_its_connection()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Logs"));
        var cards = await env.OpenAsync(TestEnvironment.Definition("Cards"));
        await env.History.AddAsync(TestEnvironment.HistoryEntry("SELECT * FROM cards\nWHERE id = 1") with { ConnectionId = cards.Id }, Token);
        await env.History.AddAsync(TestEnvironment.HistoryEntry("SELECT 1") with { ConnectionId = Guid.NewGuid() }, Token);
        var documents = env.Documents();
        var panel = env.HistoryPanel(documents);
        await panel.LoadAsync();

        panel.Selected = panel.Entries[1];
        Assert.True(panel.OpenSelectedCommand.CanExecute(null));
        panel.OpenSelectedCommand.Execute(null);

        Assert.Equal(2, documents.Documents.Count);
        Assert.Equal("SELECT * FROM cards\nWHERE id = 1", documents.Selected!.Text);
        Assert.Equal("Cards", documents.Selected.SelectedSession!.Name);

        // The connection of this run is not open any more: the text still opens.
        panel.Selected = panel.Entries[0];
        panel.OpenSelectedCommand.Execute(null);
        Assert.Equal(3, documents.Documents.Count);
        Assert.Equal("SELECT 1", documents.Selected!.Text);
    }

    [Fact]
    public async Task Delete_removes_one_entry_and_clear_asks_first()
    {
        await using var env = new TestEnvironment();
        await Seed(env, "SELECT 1", "SELECT 2", "SELECT 3");
        var panel = env.HistoryPanel(env.Documents());
        await panel.LoadAsync();

        panel.Selected = panel.Entries[1];
        await panel.DeleteSelectedCommand.ExecuteAsync(null);
        await Until(() => panel.Entries.Count == 2);
        Assert.Equal(["SELECT 3", "SELECT 1"], panel.Entries.Select(static e => e.Summary));
        Assert.Null(panel.Selected);

        env.Dialogs.Confirm = false;
        await panel.ClearCommand.ExecuteAsync(null);
        Assert.Equal(2, (await env.History.SearchAsync(null, 10, Token)).Count);
        Assert.Contains(env.Dialogs.Asked, static a => a.StartsWith("confirm:Delete the whole query history?", StringComparison.Ordinal));

        env.Dialogs.Confirm = true;
        await panel.ClearCommand.ExecuteAsync(null);
        await Until(() => panel.IsEmpty);
        Assert.Empty(await env.History.SearchAsync(null, 10, Token));
    }


    private static async Task Seed(TestEnvironment env, params string[] statements)
    {
        foreach (var sql in statements)
        {
            await env.History.AddAsync(TestEnvironment.HistoryEntry(sql), Token);
        }
    }

    /// <summary>The panel reloads in the background when the history changes.</summary>
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Token);
        }

        Assert.True(condition());
    }

    private static async IAsyncEnumerable<QueryEvent> Failing(string message, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new QueryMessage("before the error", QueryMessageSeverity.Info);
        throw new InvalidOperationException(message);
    }

    private static async IAsyncEnumerable<QueryEvent> Endless(TaskCompletionSource started, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        started.SetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }
}
