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

/// <summary>
/// Optional for a <see cref="IGridDataSource"/>: a short second line under a column's name in the header
/// (for example the column's data type). When any column has one, the header is taller.
/// </summary>
public interface IGridColumnCaptions
{
    /// <summary>The caption, or null/empty for none. Called on the UI thread; must be fast.</summary>
    string? GetColumnCaption(int column);
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

/// <summary>Whether a row of an editable source is as read, added or about to be deleted.</summary>
public enum GridRowState
{
    Normal,
    Inserted,
    Deleted,
}

/// <summary>
/// Optional for a <see cref="IGridDataSource"/>: cells the user may edit in the grid (F2, a double click, or typing),
/// and the marks of what changed. The grid only edits text; the host turns it into values and keeps the changes.
/// Members are called on the UI thread and must be fast.
/// </summary>
public interface IEditableGridDataSource
{
    /// <summary>Whether the cell can be edited.</summary>
    bool CanEdit(int row, int column);

    /// <summary>The text the editor starts with (the value as it would be typed); null or empty for NULL.</summary>
    string? GetEditText(int row, int column);

    /// <summary>Whether the cell holds a change not saved yet.</summary>
    bool IsModified(int row, int column);

    /// <summary>Whether the row was added or is to be deleted.</summary>
    GridRowState GetRowState(int row);
}
