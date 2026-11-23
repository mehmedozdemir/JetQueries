namespace JetQueries.Sdk;

/// <summary>
/// Optional feature of an <see cref="IDatabaseProvider"/>: a SELECT of the first rows of a table or view in the
/// dialect's own words (<c>TOP</c>, <c>FETCH FIRST</c>, <c>LIMIT</c>), for the explorer's "Select first rows".
/// </summary>
/// <remarks>Preview: part of SDK contract version 0.</remarks>
public interface IRowLimitedSelect
{
    /// <summary>The statement.</summary>
    /// <param name="qualifiedName">The table or view, already quoted by <see cref="IDatabaseProvider.QuoteIdentifier"/>.</param>
    /// <param name="quotedColumns">The columns, each already quoted.</param>
    /// <param name="rows">How many rows at most.</param>
    string SelectRows(string qualifiedName, IReadOnlyList<string> quotedColumns, int rows);
}
