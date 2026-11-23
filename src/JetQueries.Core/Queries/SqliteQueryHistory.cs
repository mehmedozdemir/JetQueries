using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace JetQueries.Core.Queries;

/// <summary>
/// <see cref="IQueryHistory"/> on a SQLite file. Unlike the metadata cache this is the user's own data: a file
/// that cannot be read is moved aside and kept, never deleted, and a new one is started.
/// </summary>
public sealed partial class SqliteQueryHistory : IQueryHistory, IAsyncDisposable, IDisposable
{
    private const int SchemaVersion = 1;
    private const string TruncationMark = "\n-- … (cut: the rest of this script was too long to keep in the history)";

    private readonly QueryHistoryOptions _options;
    private readonly ILogger<SqliteQueryHistory> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<QueryHistoryEntry> _memory = [];
    private SqliteConnection? _connection;
    private bool _opened;
    private bool _disposed;
    private long _memoryId;

    /// <summary>Creates the store. The file is opened on first use, never on the calling thread.</summary>
    /// <param name="options">Location and limits.</param>
    /// <param name="logger">Receives a warning when the file cannot be used.</param>
    public SqliteQueryHistory(QueryHistoryOptions options, ILogger<SqliteQueryHistory> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public async Task AddAsync(QueryHistoryEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var sql = SecretRedactor.Redact(entry.Sql);
        if (sql.Length > _options.MaxSqlLength)
        {
            sql = string.Concat(sql.AsSpan(0, _options.MaxSqlLength), TruncationMark);
        }

        var stored = entry with { Sql = sql };
        await RunAsync(
            connection =>
            {
                if (connection is null)
                {
                    _memory.Insert(0, stored with { Id = ++_memoryId });
                    if (_memory.Count > _options.MaxEntries)
                    {
                        _memory.RemoveRange(_options.MaxEntries, _memory.Count - _options.MaxEntries);
                    }

                    return true;
                }

                using var insert = connection.CreateCommand();
                insert.CommandText =
                    """
                    INSERT INTO history (executed_at, connection_id, connection_name, provider_id, sql, duration_ms, outcome, row_count, error)
                    VALUES ($at, $connection, $name, $provider, $sql, $duration, $outcome, $rows, $error)
                    """;
                insert.Parameters.AddWithValue("$at", stored.ExecutedAt.ToUnixTimeMilliseconds());
                insert.Parameters.AddWithValue("$connection", stored.ConnectionId.ToString("N"));
                insert.Parameters.AddWithValue("$name", stored.ConnectionName);
                insert.Parameters.AddWithValue("$provider", stored.ProviderId);
                insert.Parameters.AddWithValue("$sql", stored.Sql);
                insert.Parameters.AddWithValue("$duration", (long)stored.Duration.TotalMilliseconds);
                insert.Parameters.AddWithValue("$outcome", (int)stored.Outcome);
                insert.Parameters.AddWithValue("$rows", (object?)stored.RowCount ?? DBNull.Value);
                insert.Parameters.AddWithValue("$error", (object?)stored.Error ?? DBNull.Value);
                insert.ExecuteNonQuery();

                // Keep the newest MaxEntries.
                using var prune = connection.CreateCommand();
                prune.CommandText = "DELETE FROM history WHERE id <= (SELECT id FROM history ORDER BY id DESC LIMIT 1 OFFSET $keep)";
                prune.Parameters.AddWithValue("$keep", _options.MaxEntries);
                prune.ExecuteNonQuery();
                return true;
            },
            cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<QueryHistoryEntry>> SearchAsync(string? search, int limit, CancellationToken cancellationToken) =>
        FindAsync(new QueryHistoryFilter(search), limit, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetConnectionNamesAsync(CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<string>>(
            connection =>
            {
                if (connection is null)
                {
                    return [.. _memory.Select(static e => e.ConnectionName).Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCulture)];
                }

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT DISTINCT connection_name FROM history";
                var names = new List<string>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    names.Add(reader.GetString(0));
                }

                return [.. names.Order(StringComparer.CurrentCulture)];
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<QueryHistoryEntry>> FindAsync(QueryHistoryFilter filter, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var search = filter.Search;
        var connectionName = string.IsNullOrEmpty(filter.ConnectionName) ? null : filter.ConnectionName;
        var failedOnly = filter.FailedOnly;
        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        return RunAsync<IReadOnlyList<QueryHistoryEntry>>(
            connection =>
            {
                if (connection is null)
                {
                    return [.. _memory
                        .Where(e => term is null || e.Sql.Contains(term, StringComparison.OrdinalIgnoreCase) || e.ConnectionName.Contains(term, StringComparison.OrdinalIgnoreCase))
                        .Where(e => connectionName is null || e.ConnectionName == connectionName)
                        .Where(e => !failedOnly || e.Outcome == QueryOutcome.Failed)
                        .Take(limit)];
                }

                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT id, executed_at, connection_id, connection_name, provider_id, sql, duration_ms, outcome, row_count, error
                    FROM history
                    WHERE ($term IS NULL OR sql LIKE $pattern ESCAPE '\' OR connection_name LIKE $pattern ESCAPE '\')
                      AND ($connection IS NULL OR connection_name = $connection)
                      AND ($failed = 0 OR outcome = $failedOutcome)
                    ORDER BY id DESC
                    LIMIT $limit
                    """;

                // The user's text is a value to find, not a pattern: its wildcard characters are escaped.
                var pattern = term is null
                    ? null
                    : "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
                command.Parameters.AddWithValue("$term", (object?)term ?? DBNull.Value);
                command.Parameters.AddWithValue("$pattern", (object?)pattern ?? DBNull.Value);
                command.Parameters.AddWithValue("$limit", limit);
                command.Parameters.AddWithValue("$connection", (object?)connectionName ?? DBNull.Value);
                command.Parameters.AddWithValue("$failed", failedOnly ? 1 : 0);
                command.Parameters.AddWithValue("$failedOutcome", (int)QueryOutcome.Failed);

                var entries = new List<QueryHistoryEntry>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    entries.Add(new QueryHistoryEntry(
                        reader.GetInt64(0),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                        Guid.TryParseExact(reader.GetString(2), "N", out var id) ? id : Guid.Empty,
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        TimeSpan.FromMilliseconds(reader.GetInt64(6)),
                        (QueryOutcome)reader.GetInt32(7),
                        reader.IsDBNull(8) ? null : reader.GetInt64(8),
                        reader.IsDBNull(9) ? null : reader.GetString(9)));
                }

                return entries;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await RunAsync(
            connection =>
            {
                if (connection is null)
                {
                    _memory.RemoveAll(e => e.Id == id);
                    return true;
                }

                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM history WHERE id = $id";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
                return true;
            },
            cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await RunAsync(
            connection =>
            {
                _memory.Clear();
                if (connection is not null)
                {
                    using var command = connection.CreateCommand();

                    // VACUUM afterwards: "clear" should also remove the text from the file, not just unlink it.
                    command.CommandText = "DELETE FROM history; VACUUM;";
                    command.ExecuteNonQuery();
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes the file without waiting for an operation in flight; prefer <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _connection, null)?.Dispose();
    }

    /// <summary>Closes the file.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs one unit of work on the thread pool, one at a time. The work gets the open connection, or null
    /// when there is no usable file and the history lives in memory for this session.
    /// </summary>
    private async Task<T> RunAsync<T>(Func<SqliteConnection?, T> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => work(_connection ??= Open()), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SqliteConnection? Open()
    {
        var path = _options.DatabasePath;
        if (path is null || _opened)
        {
            return null;
        }

        _opened = true;
        try
        {
            return OpenFile(path);
        }
        catch (SqliteException first)
        {
            // Damaged, or written by a newer version: keep it for recovery and start a fresh file.
            try
            {
                SqliteConnection.ClearAllPools();
                var aside = string.Create(CultureInfo.InvariantCulture, $"{path}.unreadable-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
                File.Move(path, aside);
                LogSetAside(path, aside, first);
                return OpenFile(path);
            }
            catch (Exception second) when (second is SqliteException or IOException or UnauthorizedAccessException)
            {
                LogMemoryOnly(path, second);
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogMemoryOnly(path, ex);
            return null;
        }
    }

    private static SqliteConnection OpenFile(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            connection.Open();
            using (var pragmas = connection.CreateCommand())
            {
                pragmas.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
                pragmas.ExecuteNonQuery();
            }

            using (var version = connection.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version";
                var found = Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (found > SchemaVersion)
                {
                    throw new SqliteException($"History file has schema version {found}; this build understands {SchemaVersion}.", 1);
                }
            }

            using var create = connection.CreateCommand();
            create.CommandText =
                $"""
                CREATE TABLE IF NOT EXISTS history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    executed_at INTEGER NOT NULL,
                    connection_id TEXT NOT NULL,
                    connection_name TEXT NOT NULL,
                    provider_id TEXT NOT NULL,
                    sql TEXT NOT NULL,
                    duration_ms INTEGER NOT NULL,
                    outcome INTEGER NOT NULL,
                    row_count INTEGER NULL,
                    error TEXT NULL
                );
                PRAGMA user_version = {SchemaVersion};
                """;
            create.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Query history file {Path} could not be read; it was moved to {Aside} and a new history was started.")]
    private partial void LogSetAside(string path, string aside, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Query history file {Path} cannot be used; the history is kept in memory for this session.")]
    private partial void LogMemoryOnly(string path, Exception exception);
}
