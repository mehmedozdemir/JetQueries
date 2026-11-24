namespace JetQueries.Sdk;

/// <summary>
/// Optional for an <see cref="IDatabaseProvider"/>: the statements that change or delete one row found by its key
/// (the data editor of a result grid). The host binds the values as parameters: <c>s{i}</c> for the new value of
/// the i-th column set and <c>k{i}</c> for the i-th key column's value as read (zero-based). Nothing the user types
/// is ever written into the SQL text. Rows are added with <see cref="IBulkInsertScripting"/>.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0. Pure: no I/O.</remarks>
public interface IRowEditScripting
{
    /// <summary><c>UPDATE table SET c0 = :s0, … WHERE k0 = :k0 AND …</c>.</summary>
    /// <param name="table">The table.</param>
    /// <param name="columns">The columns to set, unquoted, in the order of the <c>s{i}</c> parameters.</param>
    /// <param name="keyColumns">The key columns, unquoted, in the order of the <c>k{i}</c> parameters.</param>
    string ScriptUpdate(DatabaseObjectName table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns);

    /// <summary><c>DELETE FROM table WHERE k0 = :k0 AND …</c>.</summary>
    /// <param name="table">The table.</param>
    /// <param name="keyColumns">The key columns, unquoted, in the order of the <c>k{i}</c> parameters.</param>
    string ScriptDelete(DatabaseObjectName table, IReadOnlyList<string> keyColumns);
}
