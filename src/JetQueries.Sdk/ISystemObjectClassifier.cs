namespace JetQueries.Sdk;

/// <summary>
/// Optional for an <see cref="IDatabaseProvider"/>: tells which databases (or schemas) belong to the server itself
/// (SQL Server's <c>master</c>, <c>model</c>, <c>msdb</c>, <c>tempdb</c>), so that the explorer can keep them apart
/// from the user's own. Without it every database is shown alike.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0. Pure: no I/O.</remarks>
public interface ISystemObjectClassifier
{
    /// <summary>Whether <paramref name="databaseObject"/> (a database or schema at the top of the tree) is a system one.</summary>
    /// <param name="databaseObject">A database or schema as the metadata provider listed it.</param>
    bool IsSystemObject(DatabaseObject databaseObject);
}
