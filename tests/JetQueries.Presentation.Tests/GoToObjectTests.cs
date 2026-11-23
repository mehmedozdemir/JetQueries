using System.Globalization;

namespace JetQueries.Presentation.Tests;

/// <summary>Go to object: finds objects of open connections by name and reveals them in the explorer.</summary>
public sealed class GoToObjectTests
{
    [Fact]
    public async Task Objects_are_found_by_part_of_their_name_and_revealed_in_the_explorer()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        await using var env = new TestEnvironment();
        var (connections, node) = await TestEnvironment.ConnectAsync(env);
        await TestEnvironment.LoadedAsync(node);

        var goTo = new GoToObjectViewModel(connections, env.Ui);
        await goTo.IndexAsync();
        Assert.False(goTo.IsIndexing);

        // Starting with the text first, shorter first; the tree itself stays collapsed.
        goTo.SearchText = "card";
        Assert.Equal(["Cards", "Cards", "CardSummary", "CardSummary"], goTo.Results.Select(static r => r.Name));
        Assert.Equal("Cards · Cards · dbo", goTo.Results[0].Location);
        Assert.False(node.Children.OfType<DatabaseObjectNodeViewModel>().First().IsExpanded);

        // Turkish letters match in any case.
        goTo.SearchText = "MÜŞTERİ";
        Assert.Equal("Müşteri İşlemleri", goTo.Results[0].Name);

        var revealed = new List<ExplorerNodeViewModel>();
        connections.RevealRequested += (_, n) => revealed.Add(n);
        var closed = false;
        goTo.CloseRequested += (_, _) => closed = true;
        goTo.OpenCommand.Execute(null);

        var target = Assert.Single(revealed);
        Assert.Same(goTo.Results[0].Node, target);
        Assert.Same(target, connections.Selected);
        Assert.All(goTo.Results[0].Ancestors, static a => Assert.True(a.IsExpanded));
        Assert.True(closed);
    }

    [Fact]
    public async Task Without_an_open_connection_it_says_so()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        await using var env = new TestEnvironment();
        var connections = env.Connections();
        await connections.LoadAsync();

        var goTo = new GoToObjectViewModel(connections, env.Ui);
        await goTo.IndexAsync();

        Assert.StartsWith("Connect to a database first", goTo.StatusText, StringComparison.Ordinal);
    }
}
