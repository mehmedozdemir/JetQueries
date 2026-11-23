namespace JetQueries.Sdk;

/// <summary>
/// Optional for an <see cref="IDatabaseProvider"/>: writes the DDL statements the object explorer's "Script"
/// menu offers next to the definition (<see cref="IMetadataProvider.GetDefinitionAsync"/>). Names are quoted
/// by the provider; nothing is run.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0.</remarks>
public interface IObjectScripting
{
    /// <summary>A statement that drops the object, with the provider's statement terminator.</summary>
    /// <param name="target">The object.</param>
    /// <returns>The statement, or <see langword="null"/> when the kind cannot be dropped by a plain statement.</returns>
    string? ScriptDrop(DatabaseObject target);

    /// <summary>
    /// The definition rewritten to change the existing object (SQL Server's <c>ALTER</c>, Oracle's and
    /// PostgreSQL's <c>CREATE OR REPLACE</c>).
    /// </summary>
    /// <param name="target">The object.</param>
    /// <param name="definition">Its definition as <see cref="IMetadataProvider.GetDefinitionAsync"/> gave it.</param>
    /// <returns>The script, or <see langword="null"/> when the kind has no such form (tables, for example).</returns>
    string? ScriptAlter(DatabaseObject target, string definition);
}
