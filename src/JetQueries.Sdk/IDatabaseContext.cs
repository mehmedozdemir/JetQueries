namespace JetQueries.Sdk;

/// <summary>What an <see cref="IDatabaseContext"/> switches: a database, or a schema within one database.</summary>
public enum DatabaseContextKind
{
    /// <summary>A database of the server (SQL Server <c>USE</c>, PostgreSQL's connection database).</summary>
    Database,

    /// <summary>A schema of the database (Oracle's current schema).</summary>
    Schema,
}

/// <summary>
/// Optional feature of an <see cref="IDatabaseConnection"/>: the database (or schema) that unqualified names on
/// the connection refer to, and switching it, as the database selector of a query window does. The host
/// serializes calls with the connection's queries.
/// </summary>
/// <remarks>Preview: part of SDK contract version 0.</remarks>
public interface IDatabaseContext
{
    /// <summary>Whether a database or a schema is switched.</summary>
    DatabaseContextKind Kind { get; }

    /// <summary>The current database or schema, as the server reports it now (a script may have changed it).</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<string?> GetCurrentAsync(CancellationToken cancellationToken);

    /// <summary>The databases or schemas the user may switch to, sorted for display.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<IReadOnlyList<string>> GetAvailableAsync(CancellationToken cancellationToken);

    /// <summary>Makes <paramref name="name"/> the current database or schema of this connection.</summary>
    /// <param name="name">A name from <see cref="GetAvailableAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task ChangeAsync(string name, CancellationToken cancellationToken);
}
