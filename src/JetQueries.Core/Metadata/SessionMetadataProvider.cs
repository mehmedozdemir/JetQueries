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
public sealed class SessionMetadataProvider(ConnectionSession session, IMetadataService metadata) : IMetadataProvider
{
    /// <inheritdoc />
    public async IAsyncEnumerable<DatabaseObject> GetObjectsAsync(
        DatabaseObject? parent, DatabaseObjectKinds kinds, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var item in await metadata.GetObjectsAsync(session, parent, kinds, refresh: false, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DatabaseObjectName tableOrView, CancellationToken cancellationToken) =>
        metadata.GetColumnsAsync(session, tableOrView, refresh: false, cancellationToken);

    /// <inheritdoc />
    public Task<string?> GetDefinitionAsync(DatabaseObject target, CancellationToken cancellationToken) =>
        session.UseMetadataAsync((provider, token) => provider.GetDefinitionAsync(target, token), cancellationToken);
}
