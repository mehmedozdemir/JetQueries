using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using JetQueries.Sdk;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace JetQueries.Core.Metadata;

/// <summary>
/// <see cref="IMetadataCache"/> with two layers: a dictionary that answers repeat lookups without I/O, and a
/// SQLite file that survives restarts. The file is disposable: if it is unreadable, from another schema
/// version, or cannot be opened, it is recreated or the cache continues in memory only.
/// </summary>
public sealed partial class SqliteMetadataCache : IMetadataCache, IAsyncDisposable, IDisposable
{
    // Bump when the table layout or the JSON shape changes; an older file is simply emptied.
    // 2: object names carry the package a routine is declared in.
    private const int SchemaVersion = 2;

    private const int ScopeObjects = 1;
    private const int ScopeColumns = 2;
    private const int ScopeMarks = 3;
    private const char Separator = '\u001f';

    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly ILogger<SqliteMetadataCache> _logger;
    private readonly ConcurrentDictionary<(Guid Connection, int Scope, string Key), Entry> _memory = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private bool _opened;
    private bool _disposed;

    /// <summary>Creates the cache. The file is opened on first use, never on the calling thread.</summary>
    /// <param name="options">Location of the SQLite file, or no path for a memory-only cache.</param>
    /// <param name="timeProvider">Clock used to stamp entries.</param>
    /// <param name="logger">Receives a warning when the file cannot be used.</param>
    public SqliteMetadataCache(MetadataCacheOptions options, TimeProvider timeProvider, ILogger<SqliteMetadataCache> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _path = options.DatabasePath;
        _time = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CachedMetadata<IReadOnlyList<DatabaseObject>>?> GetObjectsAsync(
        Guid connectionId, DatabaseObject? parent, DatabaseObjectKinds kinds, CancellationToken cancellationToken)
    {
        var entry = await GetAsync(connectionId, ScopeObjects, ObjectsKey(parent, kinds), MetadataJson.Default.DatabaseObjectArray, cancellationToken)
            .ConfigureAwait(false);
        return entry is null ? null : new CachedMetadata<IReadOnlyList<DatabaseObject>>((DatabaseObject[])entry.Value, entry.LoadedAt);
    }

    /// <inheritdoc />
    public Task SetObjectsAsync(
        Guid connectionId, DatabaseObject? parent, DatabaseObjectKinds kinds, IReadOnlyList<DatabaseObject> objects, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objects);
        return SetAsync(connectionId, ScopeObjects, ObjectsKey(parent, kinds), objects.ToArray(), MetadataJson.Default.DatabaseObjectArray, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CachedMetadata<IReadOnlyList<ColumnInfo>>?> GetColumnsAsync(
        Guid connectionId, DatabaseObjectName tableOrView, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tableOrView);
        var entry = await GetAsync(connectionId, ScopeColumns, NameKey(tableOrView), MetadataJson.Default.ColumnInfoArray, cancellationToken)
            .ConfigureAwait(false);
        return entry is null ? null : new CachedMetadata<IReadOnlyList<ColumnInfo>>((ColumnInfo[])entry.Value, entry.LoadedAt);
    }

    /// <inheritdoc />
    public Task SetColumnsAsync(
        Guid connectionId, DatabaseObjectName tableOrView, IReadOnlyList<ColumnInfo> columns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tableOrView);
        ArgumentNullException.ThrowIfNull(columns);
        return SetAsync(connectionId, ScopeColumns, NameKey(tableOrView), columns.ToArray(), MetadataJson.Default.ColumnInfoArray, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetManyColumnsAsync(
        Guid connectionId, IReadOnlyList<(DatabaseObjectName Name, IReadOnlyList<ColumnInfo> Columns)> tables, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var loadedAt = _time.GetUtcNow();
        var rows = new List<(string Key, string Payload)>(tables.Count);
        foreach (var (name, columns) in tables)
        {
            var value = columns.ToArray();
            var key = NameKey(name);
            _memory[(connectionId, ScopeColumns, key)] = new Entry(value, loadedAt);
            rows.Add((key, JsonSerializer.Serialize(value, MetadataJson.Default.ColumnInfoArray)));
        }

        // One transaction: thousands of single writes would each wait for the disk.
        return OnDiskAsync(
            connection =>
            {
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO entries (connection_id, scope, key, payload, loaded_at)
                    VALUES ($connection, $scope, $key, $payload, $loadedAt)
                    ON CONFLICT (connection_id, scope, key) DO UPDATE SET payload = excluded.payload, loaded_at = excluded.loaded_at
                    """;
                command.Parameters.AddWithValue("$connection", connectionId.ToString("N"));
                command.Parameters.AddWithValue("$scope", ScopeColumns);
                var key = command.Parameters.Add("$key", SqliteType.Text);
                var payload = command.Parameters.Add("$payload", SqliteType.Text);
                command.Parameters.AddWithValue("$loadedAt", loadedAt.ToUnixTimeMilliseconds());
                foreach (var row in rows)
                {
                    key.Value = row.Key;
                    payload.Value = row.Payload;
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetMarkAsync(Guid connectionId, string key, CancellationToken cancellationToken) =>
        (await GetAsync(connectionId, ScopeMarks, key, MetadataJson.Default.ColumnInfoArray, cancellationToken).ConfigureAwait(false))?.LoadedAt;

    /// <inheritdoc />
    public Task SetMarkAsync(Guid connectionId, string key, CancellationToken cancellationToken) =>
        SetAsync(connectionId, ScopeMarks, key, Array.Empty<ColumnInfo>(), MetadataJson.Default.ColumnInfoArray, cancellationToken);

    /// <inheritdoc />
    public async Task InvalidateAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        foreach (var key in _memory.Keys)
        {
            if (key.Connection == connectionId)
            {
                _memory.TryRemove(key, out _);
            }
        }

        await OnDiskAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM entries WHERE connection_id = $connection";
                command.Parameters.AddWithValue("$connection", connectionId.ToString("N"));
                command.ExecuteNonQuery();
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes the SQLite file without waiting for an operation in flight; prefer <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        // Closing a local SQLite handle is quick and does no network I/O; needed because a synchronously
        // disposed service provider cannot dispose a service that is only IAsyncDisposable.
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _connection, null)?.Dispose();
    }

    /// <summary>Closes the SQLite file. Entries already written stay on disk.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var connection = _connection;
            _connection = null;
            if (connection is not null)
            {
                await Task.Run(connection.Dispose).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    private async Task<Entry?> GetAsync<T>(
        Guid connectionId, int scope, string key, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        var memoryKey = (connectionId, scope, key);
        if (_memory.TryGetValue(memoryKey, out var entry))
        {
            return entry;
        }

        var stored = await OnDiskAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT payload, loaded_at FROM entries WHERE connection_id = $connection AND scope = $scope AND key = $key";
                command.Parameters.AddWithValue("$connection", connectionId.ToString("N"));
                command.Parameters.AddWithValue("$scope", scope);
                command.Parameters.AddWithValue("$key", key);
                using var reader = command.ExecuteReader();
                return reader.Read() ? (Payload: reader.GetString(0), LoadedAt: reader.GetInt64(1)) : default;
            },
            cancellationToken).ConfigureAwait(false);

        if (stored.Payload is null)
        {
            return null;
        }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize(stored.Payload, typeInfo);
        }
        catch (JsonException)
        {
            // Written by something else or damaged: a miss, and the next Set overwrites it.
            return null;
        }

        if (value is null)
        {
            return null;
        }

        // A Set that raced with this read is newer than what the file had: keep it.
        return _memory.GetOrAdd(memoryKey, new Entry(value, DateTimeOffset.FromUnixTimeMilliseconds(stored.LoadedAt)));
    }

    private async Task SetAsync<T>(
        Guid connectionId, int scope, string key, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        var loadedAt = _time.GetUtcNow();
        _memory[(connectionId, scope, key)] = new Entry(value, loadedAt);

        await OnDiskAsync(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO entries (connection_id, scope, key, payload, loaded_at)
                    VALUES ($connection, $scope, $key, $payload, $loadedAt)
                    ON CONFLICT (connection_id, scope, key) DO UPDATE SET payload = excluded.payload, loaded_at = excluded.loaded_at
                    """;
                command.Parameters.AddWithValue("$connection", connectionId.ToString("N"));
                command.Parameters.AddWithValue("$scope", scope);
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value, typeInfo));
                command.Parameters.AddWithValue("$loadedAt", loadedAt.ToUnixTimeMilliseconds());
                command.ExecuteNonQuery();
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one unit of SQLite work on the thread pool, one at a time. Microsoft.Data.Sqlite has no truly
    /// asynchronous I/O, so the synchronous API on a pool thread is the honest way to keep callers unblocked.
    /// Returns the default value when there is no usable file.
    /// </summary>
    private async Task<T?> OnDiskAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken)
    {
        if (_path is null)
        {
            return default;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(
                () =>
                {
                    try
                    {
                        var connection = _connection ??= Open();
                        return connection is null ? default : work(connection);
                    }
                    catch (SqliteException ex)
                    {
                        // The memory layer still holds the truth for this session.
                        LogDiskFailed(_path, ex);
                        return default;
                    }
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SqliteConnection? Open()
    {
        if (_opened)
        {
            return null;
        }

        _opened = true;
        try
        {
            return OpenFile();
        }
        catch (SqliteException first)
        {
            // Not a database, or damaged: it is only a cache, so start again with an empty file.
            LogDiskFailed(_path!, first);
            try
            {
                SqliteConnection.ClearAllPools();
                File.Delete(_path!);
                return OpenFile();
            }
            catch (Exception second) when (second is SqliteException or IOException or UnauthorizedAccessException)
            {
                LogMemoryOnly(_path!, second);
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogMemoryOnly(_path!, ex);
            return null;
        }
    }

    private SqliteConnection OpenFile()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path!));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");

            using (var version = connection.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version";
                if (Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != SchemaVersion)
                {
                    Execute(connection, "DROP TABLE IF EXISTS entries");
                }
            }

            Execute(
                connection,
                $"""
                CREATE TABLE IF NOT EXISTS entries (
                    connection_id TEXT NOT NULL,
                    scope INTEGER NOT NULL,
                    key TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    loaded_at INTEGER NOT NULL,
                    PRIMARY KEY (connection_id, scope, key)
                ) WITHOUT ROWID;
                PRAGMA user_version = {SchemaVersion};
                """);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Only called with the constant schema statements above; no user input reaches it.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    private static string ObjectsKey(DatabaseObject? parent, DatabaseObjectKinds kinds) =>
        parent is null
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Separator}{(int)kinds}")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(int)parent.Kind}{Separator}{NameKey(parent.Name)}{Separator}{(int)kinds}");

    // A missing part and an empty part must not collide, hence the explicit marker.
    private static string NameKey(DatabaseObjectName name) =>
        $"{name.Database ?? "\u0000"}{Separator}{name.Schema ?? "\u0000"}{Separator}{name.Name}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Metadata cache file {Path} failed; continuing from memory for this operation.")]
    private partial void LogDiskFailed(string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Metadata cache file {Path} cannot be used; the cache is in memory only for this session.")]
    private partial void LogMemoryOnly(string path, Exception exception);

    private sealed record Entry(object Value, DateTimeOffset LoadedAt);

    [JsonSerializable(typeof(DatabaseObject[]))]
    [JsonSerializable(typeof(ColumnInfo[]))]
    private sealed partial class MetadataJson : JsonSerializerContext;
}
