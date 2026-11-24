namespace JetQueries.Sdk;

/// <summary>
/// Optional for an <see cref="IDatabaseProvider"/>: how a named parameter is written in its SQL, so that the host can
/// turn the user's <c>:name</c> placeholders into the driver's own (<c>@name</c> for SQL Server, PostgreSQL and
/// SQLite; <c>:name</c> for Oracle) and bind the values the user typed as <see cref="QueryParameter"/>s.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0. Pure: no I/O.</remarks>
public interface IQueryParameterSyntax
{
    /// <summary>The reference to the parameter <paramref name="name"/> in SQL text (<c>@name</c>, <c>:name</c>).</summary>
    /// <param name="name">The parameter's name, as bound in <see cref="QueryRequest.Parameters"/>.</param>
    string ParameterReference(string name);
}
