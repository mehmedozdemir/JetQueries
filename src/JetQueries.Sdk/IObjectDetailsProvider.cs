namespace JetQueries.Sdk;

/// <summary>
/// Optional for an <see cref="IMetadataProvider"/>: everything about one object for the object details view
/// (opened by Ctrl+Click in the editor and from the explorer): its properties and tables of columns, keys,
/// foreign keys, references, indexes, triggers and so on. Catalog queries only, parameterized; user tables are
/// never scanned.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0.</remarks>
public interface IObjectDetailsProvider
{
    /// <summary>Reads the details of <paramref name="target"/>.</summary>
    /// <param name="target">A table, view, routine, package, sequence or other object.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The details, or <see langword="null"/> when the object does not exist or cannot be seen.</returns>
    Task<ObjectDetails?> GetDetailsAsync(DatabaseObject target, CancellationToken cancellationToken);
}

/// <summary>The details of one object.</summary>
/// <param name="Target">The object, with its name as the catalog has it.</param>
/// <param name="Properties">Name/value pairs (type, owner, created, rows, size, comment…), in display order.</param>
/// <param name="Sections">Tables shown as tabs, in display order; empty sections may be left out.</param>
public sealed record ObjectDetails(DatabaseObject Target, IReadOnlyList<ObjectProperty> Properties, IReadOnlyList<ObjectDetailSection> Sections);

/// <summary>A property of an object.</summary>
/// <param name="Key">A stable key; the host shows a translated label for the keys of <see cref="ObjectDetailKeys"/>.</param>
/// <param name="Label">The label used when the host has no translation for <paramref name="Key"/>.</param>
/// <param name="Value">The value as text; null or empty is shown as empty.</param>
public sealed record ObjectProperty(string Key, string Label, string? Value);

/// <summary>A table of details (one tab of the details view).</summary>
/// <param name="Key">A stable key, such as <see cref="ObjectDetailKeys.Columns"/>; the host translates known keys.</param>
/// <param name="Title">The title used when the host has no translation for <paramref name="Key"/>.</param>
/// <param name="Columns">The table's columns.</param>
/// <param name="Rows">The rows; each has one cell per column.</param>
public sealed record ObjectDetailSection(string Key, string Title, IReadOnlyList<ObjectDetailColumn> Columns, IReadOnlyList<ObjectDetailRow> Rows);

/// <summary>A column of a details table.</summary>
/// <param name="Key">A stable key, such as <see cref="ObjectDetailKeys.Name"/>; the host translates known keys.</param>
/// <param name="Title">The header used when the host has no translation for <paramref name="Key"/>.</param>
public sealed record ObjectDetailColumn(string Key, string Title);

/// <summary>A row of a details table.</summary>
/// <param name="Cells">The cell texts; null for an empty cell.</param>
public sealed record ObjectDetailRow(IReadOnlyList<string?> Cells)
{
    /// <summary>An object the row is about (the table a foreign key refers to, a dependent view…), opened on double-click.</summary>
    public DatabaseObject? Target { get; init; }

    /// <summary>Source text the row carries (a trigger's or a check constraint's definition), shown under the table.</summary>
    public string? Source { get; init; }
}

/// <summary>Well-known keys of <see cref="ObjectDetails"/>: sections, columns and properties the host translates.</summary>
public static class ObjectDetailKeys
{
    // Sections

    /// <summary>Section: the columns of a table or view.</summary>
    public const string Columns = "columns";

    /// <summary>Section: primary key, unique and check constraints.</summary>
    public const string Constraints = "constraints";

    /// <summary>Section: foreign keys of the object.</summary>
    public const string ForeignKeys = "foreignKeys";

    /// <summary>Section: foreign keys of other tables that refer to the object.</summary>
    public const string References = "references";

    /// <summary>Section: indexes.</summary>
    public const string Indexes = "indexes";

    /// <summary>Section: triggers.</summary>
    public const string Triggers = "triggers";

    /// <summary>Section: objects the object uses and objects that use it.</summary>
    public const string Dependencies = "dependencies";

    /// <summary>Section: parameters of a routine.</summary>
    public const string Parameters = "parameters";

    // Columns

    /// <summary>Column: a name.</summary>
    public const string Name = "name";

    /// <summary>Column: a position (1, 2, …).</summary>
    public const string Position = "position";

    /// <summary>Column: a data type.</summary>
    public const string DataType = "dataType";

    /// <summary>Column: whether NULL is allowed.</summary>
    public const string Nullable = "nullable";

    /// <summary>Column: a default value.</summary>
    public const string Default = "default";

    /// <summary>Column or property: identity / auto-increment / generated.</summary>
    public const string Identity = "identity";

    /// <summary>Column: key membership (PK, FK…).</summary>
    public const string Key = "key";

    /// <summary>Column: a collation.</summary>
    public const string Collation = "collation";

    /// <summary>Column or property: a comment / description.</summary>
    public const string Comment = "comment";

    /// <summary>Column or property: a kind or type (PRIMARY KEY, CLUSTERED, AFTER INSERT…).</summary>
    public const string Kind = "kind";

    /// <summary>Column: the columns a constraint or index covers.</summary>
    public const string ColumnList = "columnList";

    /// <summary>Column: a definition or condition.</summary>
    public const string Definition = "definition";

    /// <summary>Column: a referenced (or referencing) table.</summary>
    public const string Table = "table";

    /// <summary>Column: the referenced (or referencing) columns.</summary>
    public const string ReferencedColumns = "referencedColumns";

    /// <summary>Column: the delete rule of a foreign key.</summary>
    public const string OnDelete = "onDelete";

    /// <summary>Column: the update rule of a foreign key.</summary>
    public const string OnUpdate = "onUpdate";

    /// <summary>Column: whether an index is unique.</summary>
    public const string Unique = "unique";

    /// <summary>Column: included (non-key) columns of an index.</summary>
    public const string Included = "included";

    /// <summary>Column: an index filter / partial index condition.</summary>
    public const string Filter = "filter";

    /// <summary>Column or property: enabled/disabled state or status.</summary>
    public const string Status = "status";

    /// <summary>Column: the events of a trigger.</summary>
    public const string Events = "events";

    /// <summary>Column: a direction (IN/OUT, uses/used by).</summary>
    public const string Direction = "direction";

    // Properties

    /// <summary>Property: the object's type.</summary>
    public const string ObjectType = "objectType";

    /// <summary>Property: the schema or owner.</summary>
    public const string Schema = "schema";

    /// <summary>Property: the database.</summary>
    public const string Database = "database";

    /// <summary>Property: when the object was created.</summary>
    public const string Created = "created";

    /// <summary>Property: when the object was last changed.</summary>
    public const string Modified = "modified";

    /// <summary>Property: an estimated row count (from statistics, not counted).</summary>
    public const string Rows = "rows";

    /// <summary>Property: the space used.</summary>
    public const string Size = "size";

    /// <summary>Property: a tablespace or filegroup.</summary>
    public const string Tablespace = "tablespace";
}
