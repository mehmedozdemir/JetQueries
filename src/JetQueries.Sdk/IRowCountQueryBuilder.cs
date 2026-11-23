namespace JetQueries.Sdk;

/// <summary>
/// Optional feature of an <see cref="IDatabaseProvider"/>: a statement that counts the rows a query returns
/// without fetching them, as DBeaver's "calculate total row count" does.
/// </summary>
/// <remarks>Preview: part of SDK contract version 0.</remarks>
public interface IRowCountQueryBuilder
{
    /// <summary>
    /// Wraps <paramref name="sql"/> in a statement that returns one row with one number: how many rows the query
    /// returns. Null when the text is not a single query that can be counted that way (several statements,
    /// a procedure call, DML).
    /// </summary>
    /// <param name="sql">The query as the user ran it.</param>
    string? BuildCountQuery(string sql);
}
