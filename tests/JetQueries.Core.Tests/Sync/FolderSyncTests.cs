using JetQueries.Core.Connections;
using JetQueries.Core.Library;
using JetQueries.Core.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Core.Tests.Sync;

/// <summary>Two machines syncing connections and saved queries through one shared folder.</summary>
public sealed class FolderSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "sync-tests", Guid.NewGuid().ToString("N"));
    private readonly string _shared;

    public FolderSyncTests()
    {
        _shared = Path.Combine(_root, "OneDrive", "JetQueries");
        Directory.CreateDirectory(_shared);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task What_one_machine_adds_reaches_the_other_without_secrets()
    {
        using var a = new Machine(_root, "A");
        using var b = new Machine(_root, "B");
        var connection = Connection("Kartlar Prod") with { Ssh = new SshTunnelSettings { Host = "bastion", UserName = "ops", SavePassword = true } };
        await a.Connections.SaveAsync(connection, Token);
        await a.Queries.SaveAsync(Query("Aylık rapor", DateTimeOffset.UtcNow), Token);

        var sent = await a.Sync.SyncAsync(_shared, Token);
        var received = await b.Sync.SyncAsync(_shared, Token);

        Assert.Equal(2, sent.Sent);
        Assert.Equal(2, received.Received);
        Assert.True(received.ConnectionsChanged);
        Assert.True(received.QueriesChanged);
        var copy = Assert.Single(await b.Connections.GetAllAsync(Token));
        Assert.Equal(connection.Settings, copy.Settings);
        Assert.Equal("bastion", copy.Ssh!.Host);
        Assert.Equal("Aylık rapor", Assert.Single(await b.Queries.GetAllAsync(Token)).Name);

        // The file has no place for a password; nothing like one is in it.
        var text = await File.ReadAllTextAsync(Path.Combine(_shared, FolderSyncService.FileName), Token);
        Assert.DoesNotContain("\"password\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"updatedBy\": \"A\"", text, StringComparison.Ordinal);

        // Nothing changed: nothing moves.
        var again = await b.Sync.SyncAsync(_shared, Token);
        Assert.Equal(new SyncResult(0, 0, 0, false, false), again);
    }

    [Fact]
    public async Task Edits_and_deletions_travel_both_ways()
    {
        using var a = new Machine(_root, "A");
        using var b = new Machine(_root, "B");
        var connection = Connection("Kartlar");
        var query = Query("Bakiye", DateTimeOffset.UtcNow.AddHours(-1));
        await a.Connections.SaveAsync(connection, Token);
        await a.Queries.SaveAsync(query, Token);
        await a.Sync.SyncAsync(_shared, Token);
        await b.Sync.SyncAsync(_shared, Token);

        await b.Connections.SaveAsync(connection with { Name = "Kartlar (test)", Folder = "Banka" }, Token);
        await b.Queries.DeleteAsync(query.Id, Token);
        await b.Sync.SyncAsync(_shared, Token);
        var result = await a.Sync.SyncAsync(_shared, Token);

        Assert.Equal(1, result.Removed);
        Assert.Equal("Kartlar (test)", Assert.Single(await a.Connections.GetAllAsync(Token)).Name);
        Assert.Empty(await a.Queries.GetAllAsync(Token));

        // A deletes the connection; B learns it.
        await a.Connections.DeleteAsync(connection.Id, Token);
        await a.Sync.SyncAsync(_shared, Token);
        await b.Sync.SyncAsync(_shared, Token);
        Assert.Empty(await b.Connections.GetAllAsync(Token));
    }

    [Fact]
    public async Task When_both_change_the_same_item_the_newer_query_and_the_syncing_machines_connection_win()
    {
        using var a = new Machine(_root, "A");
        using var b = new Machine(_root, "B");
        var connection = Connection("Kartlar");
        var query = Query("Rapor", DateTimeOffset.UtcNow.AddHours(-2));
        await a.Connections.SaveAsync(connection, Token);
        await a.Queries.SaveAsync(query, Token);
        await a.Sync.SyncAsync(_shared, Token);
        await b.Sync.SyncAsync(_shared, Token);

        await a.Connections.SaveAsync(connection with { Name = "A'nın adı" }, Token);
        await a.Queries.SaveAsync(query with { Sql = "SELECT 'newer'", UpdatedAt = DateTimeOffset.UtcNow }, Token);
        await b.Connections.SaveAsync(connection with { Name = "B'nin adı" }, Token);
        await b.Queries.SaveAsync(query with { Sql = "SELECT 'older'", UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-30) }, Token);

        await a.Sync.SyncAsync(_shared, Token);
        await b.Sync.SyncAsync(_shared, Token);
        await a.Sync.SyncAsync(_shared, Token);

        // Both machines end with the same: B synced last, so its connection; the newer query (A's).
        foreach (var machine in new[] { a, b })
        {
            Assert.Equal("B'nin adı", Assert.Single(await machine.Connections.GetAllAsync(Token)).Name);
            Assert.Equal("SELECT 'newer'", Assert.Single(await machine.Queries.GetAllAsync(Token)).Sql);
        }
    }

    [Fact]
    public async Task An_edit_beats_a_deletion_elsewhere()
    {
        using var a = new Machine(_root, "A");
        using var b = new Machine(_root, "B");
        var connection = Connection("Kartlar");
        await a.Connections.SaveAsync(connection, Token);
        await a.Sync.SyncAsync(_shared, Token);
        await b.Sync.SyncAsync(_shared, Token);

        await a.Connections.DeleteAsync(connection.Id, Token);
        await b.Connections.SaveAsync(connection with { IsReadOnly = true }, Token);
        await a.Sync.SyncAsync(_shared, Token);
        await b.Sync.SyncAsync(_shared, Token);
        await a.Sync.SyncAsync(_shared, Token);

        Assert.True(Assert.Single(await a.Connections.GetAllAsync(Token)).IsReadOnly);
        Assert.Single(await b.Connections.GetAllAsync(Token));
    }

    [Fact]
    public async Task A_new_folder_or_a_first_sync_merges_both_sides()
    {
        using var a = new Machine(_root, "A");
        using var b = new Machine(_root, "B");
        await a.Connections.SaveAsync(Connection("A'nın"), Token);
        await b.Connections.SaveAsync(Connection("B'nin"), Token);
        await a.Sync.SyncAsync(_shared, Token);
        await b.Sync.SyncAsync(_shared, Token);
        await a.Sync.SyncAsync(_shared, Token);

        Assert.Equal(2, (await a.Connections.GetAllAsync(Token)).Count);
        Assert.Equal(2, (await b.Connections.GetAllAsync(Token)).Count);

        // Another folder: its file starts with this machine's items; nothing here is deleted.
        var other = Path.Combine(_root, "Dropbox");
        Directory.CreateDirectory(other);
        var result = await a.Sync.SyncAsync(other, Token);
        Assert.Equal(0, result.Removed);
        Assert.Equal(2, result.Sent);
    }

    [Fact]
    public async Task An_unreadable_shared_file_is_left_alone()
    {
        using var a = new Machine(_root, "A");
        await a.Connections.SaveAsync(Connection("Kartlar"), Token);
        var path = Path.Combine(_shared, FolderSyncService.FileName);
        await File.WriteAllTextAsync(path, "{ not json", Token);

        await Assert.ThrowsAsync<InvalidDataException>(() => a.Sync.SyncAsync(_shared, Token));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(path, Token));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => a.Sync.SyncAsync(Path.Combine(_root, "missing"), Token));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ConnectionDefinition Connection(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = "sqlserver",
        Settings = new Dictionary<string, string> { ["server"] = "db01", ["database"] = "Cards" },
        UserName = "app",
        SavePassword = true,
        Environment = ConnectionEnvironment.Production,
    };

    private static SavedQuery Query(string name, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Folder = "Raporlar",
        Sql = "SELECT * FROM Cards WHERE City = :city",
        CreatedAt = at,
        UpdatedAt = at,
    };

    /// <summary>One machine: its own stores and its own sync base.</summary>
    private sealed class Machine : IDisposable
    {
        public Machine(string root, string name)
        {
            var local = Path.Combine(root, name);
            Connections = new JsonConnectionStore(new ConnectionStoreOptions { FilePath = Path.Combine(local, "connections.json") }, NullLogger<JsonConnectionStore>.Instance);
            Queries = new JsonSavedQueryStore(new SavedQueryStoreOptions { FilePath = Path.Combine(local, "saved-queries.json") }, NullLogger<JsonSavedQueryStore>.Instance);
            Sync = new FolderSyncService(
                Connections, Queries, new FolderSyncOptions { StatePath = Path.Combine(local, "sync-state.json"), DeviceName = name }, TimeProvider.System,
                NullLogger<FolderSyncService>.Instance);
        }

        public JsonConnectionStore Connections { get; }

        public JsonSavedQueryStore Queries { get; }

        public FolderSyncService Sync { get; }

        public void Dispose()
        {
            Sync.Dispose();
            Queries.Dispose();
            Connections.Dispose();
        }
    }
}
