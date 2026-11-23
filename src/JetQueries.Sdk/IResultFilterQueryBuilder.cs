namespace JetQueries.Sdk;

/// <summary>
/// Optional feature of an <see cref="IDatabaseProvider"/>: a query narrowed to the rows matching a condition the
/// user typed over its result, as DBeaver's "Enter a SQL expression to filter results" does. The filter runs on
/// the server, so it covers every row of the query, not only those fetched.
/// </summary>
/// <remarks>Preview: part of SDK contract version 0.</remarks>
public interface IResultFilterQueryBuilder
{
    /// <summary>
    /// Wraps <paramref name="sql"/> so that only the rows for which <paramref name="condition"/> holds are returned,
    /// keeping the query's order. Null when the text is not a single query that can be wrapped (as for
    /// <see cref="IRowCountQueryBuilder.BuildCountQuery"/>), or when the condition is not one expression
    /// (a statement separator, unbalanced parentheses, a comment).
    /// </summary>
    /// <param name="sql">The query as the user ran it.</param>
    /// <param name="condition">A SQL condition over the query's columns, for example <c>balance &gt; 100 AND city LIKE 'İ%'</c>.</param>
    string? BuildFilterQuery(string sql, string condition);
}
