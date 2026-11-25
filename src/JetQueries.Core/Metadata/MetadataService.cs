using JetQueries.Core.Connections;
using JetQueries.Sdk;

namespace JetQueries.Core.Metadata;

/// <summary>
/// Metadata of an open session, answered from the cache when it has a recent answer and from the database
/// otherwise. This is what the object explorer and IntelliSense ask.
/// </summary>
public interface IMetadataService
{
    /// <summary>Lists the objects directly under <paramref name="parent"/>.</summary>
    /// <param name="session">The open session.</param>
    /// <param name="parent">The container, or <see langword="null"/> for the root of the connection.</param>
    /// <param name="kinds">The kinds of objects wanted.</param>
    /// <param name="refresh"><see langword="true"/> to ask the database even if the cache has an answer.</param>
    /// <param name="cancellationToken">Cancels the lookup and the database call.</param>
    /// <param name="database">
    /// The database a query window has selected, for providers whose metadata sees one database only
    /// (<see cref="ConnectionSession.HasMetadataPerDatabase"/>); null for the session's own.
    /// </param>
    Task<IReadOnlyList<DatabaseObject>> GetObjectsAsync(
        ConnectionSession session, DatabaseObject? parent, DatabaseObjectKinds kinds, bool refresh, CancellationToken cancellationToken, string? database = null);

    /// <summary>Gets the columns of a table or view.</summary>
    /// <param name="session">The open session.</param>
    /// <param name="tableOrView">The table or view.</param>
    /// <param name="refresh"><see langword="true"/> to ask the database even if the cache has an answer.</param>
    /// <param name="cancellationToken">Cancels the lookup and the database call.</param>
    /// <param name="database">As for <see cref="GetObjectsAsync"/>.</param>
    Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(
        ConnectionSession session, DatabaseObjectName tableOrView, bool refresh, CancellationToken cancellationToken, string? database = null);

    /// <summary>
    /// Reads the columns of every table and view of a schema in one round trip, when the provider can
    /// (<see cref="IBulkColumnMetadataProvider"/>), and caches them under the provider's names and under
    /// <paramref name="lookupName"/>. Skipped while an earlier read of the same schema is fresh.
    /// </summary>
    /// <param name="session">The open session.</param>
    /// <param name="schema">The schema, or <see langword="null"/> for the connection's current one.</param>
    /// <param name="lookupName">The name a language service asks a table's columns by.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <param name="database">As for <see cref="GetObjectsAsync"/>.</param>
    /// <returns>How many tables and views were read; 0 when skipped.</returns>
    Task<int> PreloadColumnsAsync(
        ConnectionSession session, DatabaseObject? schema, Func<DatabaseObjectName, DatabaseObjectName> lookupName, CancellationToken cancellationToken, string? database = null);
}

/// <summary>Default <see cref="IMetadataService"/>.</summary>
/// <param name="cache">Where answers are remembered.</param>
/// <param name="timeProvider">Clock used to judge the age of cached answers.</param>
public sealed class MetadataService(IMetadataCache cache, TimeProvider timeProvider) : IMetadataService
{
    /// <summary>
    /// How long a cached answer is used without asking. Schemas change rarely and "Refresh" is one click away;
    /// a stale list is cheaper than a slow tree on every start.
    /// </summary>
    public static TimeSpan MaxAge { get; } = TimeSpan.FromHours(12);

    private const char Sep = '\u001f';
    private readonly Dictionary<string, Task> _reading = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Task> _preloads = [];

    /// <inheritdoc />
    public async Task<IReadOnlyList<DatabaseObject>> GetObjectsAsync(
        ConnectionSession session, DatabaseObject? parent, DatabaseObjectKinds kinds, bool refresh, CancellationToken cancellationToken, string? database = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var other = await session.OtherMetadataDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        var connectionId = session.MetadataCacheId(other);
        if (!refresh)
        {
            // A cache hit needs neither the metadata connection nor its turn in the queue.
            var cached = await cache.GetObjectsAsync(connectionId, parent, kinds, cancellationToken).ConfigureAwait(false);
            if (cached is not null && timeProvider.GetUtcNow() - cached.LoadedAt < MaxAge)
            {
                return cached.Value;
            }
        }

        // Several asking for the same list at once (completion while the explorer loads, a request per keystroke)
        // share one read instead of queueing the same query on the metadata connection.
        return await ReadOnceAsync<IReadOnlyList<DatabaseObject>>(
            $"o{Sep}{connectionId:N}{Sep}{(int?)parent?.Kind}{Sep}{parent?.Name.Database}{Sep}{parent?.Name.Schema}{Sep}{parent?.Name.Name}{Sep}{(int)kinds}",
            async token =>
            {
                var objects = await session.UseMetadataAsync(
                    other,
                    async (metadata, inner) =>
                    {
                        var list = new List<DatabaseObject>();
                        await foreach (var item in metadata.GetObjectsAsync(parent, kinds, inner).ConfigureAwait(false))
                        {
                            list.Add(item);
                        }

                        return list;
                    },
                    token).ConfigureAwait(false);
                await cache.SetObjectsAsync(connectionId, parent, kinds, objects, token).ConfigureAwait(false);
                return objects;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(
        ConnectionSession session, DatabaseObjectName tableOrView, bool refresh, CancellationToken cancellationToken, string? database = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var other = await session.OtherMetadataDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        var connectionId = session.MetadataCacheId(other);
        if (!refresh)
        {
            var cached = await cache.GetColumnsAsync(connectionId, tableOrView, cancellationToken).ConfigureAwait(false);
            if (cached is not null && timeProvider.GetUtcNow() - cached.LoadedAt < MaxAge)
            {
                return cached.Value;
            }
        }

        return await ReadOnceAsync(
            $"c{Sep}{connectionId:N}{Sep}{tableOrView.Database}{Sep}{tableOrView.Schema}{Sep}{tableOrView.Name}",
            async token =>
            {
                // A schema read ahead may be under way: wait for it rather than read this table on its own.
                if (Preloading(connectionId) is { } preload)
                {
                    try
                    {
                        await preload.WaitAsync(token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        // The read ahead failed or was stopped: this table is read on its own below.
                    }

                    if (await cache.GetColumnsAsync(connectionId, tableOrView, token).ConfigureAwait(false) is { } loaded &&
                        timeProvider.GetUtcNow() - loaded.LoadedAt < MaxAge)
                    {
                        return loaded.Value;
                    }
                }

                var columns = await session.UseMetadataAsync(other, (metadata, inner) => metadata.GetColumnsAsync(tableOrView, inner), token).ConfigureAwait(false);
                await cache.SetColumnsAsync(connectionId, tableOrView, columns, token).ConfigureAwait(false);
                return columns;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> PreloadColumnsAsync(
        ConnectionSession session, DatabaseObject? schema, Func<DatabaseObjectName, DatabaseObjectName> lookupName, CancellationToken cancellationToken, string? database = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(lookupName);
        var other = await session.OtherMetadataDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        var connectionId = session.MetadataCacheId(other);
        var mark = $"columns{Sep}{schema?.Name.Database}{Sep}{schema?.Name.Name}";
        if (await cache.GetMarkAsync(connectionId, mark, cancellationToken).ConfigureAwait(false) is { } done && timeProvider.GetUtcNow() - done < MaxAge)
        {
            return 0;
        }

        var read = ReadOnceAsync(
            $"p{Sep}{connectionId:N}{Sep}{mark}",
            async token =>
            {
                var tables = await session.UseMetadataAsync(
                    other,
                    (metadata, inner) => metadata is IBulkColumnMetadataProvider bulk
                        ? bulk.GetAllColumnsAsync(schema, inner)
                        : Task.FromResult<IReadOnlyList<TableColumns>>([]),
                    token).ConfigureAwait(false);
                var entries = new List<(DatabaseObjectName, IReadOnlyList<ColumnInfo>)>(tables.Count * 2);
                foreach (var table in tables)
                {
                    entries.Add((table.Table, table.Columns));
                    var asked = lookupName(table.Table);
                    if (asked != table.Table)
                    {
                        entries.Add((asked, table.Columns));
                    }
                }

                if (entries.Count > 0)
                {
                    await cache.SetManyColumnsAsync(connectionId, entries, token).ConfigureAwait(false);
                }

                await cache.SetMarkAsync(connectionId, mark, token).ConfigureAwait(false);
                return tables.Count;
            },
            cancellationToken);
        lock (_preloads)
        {
            _preloads[connectionId] = read;
        }

        try
        {
            return await read.ConfigureAwait(false);
        }
        finally
        {
            lock (_preloads)
            {
                if (_preloads.TryGetValue(connectionId, out var current) && ReferenceEquals(current, read))
                {
                    _preloads.Remove(connectionId);
                }
            }
        }
    }

    private Task? Preloading(Guid connectionId)
    {
        lock (_preloads)
        {
            return _preloads.TryGetValue(connectionId, out var task) ? task : null;
        }
    }

    /// <summary>
    /// Runs <paramref name="read"/> once for everyone asking for <paramref name="key"/> at the same time. A caller
    /// that gives up does not cancel the others: when the shared read was canceled by its starter, the next caller
    /// reads by itself.
    /// </summary>
    private async Task<T> ReadOnceAsync<T>(string key, Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        while (true)
        {
            TaskCompletionSource<T>? owner = null;
            Task<T> task;
            lock (_reading)
            {
                if (_reading.TryGetValue(key, out var running))
                {
                    task = (Task<T>)running;
                }
                else
                {
                    owner = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                    task = owner.Task;
                    _reading[key] = task;
                }
            }

            if (owner is not null)
            {
                try
                {
                    var value = await read(cancellationToken).ConfigureAwait(false);
                    owner.SetResult(value);
                    return value;
                }
                catch (OperationCanceledException)
                {
                    owner.SetCanceled(CancellationToken.None);
                    throw;
                }
                catch (Exception ex)
                {
                    owner.SetException(ex);
                    _ = owner.Task.Exception;
                    throw;
                }
                finally
                {
                    lock (_reading)
                    {
                        _reading.Remove(key);
                    }
                }
            }

            try
            {
                return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The one who started the read gave up; this caller still wants the answer, so it reads itself.
            }
        }
    }
}
