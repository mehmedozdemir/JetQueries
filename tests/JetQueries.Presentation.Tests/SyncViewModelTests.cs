using System.Globalization;
using JetQueries.Core.Connections;
using JetQueries.Core.Library;
using JetQueries.Core.Sync;
using JetQueries.Presentation.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Presentation.Tests;

/// <summary>The sync settings page and its runs.</summary>
public sealed class SyncViewModelTests
{
    public SyncViewModelTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sync_runs_when_on_reports_what_it_did_and_says_why_it_failed()
    {
        await using var env = new TestEnvironment();
        var root = Path.Combine(AppContext.BaseDirectory, "sync-vm-tests", Guid.NewGuid().ToString("N"));
        var shared = Path.Combine(root, "shared");
        Directory.CreateDirectory(shared);
        using var queries = new JsonSavedQueryStore(new SavedQueryStoreOptions(), NullLogger<JsonSavedQueryStore>.Instance);
        using var service = new FolderSyncService(env.Store, queries, new FolderSyncOptions(), TimeProvider.System, NullLogger<FolderSyncService>.Instance);
        using var sync = new SyncViewModel(service, env.Jobs, env.Ui);

        // Off: nothing happens at start-up.
        await sync.StartAsync();
        Assert.False(File.Exists(Path.Combine(shared, FolderSyncService.FileName)));
        Assert.False(sync.SyncNowCommand.CanExecute(null));

        // Another machine left a connection in the folder.
        using var otherQueries = new JsonSavedQueryStore(new SavedQueryStoreOptions(), NullLogger<JsonSavedQueryStore>.Instance);
        using var otherConnections = new JsonConnectionStore(new ConnectionStoreOptions(), NullLogger<JsonConnectionStore>.Instance);
        await otherConnections.SaveAsync(TestEnvironment.Definition("Kartlar"), Token);
        using var other = new FolderSyncService(otherConnections, otherQueries, new FolderSyncOptions(), TimeProvider.System, NullLogger<FolderSyncService>.Instance);
        await other.SyncAsync(shared, Token);

        SyncResult? applied = null;
        sync.Applied += (_, result) => applied = result;
        sync.Enabled = true;
        sync.Folder = shared;
        await sync.SyncIfOnAsync();

        Assert.False(sync.Failed);
        Assert.Contains("1 received", sync.Status, StringComparison.Ordinal);
        Assert.True(applied?.ConnectionsChanged);
        Assert.Equal("Kartlar", Assert.Single(await env.Store.GetAllAsync(Token)).Name);

        // An unreachable folder is said, not thrown.
        sync.Folder = Path.Combine(root, "offline");
        await sync.SyncNowCommand.ExecuteAsync(null);
        Assert.True(sync.Failed);
        Assert.Contains("not reachable", sync.Status, StringComparison.Ordinal);
    }
}
