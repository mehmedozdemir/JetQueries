using System.Globalization;
using JetQueries.Presentation.ViewModels;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>The command palette, favourites in the explorer and group counts.</summary>
public sealed class PolishTests
{
    public PolishTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
    }

    [Fact]
    public void The_palette_matches_every_word_and_runs_the_selected_command()
    {
        var ran = new List<string>();
        var palette = new CommandPaletteViewModel(
        [
            new("Yeni sorgu", "Dosya", "Ctrl+N", () => ran.Add("new"), static () => true),
            new("Kaydet", "Dosya", "Ctrl+S", () => ran.Add("save"), static () => false),
            new("Sorgu geçmişi", "Araçlar", "Ctrl+Shift+H", () => ran.Add("history"), static () => true),
            new("İçe aktar", "Bağlantı", null, () => ran.Add("import"), static () => true),
        ]);

        // Available commands first.
        Assert.Equal("Kaydet", palette.Shown[^1].Title);

        palette.SearchText = "sorgu dosya";
        Assert.Equal("Yeni sorgu", Assert.Single(palette.Shown).Title);

        // Turkish-aware, case-insensitive: "içe" finds "İçe aktar".
        palette.SearchText = "içe";
        Assert.Equal("İçe aktar", palette.Selected?.Title);

        palette.SearchText = "ctrl+shift";
        Assert.True(palette.Run());
        Assert.Equal(["history"], ran);

        // A command that cannot run now does nothing.
        palette.SearchText = "kaydet";
        Assert.False(palette.Run());

        palette.SearchText = string.Empty;
        palette.Move(2);
        Assert.Equal(palette.Shown[2], palette.Selected);
        palette.Move(10);
        Assert.Equal(palette.Shown[^1], palette.Selected);
    }

    [Fact]
    public async Task Favourites_appear_at_the_top_of_the_connection_and_go_with_the_last_one()
    {
        await using var env = new TestEnvironment();
        var favorites = new FavoriteObjects(null, env.Jobs);
        var connections = new ConnectionsViewModel(env.Store, env.Manager, env.Credentials, env.Metadata, env.Jobs, env.Dialogs, favorites);
        var definition = TestEnvironment.Definition("Kartlar");
        await env.Store.SaveAsync(definition, CancellationToken.None);
        await env.Credentials.SetPasswordAsync(definition.Id, "stored", CancellationToken.None);
        await connections.LoadAsync();
        var node = (ConnectionNodeViewModel)connections.Roots[0];
        connections.Selected = node;
        await connections.ToggleConnectionCommand.ExecuteAsync(null);
        await TestEnvironment.LoadedAsync(node);

        // Cards > dbo > Tables (3) > Cards
        var database = (DatabaseObjectNodeViewModel)node.Children.First(static c => c.Name == "Cards");
        await TestEnvironment.ExpandAsync(database);
        var schema = (DatabaseObjectNodeViewModel)database.Children.First(static c => c.Name == "dbo");
        await TestEnvironment.ExpandAsync(schema);
        var tables = (CategoryNodeViewModel)schema.Children.First();
        await TestEnvironment.ExpandAsync(tables);
        Assert.Equal(3, tables.LoadedCount);
        var cards = (DatabaseObjectNodeViewModel)tables.Children.First(static c => c.Name == "Cards");
        Assert.True(cards.CanBeFavorite);
        Assert.False(cards.IsFavorite);

        connections.ToggleFavoriteCommand.Execute(cards);
        await Wait(() => node.Children.FirstOrDefault() is FavoritesNodeViewModel { LoadedCount: 1 });
        Assert.True(cards.IsFavorite);
        var group = (FavoritesNodeViewModel)node.Children[0];
        Assert.Equal("Favoriler", group.Name);
        var pinned = Assert.IsType<DatabaseObjectNodeViewModel>(Assert.Single(group.Children));
        Assert.Equal(cards.Target, pinned.Target);

        // Unpinning (from the pinned copy) removes the group with its last object.
        connections.ToggleFavoriteCommand.Execute(pinned);
        await Wait(() => node.Children.FirstOrDefault() is not FavoritesNodeViewModel);
        Assert.False(cards.IsFavorite);
        Assert.Empty(favorites.Of(node.Definition.Id));
    }

    private static async Task Wait(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
