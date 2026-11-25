using System.Runtime.CompilerServices;
using JetQueries.Core.Connections;
using JetQueries.Sdk;

namespace JetQueries.Core.Metadata;

/// <summary>
/// The metadata of an open session in the shape language services expect. Answers come from the cache when it
/// has them, so completion normally costs no round trip; a miss is read on the session's metadata connection
/// and cached for next time.
/// </summary>
/// <param name="session">The open session.</param>
/// <param name="metadata">The cache-backed metadata service.</param>
/// <param name="database">
/// The database the query window has selected. For a provider whose metadata sees one database only
/// (PostgreSQL), another database's objects are read on a connection to it, and carry its name, so that
/// what is opened from them (details, definitions) goes to the same database.
/// </param>
public sealed class SessionMetadataProvider(ConnectionSession session, IMetadataService metadata, string? database = null) : IMetadataProvider, IRoutineMetadataProvider, IMetadataPreloader
{
    /// <inheritdoc />
    public async IAsyncEnumerable<DatabaseObject> GetObjectsAsync(
        DatabaseObject? parent, DatabaseObjectKinds kinds, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var other = await session.OtherMetadataDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        var asked = other is null || parent is null ? parent : Local(parent);
        foreach (var item in await metadata.GetObjectsAsync(session, asked, kinds, refresh: false, cancellationToken, database).ConfigureAwait(false))
        {
            yield return other is null ? item : item with { Name = item.Name with { Database = other } };
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DatabaseObjectName tableOrView, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tableOrView);
        var other = await session.OtherMetadataDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        return await metadata.GetColumnsAsync(session, other is null ? tableOrView : tableOrView with { Database = null }, refresh: false, cancellationToken, database)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> PreloadColumnsAsync(DatabaseObject? schema, Func<DatabaseObjectName, DatabaseObjectName> lookupName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookupName);
        var other = await session.OtherMetadataDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        return await metadata.PreloadColumnsAsync(session, other is null || schema is null ? schema : Local(schema), lookupName, cancellationToken, database)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<string?> GetDefinitionAsync(DatabaseObject target, CancellationToken cancellationToken) =>
        session.UseMetadataAsync(database, (provider, token) => provider.GetDefinitionAsync(ForProvider(target), token), cancellationToken);

    /// <inheritdoc />
    /// <remarks>Read on the session's metadata connection; a provider without routine metadata gives none.</remarks>
    public Task<IReadOnlyList<SqlSignature>> GetSignaturesAsync(DatabaseObject routine, CancellationToken cancellationToken) =>
        session.UseMetadataAsync(
            database,
            (provider, token) => provider is IRoutineMetadataProvider routines ? routines.GetSignaturesAsync(ForProvider(routine), token) : Task.FromResult<IReadOnlyList<SqlSignature>>([]),
            cancellationToken);

    // A provider whose metadata sees one database is asked without the database part this class adds.
    private DatabaseObject ForProvider(DatabaseObject target) => session.HasMetadataPerDatabase ? Local(target) : target;

    private static DatabaseObject Local(DatabaseObject target) => target with { Name = target.Name with { Database = null } };
}
