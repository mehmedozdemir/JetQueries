using JetQueries.Sdk;

namespace JetQueries.Core.Metadata;

/// <summary>
/// Remembers schema metadata per connection so that the object explorer and IntelliSense answer from memory
/// instead of asking the database again, and survive a restart. The cache holds metadata only, never secrets
/// or connection settings, and can be deleted at any time.
/// </summary>
/// <remarks>
/// All members are safe to call from any thread and never block it: disk access runs on the thread pool.
/// Names are matched exactly (ordinally); callers pass names in the form the provider reported them.
/// </remarks>
public interface IMetadataCache
{
    /// <summary>Returns the cached children of <paramref name="parent"/>, or <see langword="null"/> if there are none cached.</summary>
    /// <param name="connectionId">The <see cref="ConnectionProfile.Id"/> the metadata belongs to.</param>
    /// <param name="parent">The parent object, or <see langword="null"/> for the top level.</param>
    /// <param name="kinds">The exact set of kinds that was requested when the entry was stored.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<CachedMetadata<IReadOnlyList<DatabaseObject>>?> GetObjectsAsync(
        Guid connectionId, DatabaseObject? parent, DatabaseObjectKinds kinds, CancellationToken cancellationToken);

    /// <summary>Stores the children of <paramref name="parent"/>, replacing an earlier entry for the same request.</summary>
    /// <param name="connectionId">The <see cref="ConnectionProfile.Id"/> the metadata belongs to.</param>
    /// <param name="parent">The parent object, or <see langword="null"/> for the top level.</param>
    /// <param name="kinds">The set of kinds that was requested.</param>
    /// <param name="objects">The complete answer of the provider.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SetObjectsAsync(
        Guid connectionId, DatabaseObject? parent, DatabaseObjectKinds kinds, IReadOnlyList<DatabaseObject> objects, CancellationToken cancellationToken);

    /// <summary>Returns the cached columns of a table or view, or <see langword="null"/> if they are not cached.</summary>
    /// <param name="connectionId">The <see cref="ConnectionProfile.Id"/> the metadata belongs to.</param>
    /// <param name="tableOrView">The table or view.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<CachedMetadata<IReadOnlyList<ColumnInfo>>?> GetColumnsAsync(
        Guid connectionId, DatabaseObjectName tableOrView, CancellationToken cancellationToken);

    /// <summary>Stores the columns of a table or view.</summary>
    /// <param name="connectionId">The <see cref="ConnectionProfile.Id"/> the metadata belongs to.</param>
    /// <param name="tableOrView">The table or view.</param>
    /// <param name="columns">All of its columns.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SetColumnsAsync(
        Guid connectionId, DatabaseObjectName tableOrView, IReadOnlyList<ColumnInfo> columns, CancellationToken cancellationToken);

    /// <summary>Stores the columns of many tables and views at once (a schema read ahead), in one write.</summary>
    /// <param name="connectionId">The <see cref="ConnectionProfile.Id"/> the metadata belongs to.</param>
    /// <param name="tables">Each table or view, under every name it is looked up by, with its columns.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SetManyColumnsAsync(
        Guid connectionId, IReadOnlyList<(DatabaseObjectName Name, IReadOnlyList<ColumnInfo> Columns)> tables, CancellationToken cancellationToken);

    /// <summary>When something named <paramref name="key"/> was last done for a connection (a schema read ahead), if it was.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="key">What was done.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<DateTimeOffset?> GetMarkAsync(Guid connectionId, string key, CancellationToken cancellationToken);

    /// <summary>Notes that something named <paramref name="key"/> was done now for a connection.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="key">What was done.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SetMarkAsync(Guid connectionId, string key, CancellationToken cancellationToken);

    /// <summary>Removes everything cached for one connection, for example after "Refresh" or when it is deleted.</summary>
    /// <param name="connectionId">The <see cref="ConnectionProfile.Id"/> to forget.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task InvalidateAsync(Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>A cached value and when it was read from the database.</summary>
/// <typeparam name="T">The kind of metadata.</typeparam>
/// <param name="Value">The metadata as the provider returned it.</param>
/// <param name="LoadedAt">When it was stored; callers decide how old is too old.</param>
public sealed record CachedMetadata<T>(T Value, DateTimeOffset LoadedAt);

/// <summary>Where the metadata cache is kept.</summary>
public sealed class MetadataCacheOptions
{
    /// <summary>
    /// Path of the SQLite file. <see langword="null"/> keeps the cache in memory only, so it is lost on exit.
    /// </summary>
    public string? DatabasePath { get; set; }
}

/// <summary>Read-through helpers over <see cref="IMetadataCache"/>.</summary>
public static class MetadataCacheExtensions
{
    /// <summary>
    /// Returns the children of <paramref name="parent"/> from the cache if they are younger than
    /// <paramref name="maxAge"/>; otherwise reads them from <paramref name="source"/> and caches the answer.
    /// </summary>
    /// <param name="cache">The cache.</param>
    /// <param name="connectionId">The <see cref="ConnectionProfile.Id"/> of the connection behind <paramref name="source"/>.</param>
    /// <param name="source">The provider's metadata reader, used on a miss.</param>
    /// <param name="parent">The parent object, or <see langword="null"/> for the top level.</param>
    /// <param name="kinds">The kinds of children wanted.</param>
    /// <param name="maxAge">Oldest acceptable cache entry; <see cref="TimeSpan.Zero"/> forces a reload.</param>
    /// <param name="timeProvider">Clock used to judge the age.</param>
    /// <param name="cancellationToken">Cancels the lookup and the database call.</param>
    /// <returns>The objects and when they were read from the database.</returns>
    public static async Task<CachedMetadata<IReadOnlyList<DatabaseObject>>> GetOrLoadObjectsAsync(
        this IMetadataCache cache,
        Guid connectionId,
        IMetadataProvider source,
        DatabaseObject? parent,
        DatabaseObjectKinds kinds,
        TimeSpan maxAge,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var cached = await cache.GetObjectsAsync(connectionId, parent, kinds, cancellationToken).ConfigureAwait(false);
        if (cached is not null && timeProvider.GetUtcNow() - cached.LoadedAt < maxAge)
        {
            return cached;
        }

        var objects = new List<DatabaseObject>();
        await foreach (var item in source.GetObjectsAsync(parent, kinds, cancellationToken).ConfigureAwait(false))
        {
            objects.Add(item);
        }

        await cache.SetObjectsAsync(connectionId, parent, kinds, objects, cancellationToken).ConfigureAwait(false);
        return new CachedMetadata<IReadOnlyList<DatabaseObject>>(objects, timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Returns the columns of a table or view from the cache if they are younger than <paramref name="maxAge"/>;
    /// otherwise reads them from <paramref name="source"/> and caches the answer.
    /// </summary>
    /// <param name="cache">The cache.</param>
    /// <param name="connectionId">The <see cref="ConnectionProfile.Id"/> of the connection behind <paramref name="source"/>.</param>
    /// <param name="source">The provider's metadata reader, used on a miss.</param>
    /// <param name="tableOrView">The table or view.</param>
    /// <param name="maxAge">Oldest acceptable cache entry; <see cref="TimeSpan.Zero"/> forces a reload.</param>
    /// <param name="timeProvider">Clock used to judge the age.</param>
    /// <param name="cancellationToken">Cancels the lookup and the database call.</param>
    /// <returns>The columns and when they were read from the database.</returns>
    public static async Task<CachedMetadata<IReadOnlyList<ColumnInfo>>> GetOrLoadColumnsAsync(
        this IMetadataCache cache,
        Guid connectionId,
        IMetadataProvider source,
        DatabaseObjectName tableOrView,
        TimeSpan maxAge,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var cached = await cache.GetColumnsAsync(connectionId, tableOrView, cancellationToken).ConfigureAwait(false);
        if (cached is not null && timeProvider.GetUtcNow() - cached.LoadedAt < maxAge)
        {
            return cached;
        }

        var columns = await source.GetColumnsAsync(tableOrView, cancellationToken).ConfigureAwait(false);
        await cache.SetColumnsAsync(connectionId, tableOrView, columns, cancellationToken).ConfigureAwait(false);
        return new CachedMetadata<IReadOnlyList<ColumnInfo>>(columns, timeProvider.GetUtcNow());
    }
}
