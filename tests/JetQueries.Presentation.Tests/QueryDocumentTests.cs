using System.Globalization;
using System.Runtime.CompilerServices;
using JetQueries.Core.Connections;
using JetQueries.Presentation.ViewModels;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

public sealed class QueryDocumentTests
{
    public QueryDocumentTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    [Fact]
    public async Task Run_needs_a_connection_and_some_text()
    {
        await using var env = new TestEnvironment();
        var query = env.Query();

        Assert.Empty(query.Sessions);
        Assert.False(query.RunCommand.CanExecute(null));

        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        query.RefreshSessions();
        Assert.Equal("Cards", query.SelectedSession!.Name);
        Assert.False(query.RunCommand.CanExecute(null));

        query.Text = "select 1";
        Assert.True(query.RunCommand.CanExecute(null));
        Assert.False(query.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_result_set_fills_a_grid_tab_and_messages_go_to_their_own()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var query = env.Query();
        query.Text = "SELECT * FROM cards";

        await query.RunCommand.ExecuteAsync(null);

        Assert.False(query.IsRunning);
        Assert.Equal(2, query.Tabs.Count);
        var result = Assert.IsType<QueryResultSet>(query.Tabs[0]);
        Assert.Same(result, query.SelectedTab);
        Assert.Equal("Result 1", result.Title);
        Assert.Equal(["Id", "CardNo", "Balance", "IssuedAt"], result.Columns.Select(static c => c.Header));
        Assert.Equal(3, result.RowCount);
        Assert.Equal("1250.50", result.GetText(0, 2));
        Assert.Null(result.GetText(1, 2));
        Assert.Equal("2026-01-15", result.GetText(0, 3));
        Assert.Equal("2026-02-03 14:30:05.120", result.GetText(1, 3));
        Assert.Equal("Müşteri İşlemleri", result.GetText(2, 1));
        Assert.True(result.Columns[0].IsNumeric);
        Assert.False(result.Columns[1].IsNumeric);

        Assert.Same(query.Messages, query.Tabs[^1]);
        Assert.Equal("starting", Assert.Single(query.Messages.Lines).Text);
        Assert.StartsWith("3 rows · ", query.Status, StringComparison.Ordinal);

        var request = Assert.Single(env.Provider.Executed);
        Assert.Equal("SELECT * FROM cards", request.Sql);
        Assert.Equal(QueryDocumentViewModel.MaxRows, request.MaxRows);
    }

    [Fact]
    public async Task Only_the_selection_is_run_when_there_is_one_and_a_new_run_replaces_the_old_results()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var query = env.Query();
        query.Text = "SELECT 1;\nSELECT 2;";
        await query.RunCommand.ExecuteAsync(null);

        query.SelectedText = "SELECT 2";
        await query.RunCommand.ExecuteAsync(null);

        Assert.Equal(["SELECT 1;\nSELECT 2;", "SELECT 2"], env.Provider.Executed.Select(static r => r.Sql));
        Assert.Equal(2, query.Tabs.Count);
        Assert.Single(query.Messages.Lines);
    }

    [Fact]
    public async Task Statements_without_rows_report_what_they_did()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        env.Provider.Execute = static (_, _) => Events(new StatementCompleted(12), new QueryMessage("careful", QueryMessageSeverity.Warning));
        var query = env.Query();
        query.Text = "UPDATE cards SET status = 'B' WHERE id < 13";

        await query.RunCommand.ExecuteAsync(null);

        Assert.Same(query.Messages, Assert.Single(query.Tabs));
        Assert.Equal(["12 rows affected", "careful"], query.Messages.Lines.Select(static l => l.Text));
        Assert.StartsWith("Done · ", query.Status, StringComparison.Ordinal);

        env.Provider.Execute = static (_, _) => Events();
        await query.RunCommand.ExecuteAsync(null);
        Assert.Equal("Command completed.", Assert.Single(query.Messages.Lines).Text);
    }

    [Fact]
    public async Task A_database_error_is_shown_with_the_servers_message()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        env.Provider.Execute = static (_, _) => Failing("Invalid object name 'cardz'.");
        var query = env.Query();
        query.Text = "SELECT * FROM cardz";

        await query.RunCommand.ExecuteAsync(null);

        // What arrived before the error stays; the error is the last line.
        Assert.Equal(["before the error", "Invalid object name 'cardz'."], query.Messages.Lines.Select(static l => l.Text));
        Assert.Equal([false, true], query.Messages.Lines.Select(static l => l.IsError));
        Assert.Same(query.Messages, query.SelectedTab);
        Assert.StartsWith("Failed · ", query.Status, StringComparison.Ordinal);
        Assert.False(query.IsRunning);
        Assert.True(query.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancel_stops_a_running_query()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var started = new TaskCompletionSource();
        env.Provider.Execute = (_, token) => Endless(started, token);
        var query = env.Query();
        query.Text = "WAITFOR DELAY '00:10:00'";

        var run = query.RunCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(query.IsRunning);
        Assert.False(query.RunCommand.CanExecute(null));
        Assert.True(query.CancelCommand.CanExecute(null));

        query.CancelCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(query.IsRunning);
        Assert.StartsWith("Canceled · ", query.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_only_connection_sends_nothing_that_can_change_data()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards") with { IsReadOnly = true });
        var query = env.Query();

        query.Text = "DELETE FROM cards WHERE id = 1";
        await query.RunCommand.ExecuteAsync(null);

        Assert.Empty(env.Provider.Executed);
        Assert.True(Assert.Single(query.Messages.Lines).IsError);
        Assert.Contains("read-only", query.Messages.Lines[0].Text, StringComparison.Ordinal);
        Assert.Equal("Not sent", query.Status);

        query.Text = "SELECT * FROM cards -- delete later";
        await query.RunCommand.ExecuteAsync(null);
        Assert.Single(env.Provider.Executed);
    }

    [Fact]
    public async Task Destructive_statements_on_production_are_confirmed_first()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards PROD", environment: ConnectionEnvironment.Production));
        var query = env.Query();
        query.Text = "DELETE FROM cards; DROP TABLE old_cards";

        await query.RunCommand.ExecuteAsync(null);

        Assert.Empty(env.Provider.Executed);
        var asked = Assert.Single(env.Dialogs.Asked);
        Assert.Contains("'Cards PROD'", asked, StringComparison.Ordinal);
        Assert.Contains("DELETE without WHERE, DROP", asked, StringComparison.Ordinal);
        Assert.Equal("Not sent", query.Status);

        env.Dialogs.Confirm = true;
        await query.RunCommand.ExecuteAsync(null);
        Assert.Single(env.Provider.Executed);

        // Ordinary statements are not questioned, and other environments are never asked.
        query.Text = "DELETE FROM cards WHERE id = 1";
        await query.RunCommand.ExecuteAsync(null);
        Assert.Equal(2, env.Dialogs.Asked.Count);

        await using var test = new TestEnvironment();
        await test.OpenAsync(TestEnvironment.Definition("Cards TEST", environment: ConnectionEnvironment.Test));
        var other = test.Query();
        other.Text = "DROP TABLE old_cards";
        await other.RunCommand.ExecuteAsync(null);
        Assert.Empty(test.Dialogs.Asked);
        Assert.Single(test.Provider.Executed);
    }

    [Fact]
    public async Task Query_tabs_follow_the_open_connections()
    {
        await using var env = new TestEnvironment();
        var documents = env.Documents();
        var first = Assert.Single(documents.Documents);
        Assert.Equal("Query 1", first.Title);
        Assert.Null(first.SelectedSession);

        var cards = await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var logs = await env.OpenAsync(TestEnvironment.Definition("Logs"));
        Assert.Equal(["Cards", "Logs"], first.Sessions.Select(static s => s.Name));

        documents.PreferredConnectionId = logs.Id;
        documents.NewQueryCommand.Execute(null);
        var second = documents.Selected!;
        Assert.Equal("Query 2", second.Title);
        Assert.Equal("Logs", second.SelectedSession!.Name);

        await env.Manager.CloseAsync(logs.Id);
        Assert.Equal(["Cards"], second.Sessions.Select(static s => s.Name));
        Assert.Equal("Cards", second.SelectedSession!.Name);

        documents.CloseCommand.Execute(second);
        Assert.Same(first, documents.Selected);
        documents.CloseCommand.Execute(first);
        Assert.Equal("Query 3", Assert.Single(documents.Documents).Title);
        _ = cards;
    }

    [Fact]
    public void Values_are_shown_the_same_whatever_the_windows_locale()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        var result = new QueryResultSet("r", [new("a", "x", typeof(object))]);
        result.AddRows([[1234.5m], [1.5d], [true], [new byte[] { 0xDE, 0xAD }], [new Guid("11111111-2222-3333-4444-555555555555")], [new byte[40]]]);

        Assert.Equal("1234.5", result.GetText(0, 0));
        Assert.Equal("1.5", result.GetText(1, 0));
        Assert.Equal("1", result.GetText(2, 0));
        Assert.Equal("0xDEAD", result.GetText(3, 0));
        Assert.Equal("11111111-2222-3333-4444-555555555555", result.GetText(4, 0));
        Assert.EndsWith("…", result.GetText(5, 0), StringComparison.Ordinal);
    }

    private static async IAsyncEnumerable<QueryEvent> Events(params QueryEvent[] events)
    {
        await Task.Yield();
        foreach (var item in events)
        {
            yield return item;
        }
    }

    private static async IAsyncEnumerable<QueryEvent> Failing(string message)
    {
        await Task.Yield();
        yield return new QueryMessage("before the error", QueryMessageSeverity.Info);
        throw new InvalidOperationException(message);
    }

    private static async IAsyncEnumerable<QueryEvent> Endless(TaskCompletionSource started, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        started.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }
}
