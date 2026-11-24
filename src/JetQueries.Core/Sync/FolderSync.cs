using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JetQueries.Core.Connections;
using JetQueries.Core.Library;
using Microsoft.Extensions.Logging;

namespace JetQueries.Core.Sync;

/// <summary>Where the sync keeps what both sides looked like at the last sync (the base of the merge).</summary>
public sealed class FolderSyncOptions
{
    /// <summary>Path of the state file (the user's data folder); null keeps it in memory only.</summary>
    public string? StatePath { get; set; }

    /// <summary>The name written as the last writer of the shared file (the machine's name by default).</summary>
    public string DeviceName { get; set; } = Environment.MachineName;
}

/// <summary>What a sync did.</summary>
/// <param name="Received">Connections and queries added or changed here from the shared file.</param>
/// <param name="Sent">Those of this machine written to the shared file.</param>
/// <param name="Removed">Removed here because they were deleted elsewhere.</param>
/// <param name="ConnectionsChanged">Connections changed here (the explorer reloads).</param>
/// <param name="QueriesChanged">Saved queries changed here (the library reloads).</param>
public sealed record SyncResult(int Received, int Sent, int Removed, bool ConnectionsChanged, bool QueriesChanged);

/// <summary>Synchronizes connection definitions and saved queries with other machines.</summary>
public interface ISyncService
{
    /// <summary>Merges this machine's connections and saved queries with those in <paramref name="folder"/>.</summary>
    /// <param name="folder">The shared folder (OneDrive, Dropbox, a network share).</param>
    /// <param name="cancellationToken">Cancels the sync before anything is written.</param>
    Task<SyncResult> SyncAsync(string folder, CancellationToken cancellationToken);
}

/// <summary>
/// Sync through a shared folder: one JSON file (<see cref="FileName"/>) holds every machine's connection definitions
/// and saved queries; the folder's own service (OneDrive, Dropbox, a share) carries it between machines. Each sync is
/// a three-way merge per item between this machine, the file and what both were at this machine's last sync:
/// a change on one side wins, a deletion on one side deletes, and when both sides changed the same item the newer
/// saved query wins and, for a connection, this machine's version. Secrets are never in it: a definition has no
/// password field and passwords stay in each machine's credential store.
/// </summary>
public sealed partial class FolderSyncService : ISyncService, IDisposable
{
    /// <summary>The shared file in the folder.</summary>
    public const string FileName = "JetQueries.sync.json";

    private const int FileVersion = 1;
    private const string ConnectionPrefix = "c:";
    private const string QueryPrefix = "q:";

    private readonly IConnectionStore _connections;
    private readonly ISavedQueryStore _queries;
    private readonly FolderSyncOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FolderSyncService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SyncState? _memoryState;

    /// <summary>Creates the service over this machine's stores.</summary>
    /// <param name="connections">This machine's connections.</param>
    /// <param name="queries">This machine's saved queries.</param>
    /// <param name="options">Where the base is kept.</param>
    /// <param name="time">The clock of the shared file's timestamp.</param>
    /// <param name="logger">Receives warnings about unreadable files.</param>
    public FolderSyncService(IConnectionStore connections, ISavedQueryStore queries, FolderSyncOptions options, TimeProvider time, ILogger<FolderSyncService> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _connections = connections;
        _queries = queries;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SyncResult> SyncAsync(string folder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException(folder);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(folder, FileName);
            var shared = await ReadSharedAsync(path, cancellationToken).ConfigureAwait(false);
            var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);

            // The base belongs to one folder: another folder starts from nothing (a union of both sides).
            var key = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            var known = string.Equals(state.Folder, key, StringComparison.OrdinalIgnoreCase) ? state.Hashes : [];

            var localConnections = (await _connections.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(static c => c.Id);
            var localQueries = (await _queries.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(static q => q.Id);
            var remoteConnections = shared.Connections.Where(static c => c.Id != Guid.Empty).GroupBy(static c => c.Id).ToDictionary(static g => g.Key, static g => g.Last());
            var remoteQueries = shared.Queries.Where(static q => q.Id != Guid.Empty).GroupBy(static q => q.Id).ToDictionary(static g => g.Key, static g => g.Last());

            var connections = Merge(localConnections, remoteConnections, known, ConnectionPrefix, Hash, static (local, _) => local);
            var queries = Merge(localQueries, remoteQueries, known, QueryPrefix, Hash, static (local, remote) => remote.UpdatedAt > local.UpdatedAt ? remote : local);
            cancellationToken.ThrowIfCancellationRequested();

            // This machine first: if writing the shared file fails, the next sync still has the changes to send.
            foreach (var (id, connection) in connections.Merged)
            {
                if (!localConnections.TryGetValue(id, out var local) || Hash(local) != Hash(connection))
                {
                    await _connections.SaveAsync(connection, CancellationToken.None).ConfigureAwait(false);
                }
            }

            foreach (var id in localConnections.Keys.Where(id => !connections.Merged.ContainsKey(id)))
            {
                await _connections.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false);
            }

            foreach (var (id, query) in queries.Merged)
            {
                if (!localQueries.TryGetValue(id, out var local) || Hash(local) != Hash(query))
                {
                    await _queries.SaveAsync(query, CancellationToken.None).ConfigureAwait(false);
                }
            }

            foreach (var id in localQueries.Keys.Where(id => !queries.Merged.ContainsKey(id)))
            {
                await _queries.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false);
            }

            var mergedConnections = connections.Merged.Values.OrderBy(static c => c.Id).ToList();
            var mergedQueries = queries.Merged.Values.OrderBy(static q => q.Id).ToList();
            if (connections.SharedChanged || queries.SharedChanged || !File.Exists(path))
            {
                await WriteSharedAsync(path, new SharedFile
                {
                    Version = FileVersion,
                    UpdatedAt = _time.GetUtcNow(),
                    UpdatedBy = _options.DeviceName,
                    Connections = mergedConnections,
                    Queries = mergedQueries,
                }).ConfigureAwait(false);
            }

            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var connection in mergedConnections)
            {
                hashes[ConnectionPrefix + connection.Id.ToString("N")] = Hash(connection);
            }

            foreach (var query in mergedQueries)
            {
                hashes[QueryPrefix + query.Id.ToString("N")] = Hash(query);
            }

            await WriteStateAsync(new SyncState { Folder = key, Hashes = hashes, SyncedAt = _time.GetUtcNow() }).ConfigureAwait(false);
            return new SyncResult(
                connections.Received + queries.Received,
                connections.Sent + queries.Sent,
                connections.Removed + queries.Removed,
                connections.LocalChanged,
                queries.LocalChanged);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>What a three-way merge of one kind of item gives.</summary>
    private sealed record MergeOutcome<T>(Dictionary<Guid, T> Merged, int Received, int Sent, int Removed, bool LocalChanged, bool SharedChanged);

    private static MergeOutcome<T> Merge<T>(
        Dictionary<Guid, T> local, Dictionary<Guid, T> remote, IReadOnlyDictionary<string, string> known, string prefix, Func<T, string> hash, Func<T, T, T> bothChanged)
        where T : class
    {
        var merged = new Dictionary<Guid, T>();
        int received = 0, sent = 0, removed = 0;
        bool localChanged = false, sharedChanged = false;
        var ids = local.Keys.Union(remote.Keys).Union(known.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => Guid.Parse(k.AsSpan(prefix.Length))));
        foreach (var id in ids)
        {
            var l = local.GetValueOrDefault(id);
            var r = remote.GetValueOrDefault(id);
            var hl = l is null ? null : hash(l);
            var hr = r is null ? null : hash(r);
            var b = known.GetValueOrDefault(prefix + id.ToString("N"));
            if (hl == hr)
            {
                if (l is not null)
                {
                    merged[id] = l;
                }

                continue;
            }

            T? result;
            if (hl == b)
            {
                // Changed (or deleted) only in the shared file.
                result = r;
            }
            else if (hr == b)
            {
                // Changed (or deleted) only here.
                result = l;
            }
            else
            {
                // Both: an edit beats a deletion; two edits go to the rule of the kind.
                result = l is null ? r : r is null ? l : bothChanged(l, r);
            }

            if (result is not null)
            {
                merged[id] = result;
            }

            var hm = result is null ? null : hash(result);
            if (hm != hl)
            {
                localChanged = true;
                if (result is null)
                {
                    removed++;
                }
                else
                {
                    received++;
                }
            }

            if (hm != hr)
            {
                sharedChanged = true;
                if (result is not null)
                {
                    sent++;
                }
            }
        }

        return new MergeOutcome<T>(merged, received, sent, removed, localChanged, sharedChanged);
    }

    /// <summary>A fingerprint of a connection's content (settings in a fixed order).</summary>
    internal static string Hash(ConnectionDefinition connection) => Hash(JsonSerializer.Serialize(
        connection with { Settings = new SortedDictionary<string, string>(connection.Settings.ToDictionary(), StringComparer.Ordinal) },
        SyncJson.Default.ConnectionDefinition));

    internal static string Hash(SavedQuery query) => Hash(JsonSerializer.Serialize(query, SyncJson.Default.SavedQuery));

    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    private async Task<SharedFile> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new SharedFile();
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                var file = await JsonSerializer.DeserializeAsync(stream, SyncJson.Default.SharedFile, cancellationToken).ConfigureAwait(false) ?? new SharedFile();
                if (file.Version > FileVersion)
                {
                    throw new InvalidDataException($"{path} was written by a newer JetQueries (format {file.Version}); update this one to sync.");
                }

                return file;
            }
            catch (JsonException ex)
            {
                // Never merge into (and overwrite) a file that cannot be read: it may hold another machine's work.
                LogUnreadable(path, ex);
                throw new InvalidDataException($"{path} cannot be read: {ex.Message}", ex);
            }
        }
    }

    private static async Task WriteSharedAsync(string path, SharedFile file)
    {
        // Written beside it and moved over it: a reader (another machine, the sync client) never sees half a file.
        var temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, file, SyncJson.Default.SharedFile, CancellationToken.None).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private async Task<SyncState> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (_options.StatePath is not { } path)
        {
            return _memoryState ?? new SyncState();
        }

        if (!File.Exists(path))
        {
            return new SyncState();
        }

        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync(stream, SyncJson.Default.SyncState, cancellationToken).ConfigureAwait(false) ?? new SyncState();
            }
        }
        catch (JsonException ex)
        {
            // Without a base the next sync is a union: nothing is lost, deletions elsewhere come back once.
            LogUnreadable(path, ex);
            return new SyncState();
        }
    }

    private async Task WriteStateAsync(SyncState state)
    {
        if (_options.StatePath is not { } path)
        {
            _memoryState = state;
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, state, SyncJson.Default.SyncState, CancellationToken.None).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sync file {Path} could not be read.")]
    private partial void LogUnreadable(string path, Exception exception);

    /// <summary>The shared file.</summary>
    internal sealed class SharedFile
    {
        public int Version { get; set; } = FileVersion;

        public DateTimeOffset? UpdatedAt { get; set; }

        public string? UpdatedBy { get; set; }

        public List<ConnectionDefinition> Connections { get; set; } = [];

        public List<SavedQuery> Queries { get; set; } = [];
    }

    /// <summary>This machine's base: the folder and each item's fingerprint at the last sync.</summary>
    internal sealed class SyncState
    {
        public string? Folder { get; set; }

        public DateTimeOffset? SyncedAt { get; set; }

        public Dictionary<string, string> Hashes { get; set; } = [];
    }

    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UseStringEnumConverter = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(SharedFile))]
    [JsonSerializable(typeof(SyncState))]
    [JsonSerializable(typeof(ConnectionDefinition))]
    [JsonSerializable(typeof(SavedQuery))]
    internal sealed partial class SyncJson : JsonSerializerContext;
}
