using JetQueries.Sdk;

namespace JetQueries.Provider.Sqlite;

/// <summary>
/// SQLite has no stored functions or procedures: functions are registered by the application that opens the
/// database and are not in its schema. The host still gets an answer, always empty; the built-ins come from
/// the dialect's function catalog.
/// </summary>
internal sealed partial class SqliteMetadataProvider : IRoutineMetadataProvider
{
    /// <summary>Always empty: a SQLite database holds no routines.</summary>
    public Task<IReadOnlyList<SqlSignature>> GetSignaturesAsync(DatabaseObject routine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(routine);
        return Task.FromResult<IReadOnlyList<SqlSignature>>([]);
    }
}
