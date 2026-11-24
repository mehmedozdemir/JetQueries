using System.Globalization;
using JetQueries.Core.Connections;
using JetQueries.Presentation.ViewModels;
using JetQueries.Provider.SqlServer;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>Folders of the connection tree (create, rename, delete, drag and drop) and the import's source step.</summary>
public sealed class FolderAndImportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public FolderAndImportTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
    }

    private static async Task<(ConnectionsViewModel Connections, TestEnvironment Env, Queue<string?> Names)> SetUpAsync(TestEnvironment env)
    {
        await env.Store.SaveAsync(TestEnvironment.Definition("A", folder: "Müşteri"), Token);
        await env.Store.SaveAsync(TestEnvironment.Definition("B", folder: "Müşteri/Test"), Token);
        await env.Store.SaveAsync(TestEnvironment.Definition("C"), Token);
        var names = new Queue<string?>();
        env.Dialogs.TextAnswers = names;
        env.Dialogs.Confirm = true;
        var connections = new ConnectionsViewModel(env.Store, env.Manager, env.Credentials, env.Metadata, env.Jobs, env.Dialogs, folders: new ConnectionFolders(null, env.Jobs));
        await connections.LoadAsync();
        return (connections, env, names);
    }

    private static async Task<Dictionary<string, string>> FoldersAsync(TestEnvironment env) =>
        (await env.Store.GetAllAsync(Token)).ToDictionary(static d => d.Name, static d => d.Folder);

    private static FolderNodeViewModel Folder(IEnumerable<ExplorerNodeViewModel> nodes, string path) =>
        nodes.OfType<FolderNodeViewModel>().SelectMany(static f => (IEnumerable<FolderNodeViewModel>)[f, .. f.Children.OfType<FolderNodeViewModel>()])
            .First(f => f.Path == path);

    [Fact]
    public async Task A_new_folder_stays_while_empty_and_takes_connections_dropped_on_it()
    {
        await using var env = new TestEnvironment();
        var (connections, _, names) = await SetUpAsync(env);

        names.Enqueue("Arşiv");
        await connections.NewFolderCommand.ExecuteAsync(null);
        var archive = Folder(connections.Roots, "Arşiv");
        Assert.Same(archive, connections.Selected);
        Assert.Empty(archive.Children);

        // Drag C onto Arşiv; drag A onto B (meaning B's folder).
        var c = connections.Roots.OfType<ConnectionNodeViewModel>().Single();
        Assert.True(ConnectionsViewModel.CanMove(c, archive));
        await connections.MoveAsync(c, archive);
        var b = Folder(connections.Roots, "Müşteri/Test").Children.OfType<ConnectionNodeViewModel>().Single();
        var a = Folder(connections.Roots, "Müşteri").Children.OfType<ConnectionNodeViewModel>().Single();
        await connections.MoveAsync(a, b);
        Assert.Equal(new Dictionary<string, string> { ["A"] = "Müşteri/Test", ["B"] = "Müşteri/Test", ["C"] = "Arşiv" }, await FoldersAsync(env));

        // Dropping on empty space is the top level.
        await connections.MoveAsync(Folder(connections.Roots, "Arşiv").Children.OfType<ConnectionNodeViewModel>().Single(), null);
        Assert.Equal(string.Empty, (await FoldersAsync(env))["C"]);
    }

    [Fact]
    public async Task Folders_move_rename_and_delete_with_what_is_inside()
    {
        await using var env = new TestEnvironment();
        var (connections, _, names) = await SetUpAsync(env);

        // A folder cannot go into itself or below itself.
        var customer = Folder(connections.Roots, "Müşteri");
        Assert.False(ConnectionsViewModel.CanMove(customer, Folder(connections.Roots, "Müşteri/Test")));
        Assert.False(ConnectionsViewModel.CanMove(customer, customer));

        names.Enqueue("Arşiv");
        await connections.NewFolderCommand.ExecuteAsync(null);
        await connections.MoveAsync(Folder(connections.Roots, "Müşteri"), Folder(connections.Roots, "Arşiv"));
        Assert.Equal(new Dictionary<string, string> { ["A"] = "Arşiv/Müşteri", ["B"] = "Arşiv/Müşteri/Test", ["C"] = string.Empty }, await FoldersAsync(env));

        names.Enqueue("Eski");
        await connections.RenameFolderCommand.ExecuteAsync(Folder(connections.Roots, "Arşiv"));
        Assert.Equal("Eski/Müşteri/Test", (await FoldersAsync(env))["B"]);

        // A name with / is refused.
        names.Enqueue("a/b");
        await connections.RenameFolderCommand.ExecuteAsync(Folder(connections.Roots, "Eski"));
        Assert.Equal("Eski/Müşteri", (await FoldersAsync(env))["A"]);

        // Deleting a folder moves what is inside up one level; no connection is deleted.
        await connections.DeleteFolderCommand.ExecuteAsync(Folder(connections.Roots, "Eski/Müşteri"));
        Assert.Equal(new Dictionary<string, string> { ["A"] = "Eski", ["B"] = "Eski/Test", ["C"] = string.Empty }, await FoldersAsync(env));
    }

    [Fact]
    public async Task Import_starts_with_the_sources_and_reads_only_the_chosen_one()
    {
        await using var env = new TestEnvironment();
        var directory = Directory.CreateTempSubdirectory("jq-import2").FullName;
        var json = Path.Combine(directory, "data-sources.json");
        await File.WriteAllTextAsync(json, ConnectionImportTests.DBeaver, Token);
        IDatabaseProvider[] providers = [new SqlServerProvider()];
        var import = new ImportConnectionsViewModel(providers, env.Store, env.Jobs, () => [("DBeaver", json)]);

        await import.DiscoverAsync();
        Assert.True(import.IsChoosingSource);
        Assert.Equal(["DBeaver", "SSMS", "File"], import.Sources.Select(static s => s.Key));
        Assert.Equal("1 ayar dosyası bulundu", import.Sources[0].FoundText);
        Assert.False(import.Sources[1].IsAvailable);
        Assert.Empty(import.Items);

        // SSMS is not on this computer: nothing to list, a hint to pick its file.
        await import.ChooseSourceCommand.ExecuteAsync(import.Sources[1]);
        Assert.True(import.IsChoosingConnections);
        Assert.Empty(import.Items);
        Assert.StartsWith("Ayarları bu bilgisayarda bulunamadı", import.SourcesText, StringComparison.Ordinal);
        import.BackCommand.Execute(null);
        Assert.True(import.IsChoosingSource);

        await import.ChooseSourceCommand.ExecuteAsync(import.Sources[0]);
        Assert.NotEmpty(import.Items);

        // Under a folder of their own, without the tool's folders.
        import.TargetFolder = " DBeaver ";
        import.KeepFolders = false;
        await import.ImportCommand.ExecuteAsync(null);
        Assert.All(await env.Store.GetAllAsync(Token), static d => Assert.Equal("DBeaver", d.Folder));

        // The file source asks the view for a file.
        string? asked = null;
        import.FileRequested += (_, source) => asked = source;
        await import.ChooseSourceCommand.ExecuteAsync(import.Sources[2]);
        Assert.Equal("File", asked);
    }
}
