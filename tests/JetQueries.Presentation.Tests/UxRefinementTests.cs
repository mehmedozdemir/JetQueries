using System.Globalization;
using JetQueries.Presentation.ViewModels;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>Details as document tabs, history filters, the shortcuts overview and the explorer's error banner.</summary>
public sealed class UxRefinementTests
{
    public UxRefinementTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
    }

    private static async Task<ObjectDetailsViewModel> DetailsAsync(TestEnvironment env, string table)
    {
        var session = env.Manager.Sessions.FirstOrDefault() ?? await OpenAsync(env);
        return new ObjectDetailsViewModel(session, new DatabaseObject(new DatabaseObjectName("Cards", "dbo", table), DatabaseObjectKinds.Table), env.Metadata, env.Jobs);
    }

    private static async Task<Core.Connections.ConnectionSession> OpenAsync(TestEnvironment env)
    {
        var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar"));
        return env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
    }

    [Fact]
    public async Task Details_open_as_tabs_beside_the_queries()
    {
        await using var env = new TestEnvironment();
        var documents = env.Documents();
        var query = documents.Selected!;
        Assert.Equal([query], documents.Tabs);

        var cards = await DetailsAsync(env, "Cards");
        documents.OpenDetails(cards);
        Assert.Equal([query, cards], documents.Tabs);
        Assert.Same(cards, documents.SelectedTab);
        Assert.Null(documents.Selected);
        Assert.Equal("Cards", cards.HeaderText);

        // A new query goes after the shown tab and is selected.
        documents.NewQueryCommand.Execute(null);
        var second = documents.Selected!;
        Assert.Equal([query, cards, second], documents.Tabs);
        Assert.Same(second, documents.SelectedTab);

        // Selecting the details tab again leaves no current query; selecting a query makes it current.
        documents.SelectedTab = cards;
        Assert.Null(documents.Selected);
        documents.SelectedTab = query;
        Assert.Same(query, documents.Selected);

        // Closing the details tab selects its neighbour; closing a query removes it from the tabs too.
        documents.SelectedTab = cards;
        await documents.CloseTabCommand.ExecuteAsync(cards);
        Assert.Equal([query, second], documents.Tabs);
        Assert.Same(query, documents.SelectedTab);
        await documents.CloseTabCommand.ExecuteAsync(second);
        Assert.Equal([query], documents.Tabs);

        // Close all closes details tabs as well.
        documents.OpenDetails(await DetailsAsync(env, "Transactions"));
        await documents.CloseAllTabsCommand.ExecuteAsync(null);
        Assert.Empty(documents.Tabs);
        Assert.Null(documents.SelectedTab);
    }

    [Fact]
    public async Task The_history_filters_by_connection_and_failures()
    {
        await using var env = new TestEnvironment();
        await env.History.AddAsync(TestEnvironment.HistoryEntry("SELECT 1"), CancellationToken.None);
        await env.History.AddAsync(TestEnvironment.HistoryEntry("SELECT 2") with { ConnectionName = "Prod" }, CancellationToken.None);
        await env.History.AddAsync(
            TestEnvironment.HistoryEntry("SELECT x") with { ConnectionName = "Prod", Outcome = Core.Queries.QueryOutcome.Failed, Error = "no", RowCount = null },
            CancellationToken.None);
        var history = env.HistoryPanel(env.Documents());
        await history.LoadAsync();

        Assert.Equal(["Tüm bağlantılar", "Cards", "Prod"], history.Connections);
        Assert.Equal(3, history.Entries.Count);

        history.SelectedConnection = "Prod";
        await history.LoadAsync();
        Assert.Equal(["SELECT x", "SELECT 2"], history.Entries.Select(static e => e.Entry.Sql));

        history.FailedOnly = true;
        await history.LoadAsync();
        Assert.Equal(["SELECT x"], history.Entries.Select(static e => e.Entry.Sql));

        history.SearchText = "nothing";
        await history.LoadAsync();
        Assert.Empty(history.Entries);
        Assert.True(history.IsFilteredEmpty);
        Assert.False(history.IsEmpty);
    }

    [Fact]
    public void The_shortcuts_overview_is_searchable()
    {
        var shortcuts = new KeyboardShortcutsViewModel();
        Assert.Contains(shortcuts.Shown, static s => s.Keys == "F5" && s.Group == "Editör");
        Assert.Equal(shortcuts.All.Count, shortcuts.Shown.Count);

        shortcuts.SearchText = "ctrl+shift+h";
        Assert.Equal("Sorgu geçmişi", Assert.Single(shortcuts.Shown).Description);

        shortcuts.SearchText = "biçim";
        Assert.Equal("Ctrl+Shift+F", Assert.Single(shortcuts.Shown).Keys);
    }

    [Fact]
    public async Task The_explorer_error_can_be_dismissed()
    {
        await using var env = new TestEnvironment();
        var connections = env.Connections();
        connections.ScriptError = "no";
        connections.DismissScriptErrorCommand.Execute(null);
        Assert.Null(connections.ScriptError);
    }
}
