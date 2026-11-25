using System.Globalization;
using JetQueries.Core.Connections;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>The activity monitor: what it shows, in which order, the blocking chains, ending a session.</summary>
public sealed class ActivityMonitorTests
{
    public ActivityMonitorTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
    }

    [Fact]
    public async Task Blocking_comes_first_idle_and_system_sessions_on_request()
    {
        await using var env = new TestEnvironment();
        var (monitor, _) = await OpenAsync(env);
        await monitor.RefreshAsync();

        Assert.Null(monitor.Error);
        Assert.Equal(6, monitor.SessionCount);
        Assert.Equal(3, monitor.ActiveCount);
        Assert.Equal(2, monitor.BlockedCount);
        Assert.Equal(1, monitor.HeadBlockerCount);
        Assert.Equal(["52", "53", "54", "51", "55"], monitor.Rows.Select(static r => r.Id));
        Assert.Equal("blocking (1)", monitor.Rows[0].State);
        Assert.Equal("blocked", monitor.Rows[1].State);
        Assert.Equal("LCK_M_S · 12.4 s", monitor.Rows[1].WaitText);
        Assert.Equal("01:15", monitor.Rows.Single(r => r.Id == "55").ElapsedText);
        Assert.Equal("12,345", monitor.Rows.Single(r => r.Id == "55").Cell(12));

        // Who blocks whom: 52 holds what 53 waits for, 53 what 54 waits for.
        var chain = Assert.Single(monitor.BlockingChains);
        Assert.Equal("52", chain.Row.Id);
        Assert.Equal("52 · ali@PC-2 · idle, transaction open", chain.Text);
        Assert.Equal("53", Assert.Single(chain.Children).Row.Id);
        Assert.Equal("54", Assert.Single(chain.Children[0].Children).Row.Id);

        monitor.ShowIdle = true;
        Assert.Contains(monitor.Rows, static r => r.Id == "56");
        monitor.ShowSystem = true;
        Assert.Contains(monitor.Rows, static r => r.Id == "5");

        monitor.FilterText = "izmir";
        Assert.Equal(["54"], monitor.Rows.Select(static r => r.Id));
        monitor.FilterText = string.Empty;
    }

    [Fact]
    public async Task A_session_is_ended_after_asking_twice_on_production_never_the_monitors_own()
    {
        await using var env = new TestEnvironment();
        var (monitor, _) = await OpenAsync(env, ConnectionEnvironment.Production);
        await monitor.RefreshAsync();

        var own = monitor.Rows.Single(static r => r.Id == "51");
        Assert.True(own.IsOwn);
        Assert.False(monitor.KillCommand.CanExecute(own));

        var head = monitor.Rows[0];
        env.Dialogs.Confirm = false;
        await monitor.KillCommand.ExecuteAsync(head);
        Assert.Empty(env.Provider.Killed);

        env.Dialogs.Confirm = true;
        env.Dialogs.Asked.Clear();
        await monitor.KillCommand.ExecuteAsync(head);
        Assert.Equal(["52"], env.Provider.Killed);
        Assert.Equal(2, env.Dialogs.Asked.Count);
        Assert.Contains("PRODUCTION", env.Dialogs.Asked[1], StringComparison.Ordinal);

        // Read again: 52 is gone, so 53 now heads what is left of the chain.
        Assert.DoesNotContain(monitor.Rows, static r => r.Id == "52");
        Assert.Equal("53", Assert.Single(monitor.BlockingChains).Row.Id);
    }

    [Fact]
    public async Task Read_only_connections_end_nothing_and_SQL_opens_in_a_query_tab()
    {
        await using var env = new TestEnvironment();
        var (monitor, definition) = await OpenAsync(env, readOnly: true);
        await monitor.RefreshAsync();

        Assert.False(monitor.CanKillSessions);
        Assert.False(monitor.KillCommand.CanExecute(monitor.Rows[0]));

        NewQueryRequest? asked = null;
        monitor.QueryRequested += (_, request) => asked = request;
        monitor.Selected = monitor.Rows.Single(static r => r.Id == "55");
        monitor.OpenSqlCommand.Execute(null);
        Assert.NotNull(asked);
        Assert.Equal(definition.Id, asked.ConnectionId);
        Assert.Equal("Sales", asked.Database);
        Assert.Equal("SELECT SUM(Total) FROM dbo.Orders", asked.Text);

        // The chosen session stays chosen across reads.
        await monitor.RefreshAsync();
        Assert.Equal("55", monitor.Selected?.Id);
    }

    [Fact]
    public async Task The_explorer_and_the_documents_open_one_monitor_per_connection()
    {
        await using var env = new TestEnvironment();
        env.Provider.Activity = FakeActivity.Snapshot();
        var definition = TestEnvironment.Definition("Kartlar");
        await env.Store.SaveAsync(definition, TestContext.Current.CancellationToken);
        await env.Credentials.SetPasswordAsync(definition.Id, "x", TestContext.Current.CancellationToken);
        var connections = env.Connections();
        await connections.LoadAsync();
        var node = connections.Roots.OfType<ConnectionNodeViewModel>().Single();
        Assert.False(node.CanMonitor);
        connections.Selected = node;
        await connections.ToggleConnectionCommand.ExecuteAsync(null);
        node = connections.Roots.OfType<ConnectionNodeViewModel>().Single();
        Assert.True(node.IsConnected, "not connected");
        Assert.True(node.CanMonitor);

        var documents = env.Documents();
        connections.ActivityRequested += (_, monitor) => documents.OpenActivity(monitor);
        connections.OpenActivityCommand.Execute(node);
        connections.OpenActivityCommand.Execute(node);
        var monitor = Assert.Single(documents.Tabs.OfType<ActivityMonitorViewModel>());
        Assert.Same(monitor, documents.SelectedTab);
        Assert.Equal("Activity · Kartlar", monitor.HeaderText);

        await documents.CloseTabCommand.ExecuteAsync(monitor);
        Assert.Empty(documents.Tabs.OfType<ActivityMonitorViewModel>());
    }

    private static async Task<(ActivityMonitorViewModel Monitor, ConnectionDefinition Definition)> OpenAsync(
        TestEnvironment env, ConnectionEnvironment environment = ConnectionEnvironment.Test, bool readOnly = false)
    {
        env.Provider.Activity = FakeActivity.Snapshot();
        var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar", environment: environment) with { IsReadOnly = readOnly });
        var session = env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
        return (new ActivityMonitorViewModel(session, env.Jobs, env.Dialogs), definition);
    }
}
