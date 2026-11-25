using System.Globalization;
using JetQueries.Core.Connections;
using JetQueries.Presentation.ViewModels;

namespace JetQueries.Presentation.Tests;

/// <summary>A connection that shows only the databases chosen for it; the start database picked from the server's list.</summary>
public sealed class VisibleDatabasesTests
{
    public VisibleDatabasesTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    [Fact]
    public async Task The_editor_reads_the_databases_and_keeps_the_checked_ones_with_the_start_database()
    {
        await using var env = new TestEnvironment();
        var definition = TestEnvironment.Definition("Cards");
        await env.Credentials.SetPasswordAsync(definition.Id, "stored", CancellationToken.None);
        var editor = env.Editor(definition);
        Assert.True(editor.CanChooseDatabases);
        Assert.True(editor.ShowAllDatabases);
        var start = Assert.IsType<SettingFieldViewModel>(editor.DefaultDatabaseField);
        Assert.True(start.IsSuggested);
        Assert.False(start.IsText);
        Assert.Null(editor.BuildDefinition().VisibleDatabases);

        editor.ShowAllDatabases = false;
        for (var i = 0; i < 100 && editor.DatabaseChoices.Count == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(["Cards", "master"], editor.DatabaseChoices.Select(static c => c.Name));
        Assert.Equal(["Cards", "master"], start.Suggestions.Select(static s => s.Value));
        Assert.Null(editor.BuildDefinition().VisibleDatabases);

        editor.DatabaseChoices[1].IsChecked = true;
        start.Value = "Cards";
        Assert.Equal(["Cards", "master"], editor.BuildDefinition().VisibleDatabases);
        editor.DatabaseFilter = "mas";
        Assert.Equal([false, true], editor.DatabaseChoices.Select(static c => c.IsVisible));

        var saved = editor.BuildDefinition() with { VisibleDatabases = ["master"] };
        var again = env.Editor(saved);
        Assert.False(again.ShowAllDatabases);
        Assert.Equal(["master"], again.DatabaseChoices.Where(static c => c.IsChecked).Select(static c => c.Name));
        Assert.True(saved.ShowsDatabase("MASTER"));
        Assert.False(saved.ShowsDatabase("Cards"));
    }

    [Fact]
    public async Task The_explorer_shows_only_the_chosen_databases_and_says_how_many_are_hidden()
    {
        await using var env = new TestEnvironment();
        var definition = TestEnvironment.Definition("Cards") with { VisibleDatabases = ["Cards"] };
        await env.Store.SaveAsync(definition, CancellationToken.None);
        await env.Credentials.SetPasswordAsync(definition.Id, "stored", CancellationToken.None);
        var connections = env.Connections();
        await connections.LoadAsync();
        var node = (ConnectionNodeViewModel)connections.Roots[0];
        connections.Selected = node;
        await connections.ToggleConnectionCommand.ExecuteAsync(null);
        await TestEnvironment.LoadedAsync(node);

        Assert.Equal(["Cards", "1 hidden — edit the connection to show more"], node.Children.Select(static c => c.Name));
        Assert.IsType<MessageNodeViewModel>(node.Children[1]);
        var session = env.Manager.Sessions.Single();
        Assert.False(session.ShowsDatabase("master"));

        // The query tab's list follows; its current database stays even when hidden.
        var query = env.Query();
        for (var i = 0; i < 100 && query.Databases.Count == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.DoesNotContain("master", query.Databases);
    }
}
