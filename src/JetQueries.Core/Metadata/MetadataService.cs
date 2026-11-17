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
    Task<IReadOnlyList<DatabaseObject>> GetObjectsAsync(
        ConnectionSession session, DatabaseObject? parent, DatabaseObjectKinds kinds, bool refresh, CancellationToken cancellationToken);

    /// <summary>Gets the columns of a table or view.</summary>
    /// <param name="session">The open session.</param>
    /// <param name="tableOrView">The table or view.</param>
    /// <param name="refresh"><see langword="true"/> to ask the database even if the cache has an answer.</param>
    /// <param name="cancellationToken">Cancels the lookup and the database call.</param>
    Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(
        ConnectionSession session, DatabaseObjectName tableOrView, bool refresh, CancellationToken cancellationToken);
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<DatabaseObject>> GetObjectsAsync(
        ConnectionSession session, DatabaseObject? parent, DatabaseObjectKinds kinds, bool refresh, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var connectionId = session.Definition.Id;
        if (!refresh)
        {
            // A cache hit needs neither the metadata connection nor its turn in the queue.
            var cached = await cache.GetObjectsAsync(connectionId, parent, kinds, cancellationToken).ConfigureAwait(false);
            if (cached is not null && timeProvider.GetUtcNow() - cached.LoadedAt < MaxAge)
            {
                return cached.Value;
            }
        }

        var objects = await session.UseMetadataAsync(
            async (metadata, token) =>
            {
                var list = new List<DatabaseObject>();
                await foreach (var item in metadata.GetObjectsAsync(parent, kinds, token).ConfigureAwait(false))
                {
                    list.Add(item);
                }

                return list;
            },
            cancellationToken).ConfigureAwait(false);
        await cache.SetObjectsAsync(connectionId, parent, kinds, objects, cancellationToken).ConfigureAwait(false);
        return objects;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(
        ConnectionSession session, DatabaseObjectName tableOrView, bool refresh, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var connectionId = session.Definition.Id;
        if (!refresh)
        {
            var cached = await cache.GetColumnsAsync(connectionId, tableOrView, cancellationToken).ConfigureAwait(false);
            if (cached is not null && timeProvider.GetUtcNow() - cached.LoadedAt < MaxAge)
            {
                return cached.Value;
            }
        }

        var columns = await session.UseMetadataAsync((metadata, token) => metadata.GetColumnsAsync(tableOrView, token), cancellationToken).ConfigureAwait(false);
        await cache.SetColumnsAsync(connectionId, tableOrView, columns, cancellationToken).ConfigureAwait(false);
        return columns;
    }
}
