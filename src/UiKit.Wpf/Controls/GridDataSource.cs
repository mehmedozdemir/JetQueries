namespace UiKit.Controls;

/// <summary>How a column is aligned and decorated in <see cref="VirtualGrid"/>.</summary>
public enum GridColumnKind
{
    Text,
    Integer,
    Number,
    Boolean,
    DateTime,
}

public enum GridSortDirection
{
    Ascending,
    Descending,
}

public readonly record struct GridSort(int Column, GridSortDirection Direction);

/// <summary>
/// Read-only, index based data for <see cref="VirtualGrid"/>. Only visible cells are requested,
/// so implementations can wrap columnar arrays, a paged result set, or compute values on demand.
/// Members are called on the UI thread and must be fast (no I/O).
/// </summary>
public interface IGridDataSource
{
    /// <summary>
    /// Identity of the column layout. While it stays equal between two sources (e.g. the same result
    /// set filtered or sorted), the grid keeps column widths and horizontal position.
    /// </summary>
    object SchemaKey { get; }

    int RowCount { get; }

    int ColumnCount { get; }

    string GetColumnName(int column);

    GridColumnKind GetColumnKind(int column);

    /// <summary>Display text of a cell (culture formatted). Not called for null cells.</summary>
    string GetText(int row, int column);

    bool IsNull(int row, int column);
}

/// <summary>Simple delegate based source; also handy for samples and tests.</summary>
public sealed class ArrayGridDataSource : IGridDataSource
{
    private readonly string[] _names;
    private readonly GridColumnKind[] _kinds;
    private readonly Func<int, int, string?> _cell;

    public static readonly ArrayGridDataSource Empty = new([], [], 0, (_, _) => null);

    /// <param name="cell">Returns the cell text, or <c>null</c> for a NULL cell.</param>
    public ArrayGridDataSource(string[] names, GridColumnKind[] kinds, int rowCount, Func<int, int, string?> cell, object? schemaKey = null)
    {
        _names = names;
        _kinds = kinds;
        _cell = cell;
        RowCount = rowCount;
        SchemaKey = schemaKey ?? this;
    }

    public object SchemaKey { get; }

    public int RowCount { get; }

    public int ColumnCount => _names.Length;

    public string GetColumnName(int column) => _names[column];

    public GridColumnKind GetColumnKind(int column) => _kinds[column];

    public string GetText(int row, int column) => _cell(row, column) ?? string.Empty;

    public bool IsNull(int row, int column) => _cell(row, column) is null;
}
