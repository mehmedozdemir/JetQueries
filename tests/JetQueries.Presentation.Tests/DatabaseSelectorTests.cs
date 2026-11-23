using System.Globalization;
using JetQueries.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Presentation.Tests;

public sealed class DatabaseSelectorTests
{
    public DatabaseSelectorTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_query_window_shows_and_switches_its_current_database()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var query = env.Query();

        await Until(() => query.CanChangeDatabase && query.SelectedDatabase == "Cards");
        Assert.Equal(["Cards", "master"], query.Databases);
        Assert.False(query.SwitchesSchema);

        query.SelectedDatabase = "master";
        query.Text = "SELECT 1";
        await query.RunCommand.ExecuteAsync(null);
        Assert.Equal("master", query.SelectedDatabase);

        // A USE in the script moves the selector too.
        query.Text = "USE Cards";
        await query.RunCommand.ExecuteAsync(null);
        await Until(() => query.SelectedDatabase == "Cards");
    }

    [Fact]
    public async Task Each_window_has_its_own_connection_and_database()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        Assert.Equal(1, env.Provider.OpenCount);

        var first = env.Query();
        await Until(() => first.SelectedDatabase == "Cards");

        // The first window uses the session's connection; the second opens one of its own.
        Assert.Equal(1, env.Provider.OpenCount);
        var second = env.Query();
        await Until(() => second.SelectedDatabase == "Cards");
        Assert.Equal(2, env.Provider.OpenCount);

        second.SelectedDatabase = "master";
        await Until(() => second.SelectedDatabase == "master");
        first.Text = "SELECT 1";
        await first.RunCommand.ExecuteAsync(null);
        Assert.Equal("Cards", first.SelectedDatabase);

        // A window that closes gives its connection back; the next one reuses the session's.
        await first.CloseAsync();
        var third = env.Query();
        await Until(() => third.SelectedDatabase is not null);
        Assert.Equal(2, env.Provider.OpenCount);
    }

    [Fact]
    public async Task A_database_that_cannot_be_entered_leaves_the_selector_where_it_was()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var query = env.Query();
        await Until(() => query.SelectedDatabase == "Cards");

        query.SelectedDatabase = "nowhere";
        await Until(() => query.SelectedDatabase == "Cards");
        Assert.Contains("nowhere", query.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_without_databases_shows_no_selector()
    {
        await using var env = new TestEnvironment();
        env.Provider.SupportsDatabaseContext = false;
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var query = env.Query();
        query.Text = "SELECT 1";
        await query.RunCommand.ExecuteAsync(null);

        Assert.False(query.CanChangeDatabase);
        Assert.Empty(query.Databases);
        Assert.Single(env.Provider.Executed);
    }

    [Fact]
    public async Task New_query_on_an_explorer_node_opens_a_window_in_that_database()
    {
        await using var env = new TestEnvironment();
        var (connections, node) = await TestEnvironment.ConnectAsync(env);
        var documents = env.Documents();
        connections.NewQueryRequested += (_, request) => documents.OpenEmpty(request.ConnectionId, request.Database);

        Assert.True(connections.NewQueryCommand.CanExecute(node));
        connections.NewQueryCommand.Execute(node);
        var onConnection = documents.Selected!;
        Assert.Equal("Cards", onConnection.SelectedSession!.Name);

        var master = (DatabaseObjectNodeViewModel)node.Children.Single(c => c.Name == "master");
        connections.NewQueryCommand.Execute(master);
        var onMaster = documents.Selected!;
        Assert.NotSame(onConnection, onMaster);
        await Until(() => onMaster.SelectedDatabase == "master");

        // Not on a closed connection or a message row.
        Assert.False(connections.NewQueryCommand.CanExecute(new MessageNodeViewModel("x")));
    }

    [Fact]
    public async Task The_explorer_filter_keeps_matches_and_their_parents()
    {
        await using var env = new TestEnvironment();
        var (connections, node) = await TestEnvironment.ConnectAsync(env);
        var cards = (DatabaseObjectNodeViewModel)node.Children[0];
        await TestEnvironment.ExpandAsync(cards);
        var dbo = (DatabaseObjectNodeViewModel)cards.Children[0];
        await TestEnvironment.ExpandAsync(dbo);
        var tables = (CategoryNodeViewModel)dbo.Children[0];
        await TestEnvironment.ExpandAsync(tables);

        connections.FilterText = "müşteri";
        Assert.True(node.IsVisible);
        Assert.True(cards.IsVisible);
        Assert.True(tables.IsVisible);
        Assert.Equal(["Müşteri İşlemleri"], tables.Children.Where(static c => c.IsVisible).Select(static c => c.Name));
        Assert.False(((DatabaseObjectNodeViewModel)node.Children[1]).IsVisible);

        // Under a match everything shows; nodes loaded later are filtered when they arrive.
        connections.FilterText = "dbo";
        Assert.True(tables.IsVisible);
        Assert.All(tables.Children, static c => Assert.True(c.IsVisible));
        var views = (CategoryNodeViewModel)dbo.Children[1];
        await TestEnvironment.ExpandAsync(views);
        Assert.All(views.Children, static c => Assert.True(c.IsVisible));

        connections.FilterText = "CARDSUMMARY";
        await TestEnvironment.ExpandAsync((DatabaseObjectNodeViewModel)node.Children[1]);
        Assert.Equal(["CardSummary"], views.Children.Where(static c => c.IsVisible).Select(static c => c.Name));

        connections.FilterText = string.Empty;
        Assert.All(connections.Roots, static r => Assert.True(r.IsVisible));
    }

    [Fact]
    public async Task Settings_are_previewed_kept_on_save_and_undone_on_cancel()
    {
        var store = new JsonSettingsStore(new SettingsStoreOptions(), NullLogger<JsonSettingsStore>.Instance);
        await using var jobs = new Core.Jobs.JobQueue();
        var appearance = new RecordingAppearance();
        var theme = new RecordingTheme();

        var cancelled = new SettingsViewModel(store, jobs, appearance, theme, ["Segoe UI", "Consolas", "Arial"]);
        cancelled.UiFontSize = 15;
        Assert.Equal(15, appearance.Current.UiFontSize);
        cancelled.SelectedTheme = cancelled.Themes.Single(t => !t.Value);
        Assert.False(theme.IsDark);
        cancelled.CancelCommand.Execute(null);
        Assert.Equal(13, appearance.Current.UiFontSize);
        Assert.True(theme.IsDark);
        Assert.Equal(13, (await store.LoadAsync(AppearanceSettings.Section, new AppearanceSettings(), Token)).UiFontSize);

        var saved = new SettingsViewModel(store, jobs, appearance, theme, ["Segoe UI", "Consolas"]);
        var closed = false;
        saved.CloseRequested += (_, ok) => closed = ok;
        saved.EditorFontFamily = "Consolas";
        saved.EditorFontSize = 16;
        saved.GridFontSize = 99;
        await saved.SaveCommand.ExecuteAsync(null);
        Assert.True(closed);
        var stored = await store.LoadAsync(AppearanceSettings.Section, new AppearanceSettings(), Token);
        Assert.Equal(("Consolas", 16d, 20d), (stored.EditorFontFamily, stored.EditorFontSize, stored.GridFontSize));

        saved.ResetDefaultsCommand.Execute(null);
        Assert.Equal(new AppearanceSettings(), saved.Chosen);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Token);
        }

        Assert.True(condition());
    }

    private sealed class RecordingAppearance : IAppearanceService
    {
        public AppearanceSettings Current { get; private set; } = new();

        public void Apply(AppearanceSettings settings) => Current = settings;
    }

    private sealed class RecordingTheme : IThemeService
    {
        public bool IsDark { get; private set; } = true;

        public event EventHandler? Changed;

        public void Toggle() => SetDark(!IsDark);

        public void SetDark(bool dark)
        {
            IsDark = dark;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
