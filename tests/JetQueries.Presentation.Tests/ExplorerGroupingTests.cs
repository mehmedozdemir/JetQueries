using System.Globalization;
using JetQueries.Core.Connections;
using JetQueries.Core.Settings;
using JetQueries.Presentation.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Presentation.Tests;

/// <summary>The explorer grouped by environment or database type, coloured folders, several connections picked.</summary>
public sealed class ExplorerGroupingTests
{
    public ExplorerGroupingTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    private static async Task<(ConnectionsViewModel Connections, ConnectionFolders Folders, ISettingsStore Settings)> SetUpAsync(TestEnvironment env, ISettingsStore? settings = null)
    {
        settings ??= new JsonSettingsStore(new SettingsStoreOptions(), NullLogger<JsonSettingsStore>.Instance);
        foreach (var definition in new[]
        {
            TestEnvironment.Definition("Cards live", "Customer A", ConnectionEnvironment.Production),
            TestEnvironment.Definition("Cards test", "Customer A", ConnectionEnvironment.Test),
            TestEnvironment.Definition("Billing live", "Customer B", ConnectionEnvironment.Production),
            TestEnvironment.Definition("Scratch"),
        })
        {
            await env.Store.SaveAsync(definition, CancellationToken.None);
        }

        var folders = new ConnectionFolders(settings, env.Jobs);
        var connections = new ConnectionsViewModel(env.Store, env.Manager, env.Credentials, env.Metadata, env.Jobs, env.Dialogs, folders: folders);
        await connections.LoadAsync();
        return (connections, folders, settings);
    }

    private static string Tree(IEnumerable<ExplorerNodeViewModel> nodes) =>
        string.Join(" ", nodes.Select(static n => n is ConnectionNodeViewModel ? n.Name : n.Name + "[" + Tree(n.Children) + "]"));

    [Fact]
    public async Task Connections_group_by_environment_with_their_folders_and_closed_groups_are_remembered()
    {
        await using var env = new TestEnvironment();
        var (connections, folders, settings) = await SetUpAsync(env);
        Assert.True(connections.IsGroupedByFolders);
        Assert.Equal("Customer A[Cards live Cards test] Customer B[Billing live] Scratch", Tree(connections.Roots));

        await connections.GroupByCommand.ExecuteAsync("Environment");
        Assert.True(connections.IsGroupedByEnvironment);
        Assert.Equal("Production[Customer A[Cards live] Customer B[Billing live]] Test[Customer A[Cards test]] No environment[Scratch]", Tree(connections.Roots));
        var production = Assert.IsType<GroupNodeViewModel>(connections.Roots[0]);
        Assert.Equal(2, production.Count);
        Assert.True(production.IsExpanded);
        production.IsExpanded = false;

        await connections.GroupByCommand.ExecuteAsync("DatabaseType");
        Assert.Equal("fake", Assert.Single(connections.Roots).Name);
        Assert.Equal(4, ((GroupNodeViewModel)connections.Roots[0]).Count);

        // Read back by a new explorer: the grouping and the closed group come back.
        await connections.GroupByCommand.ExecuteAsync("Environment");
        await Until(() => settings.LoadAsync(ConnectionFoldersState.Section, new ConnectionFoldersState(), CancellationToken.None), static s => s.CollapsedGroups.Count == 1);
        var again = new ConnectionsViewModel(env.Store, env.Manager, env.Credentials, env.Metadata, env.Jobs, env.Dialogs, folders: new ConnectionFolders(settings, env.Jobs));
        await again.LoadAsync();
        Assert.True(again.IsGroupedByEnvironment);
        Assert.False(again.Roots[0].IsExpanded);
        Assert.True(again.Roots[1].IsExpanded);
        Assert.False(ConnectionsViewModel.CanMove(again.Roots[2].Children[0], again.Roots[0]));
        Assert.NotNull(folders);
    }

    [Fact]
    public async Task Folders_take_a_colour_and_several_connections_are_picked_and_deleted_together()
    {
        await using var env = new TestEnvironment();
        var (connections, folders, _) = await SetUpAsync(env);
        var customerA = (FolderNodeViewModel)connections.Roots[0];
        var customerB = (FolderNodeViewModel)connections.Roots[1];
        connections.Selected = customerA;
        customerB.IsMarked = true;
        connections.SetFolderColorCommand.Execute("#E5484D");
        Assert.Equal("#E5484D", customerA.Color);
        Assert.Equal("#E5484D", customerB.Color);
        Assert.Equal("#E5484D", folders.ColorOf("Customer A"));
        connections.SetFolderColorCommand.Execute("not a colour");
        Assert.Equal("#E5484D", customerA.Color);
        connections.ClearMarks();

        // Renaming keeps the colour; grouped, the folder keeps it too.
        env.Dialogs.TextAnswers.Enqueue("Customer C");
        await connections.RenameFolderCommand.ExecuteAsync(customerB);
        Assert.Equal("#E5484D", folders.ColorOf("Customer C"));
        await connections.GroupByCommand.ExecuteAsync("Environment");
        Assert.Equal("#E5484D", ((FolderNodeViewModel)connections.Roots[0].Children[1]).Color);
        await connections.GroupByCommand.ExecuteAsync("Folders");

        // Shift+click: everything shown between two nodes (inside open folders).
        connections.ExpandAllCommand.Execute(null);
        var first = connections.Roots[0].Children[0];
        connections.MarkRange(first, connections.Roots[^1]);
        Assert.Equal(["Cards live", "Cards test", "Billing live", "Scratch"], connections.MarkedConnections().Select(static c => c.Name));
        connections.RefreshPicks();
        Assert.True(connections.HasSeveralPicked);

        env.Dialogs.Confirm = true;
        await connections.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Contains(env.Dialogs.Asked, static a => a.Contains("4 connections", StringComparison.Ordinal));
        Assert.Empty(await env.Store.GetAllAsync(CancellationToken.None));
    }

    private static async Task Until<T>(Func<Task<T>> read, Func<T, bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition(await read()))
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
