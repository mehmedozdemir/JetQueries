using System.Globalization;
using JetQueries.Desktop.ViewModels;
using JetQueries.Sdk;

namespace JetQueries.Desktop.Tests;

public sealed class ObjectExplorerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ObjectExplorerTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    [Fact]
    public async Task Connecting_shows_databases_and_each_level_loads_when_expanded()
    {
        await using var env = new TestEnvironment();
        var (connections, node) = await ConnectAsync(env);

        Assert.True(node.IsExpanded);
        Assert.Equal(["Cards", "master"], node.Children.Select(static c => c.Name));
        Assert.Equal(["objects:(root):Database"], env.Provider.MetadataCalls);

        // Nothing below is asked for until the user opens it.
        var cards = (DatabaseObjectNodeViewModel)node.Children[0];
        Assert.False(cards.IsExpanded);
        Assert.IsType<MessageNodeViewModel>(Assert.Single(cards.Children));

        await ExpandAsync(cards);
        Assert.Equal(["dbo", "kart"], cards.Children.Select(static c => c.Name));

        var dbo = (DatabaseObjectNodeViewModel)cards.Children[0];
        await ExpandAsync(dbo);
        Assert.Equal(["Tables", "Views", "Procedures", "Functions", "Sequences", "Synonyms", "Types"], dbo.Children.Select(static c => c.Name));

        var tables = (CategoryNodeViewModel)dbo.Children[0];
        await ExpandAsync(tables);
        Assert.Equal(["Cards", "Müşteri İşlemleri", "Transactions"], tables.Children.Select(static c => c.Name));

        var table = (DatabaseObjectNodeViewModel)tables.Children[0];
        Assert.Equal(new DatabaseObjectName("Cards", "dbo", "Cards"), table.Target.Name);
        await ExpandAsync(table);
        var columns = table.Children.Cast<ColumnNodeViewModel>().ToList();
        Assert.Equal(["Id", "CardNo", "Balance"], columns.Select(static c => c.Name));
        Assert.True(columns[0].IsPrimaryKey);
        Assert.Equal("decimal(12,2)", columns[2].DataType);
        Assert.True(columns[2].IsNullable);

        Assert.Equal(
            ["objects:(root):Database", "objects:Cards:Schema", "objects:Cards/dbo:Table", "columns:Cards"],
            env.Provider.MetadataCalls);
        Assert.True(connections.ReloadSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Leaves_and_empty_groups_are_shown_as_such()
    {
        await using var env = new TestEnvironment();
        var (_, node) = await ConnectAsync(env);
        var dbo = await OpenSchemaAsync(node);

        var procedures = (CategoryNodeViewModel)dbo.Children[2];
        await ExpandAsync(procedures);
        var procedure = (DatabaseObjectNodeViewModel)Assert.Single(procedures.Children);
        Assert.Empty(procedure.Children);

        var types = (CategoryNodeViewModel)dbo.Children[^1];
        await ExpandAsync(types);
        Assert.Equal("(empty)", Assert.IsType<MessageNodeViewModel>(Assert.Single(types.Children)).Name);
    }

    [Fact]
    public async Task Expanding_again_or_reconnecting_answers_from_the_cache_and_refresh_asks_the_database()
    {
        await using var env = new TestEnvironment();
        var (connections, node) = await ConnectAsync(env);
        var cards = (DatabaseObjectNodeViewModel)node.Children[0];
        await ExpandAsync(cards);
        cards.IsExpanded = false;
        cards.IsExpanded = true;
        await TestEnvironment.LoadedAsync(cards);
        Assert.Equal(2, env.Provider.MetadataCalls.Count);

        // Disconnect and connect again: the tree fills without a single metadata query.
        await connections.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Empty(node.Children);
        Assert.False(node.IsConnected);
        await connections.ToggleConnectionCommand.ExecuteAsync(null);
        await TestEnvironment.LoadedAsync(node);
        cards = (DatabaseObjectNodeViewModel)node.Children[0];
        await ExpandAsync(cards);
        Assert.Equal(["dbo", "kart"], cards.Children.Select(static c => c.Name));
        Assert.Equal(2, env.Provider.MetadataCalls.Count);

        connections.Selected = cards;
        await connections.ReloadSelectedCommand.ExecuteAsync(null);
        Assert.Equal(3, env.Provider.MetadataCalls.Count);
        Assert.Equal("objects:Cards:Schema", env.Provider.MetadataCalls[^1]);
    }

    [Fact]
    public async Task A_failed_load_is_shown_in_the_tree_and_can_be_retried()
    {
        await using var env = new TestEnvironment();
        var (connections, node) = await ConnectAsync(env);
        var cards = (DatabaseObjectNodeViewModel)node.Children[0];
        env.Provider.MetadataError = "The server principal is not able to access the database.";

        await ExpandAsync(cards);

        var message = Assert.IsType<MessageNodeViewModel>(Assert.Single(cards.Children));
        Assert.True(message.IsError);
        Assert.Equal("The server principal is not able to access the database.", message.Name);
        Assert.False(cards.IsLoading);

        env.Provider.MetadataError = null;
        connections.Selected = cards;
        await connections.ReloadSelectedCommand.ExecuteAsync(null);
        Assert.Equal(["dbo", "kart"], cards.Children.Select(static c => c.Name));
    }

    [Fact]
    public async Task The_tree_follows_the_providers_capabilities()
    {
        await using var env = new TestEnvironment();
        env.Provider.Capabilities = ProviderCapabilities.Schemas | ProviderCapabilities.Routines | ProviderCapabilities.Packages |
            ProviderCapabilities.Synonyms | ProviderCapabilities.UserTypes;
        var (_, oracleLike) = await ConnectAsync(env);

        // No database level: schemas come first, and packages appear instead of sequences.
        Assert.Equal(["dbo", "kart"], oracleLike.Children.Select(static c => c.Name));
        var schema = (DatabaseObjectNodeViewModel)oracleLike.Children[0];
        await ExpandAsync(schema);
        Assert.Equal(["Tables", "Views", "Procedures", "Functions", "Packages", "Synonyms", "Types"], schema.Children.Select(static c => c.Name));

        await using var flat = new TestEnvironment();
        flat.Provider.Capabilities = ProviderCapabilities.None;
        var (_, sqliteLike) = await ConnectAsync(flat);
        Assert.Equal(["Tables", "Views"], sqliteLike.Children.Select(static c => c.Name));
        var tables = (CategoryNodeViewModel)sqliteLike.Children[0];
        await ExpandAsync(tables);
        Assert.Equal(3, tables.Children.Count);
    }

    [Fact]
    public async Task Refresh_is_only_offered_where_there_is_something_to_load()
    {
        await using var env = new TestEnvironment();
        var definition = TestEnvironment.Definition("Cards");
        await env.Store.SaveAsync(definition, Token);
        var connections = env.Connections();
        await connections.LoadAsync();

        Assert.False(connections.ReloadSelectedCommand.CanExecute(null));
        connections.Selected = connections.Roots[0];
        Assert.False(connections.ReloadSelectedCommand.CanExecute(null));
    }

    internal static async Task<(ConnectionsViewModel Connections, ConnectionNodeViewModel Node)> ConnectAsync(TestEnvironment env)
    {
        var definition = TestEnvironment.Definition("Cards");
        await env.Store.SaveAsync(definition, Token);
        await env.Credentials.SetPasswordAsync(definition.Id, "stored", Token);
        var connections = env.Connections();
        await connections.LoadAsync();
        var node = (ConnectionNodeViewModel)connections.Roots[0];
        connections.Selected = node;
        await connections.ToggleConnectionCommand.ExecuteAsync(null);
        await TestEnvironment.LoadedAsync(node);
        Assert.True(node.IsConnected, node.Error);
        return (connections, node);
    }

    internal static async Task ExpandAsync(LazyNodeViewModel node)
    {
        node.IsExpanded = true;
        await TestEnvironment.LoadedAsync(node);
    }

    private static async Task<DatabaseObjectNodeViewModel> OpenSchemaAsync(ConnectionNodeViewModel node)
    {
        var cards = (DatabaseObjectNodeViewModel)node.Children[0];
        await ExpandAsync(cards);
        var dbo = (DatabaseObjectNodeViewModel)cards.Children[0];
        await ExpandAsync(dbo);
        return dbo;
    }
}
