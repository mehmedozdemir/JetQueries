namespace JetQueries.Sdk;

/// <summary>
/// Optional for an <see cref="IDatabaseProvider"/>: the statement that inserts several rows at once with
/// parameters (the data import). The host binds the values as parameters named <c>p{row}_{column}</c> (zero-based);
/// nothing the user imports is ever written into the SQL text.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0. Pure: no I/O.</remarks>
public interface IBulkInsertScripting
{
    /// <summary>How many rows one statement may carry for this many columns (the driver's parameter limits).</summary>
    /// <param name="columnCount">Columns per row.</param>
    int RowsPerStatement(int columnCount);

    /// <summary>An INSERT of <paramref name="rowCount"/> rows into the columns, with parameters <c>p{row}_{column}</c>.</summary>
    /// <param name="table">The table.</param>
    /// <param name="columns">The columns, unquoted, in the order of the values.</param>
    /// <param name="rowCount">Rows in the statement (at most <see cref="RowsPerStatement"/>).</param>
    string ScriptInsert(DatabaseObjectName table, IReadOnlyList<string> columns, int rowCount);
}
