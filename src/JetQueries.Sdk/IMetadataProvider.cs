namespace JetQueries.Sdk;

/// <summary>
/// Reads schema metadata lazily, one level at a time, so that large schemas (10,000+ tables) never have to be
/// loaded up front. Implementations use parameterized catalog queries and stream their results.
/// </summary>
/// <remarks>
/// Preview contract: may change until SDK 1.0. The host's metadata cache implements this interface too, so
/// language services can consume cached and live metadata the same way.
/// </remarks>
public interface IMetadataProvider
{
    /// <summary>
    /// Lists the objects directly under <paramref name="parent"/>: databases or schemas at the root, then
    /// tables, views, routines and so on.
    /// </summary>
    /// <param name="parent">The container to list, or <see langword="null"/> for the root of the connection.</param>
    /// <param name="kinds">Only objects of these kinds are returned.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The matching objects, streamed as they are read.</returns>
    IAsyncEnumerable<DatabaseObject> GetObjectsAsync(
        DatabaseObject? parent,
        DatabaseObjectKinds kinds,
        CancellationToken cancellationToken);

    /// <summary>Gets the columns of a table or view, in ordinal order.</summary>
    /// <param name="tableOrView">The table or view.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The columns; empty if the object does not exist or has none.</returns>
    Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DatabaseObjectName tableOrView, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the source text of a programmable object (view, procedure, function, trigger, package spec or body),
    /// used by Ctrl+Click navigation.
    /// </summary>
    /// <param name="target">The object whose definition is requested.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The source text, or <see langword="null"/> if it is unavailable (missing, encrypted, no permission).</returns>
    Task<string?> GetDefinitionAsync(DatabaseObject target, CancellationToken cancellationToken);
}

/// <summary>Kinds of database objects. Combine values to filter <see cref="IMetadataProvider.GetObjectsAsync"/>.</summary>
[Flags]
public enum DatabaseObjectKinds
{
    /// <summary>No kind.</summary>
    None = 0,

    /// <summary>A database (catalog).</summary>
    Database = 1 << 0,

    /// <summary>A schema or owner.</summary>
    Schema = 1 << 1,

    /// <summary>A table.</summary>
    Table = 1 << 2,

    /// <summary>A view.</summary>
    View = 1 << 3,

    /// <summary>A stored procedure.</summary>
    Procedure = 1 << 4,

    /// <summary>A function.</summary>
    Function = 1 << 5,

    /// <summary>A package specification.</summary>
    Package = 1 << 6,

    /// <summary>A package body.</summary>
    PackageBody = 1 << 7,

    /// <summary>A sequence.</summary>
    Sequence = 1 << 8,

    /// <summary>A synonym.</summary>
    Synonym = 1 << 9,

    /// <summary>A trigger.</summary>
    Trigger = 1 << 10,

    /// <summary>A user-defined type.</summary>
    Type = 1 << 11,

    /// <summary>Every kind.</summary>
    All = (1 << 12) - 1,
}

/// <summary>
/// A multi-part object name with unquoted parts, exactly as stored in the catalog. Equality is ordinal and
/// case-sensitive; providers apply their own case-folding rules before constructing names.
/// </summary>
/// <param name="Database">Database (catalog) part, or <see langword="null"/> if not applicable.</param>
/// <param name="Schema">Schema or owner part, or <see langword="null"/> if not applicable.</param>
/// <param name="Name">The object's own name.</param>
public sealed record DatabaseObjectName(string? Database, string? Schema, string Name)
{
    /// <summary>Returns the unquoted parts joined with dots, for display only — never for building SQL.</summary>
    /// <returns>A display string such as <c>Sales.dbo.Orders</c>.</returns>
    public override string ToString() =>
        string.Join('.', new[] { Database, Schema, Name }.Where(static part => part is not null));
}

/// <summary>An object in the database catalog.</summary>
/// <param name="Name">The object's name.</param>
/// <param name="Kind">Exactly one <see cref="DatabaseObjectKinds"/> value.</param>
public sealed record DatabaseObject(DatabaseObjectName Name, DatabaseObjectKinds Kind);

/// <summary>A column of a table or view.</summary>
/// <param name="Name">Column name, unquoted.</param>
/// <param name="DataType">Data type as the database displays it, including length/precision (for example <c>nvarchar(50)</c>).</param>
/// <param name="IsNullable">Whether the column accepts <c>NULL</c>.</param>
/// <param name="Ordinal">One-based position within the table.</param>
/// <param name="IsPrimaryKey">Whether the column is part of the primary key.</param>
public sealed record ColumnInfo(string Name, string DataType, bool IsNullable, int Ordinal, bool IsPrimaryKey = false);
