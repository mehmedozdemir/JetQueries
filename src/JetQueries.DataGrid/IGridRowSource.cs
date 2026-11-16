namespace JetQueries.DataGrid;

/// <summary>Horizontal alignment of a column's cell text.</summary>
public enum GridTextAlignment
{
    /// <summary>Left-aligned (text, dates).</summary>
    Left,

    /// <summary>Right-aligned (numbers).</summary>
    Right,
}

/// <summary>A column shown by <see cref="VirtualGrid"/>.</summary>
/// <param name="Header">Header text.</param>
/// <param name="Width">Initial width in device-independent pixels.</param>
/// <param name="Alignment">Alignment of the cell text.</param>
public sealed record GridColumn(string Header, double Width = 120, GridTextAlignment Alignment = GridTextAlignment.Left);

/// <summary>
/// The data behind a <see cref="VirtualGrid"/>. The grid asks only for the cells it is about to draw, so the
/// source decides how rows are stored (memory, disk, computed).
/// </summary>
/// <remarks>All members are called on the UI thread and must return immediately (no I/O).</remarks>
public interface IGridRowSource
{
    /// <summary>The columns. Must not change after the source is attached to a grid.</summary>
    IReadOnlyList<GridColumn> Columns { get; }

    /// <summary>Number of rows currently available. May grow while a query is streaming.</summary>
    int RowCount { get; }

    /// <summary>Raised on the UI thread after <see cref="RowCount"/> changed.</summary>
    event EventHandler? RowCountChanged;

    /// <summary>Gets the display text of a cell.</summary>
    /// <param name="row">Zero-based row index, below <see cref="RowCount"/>.</param>
    /// <param name="column">Zero-based column index.</param>
    /// <returns>The text to draw, or <see langword="null"/> for SQL <c>NULL</c>.</returns>
    string? GetText(int row, int column);
}

/// <summary>A cell position.</summary>
/// <param name="Row">Zero-based row index.</param>
/// <param name="Column">Zero-based column index.</param>
public readonly record struct GridCell(int Row, int Column);

/// <summary>An inclusive rectangular range of cells.</summary>
/// <param name="FirstRow">Top row.</param>
/// <param name="FirstColumn">Left column.</param>
/// <param name="LastRow">Bottom row (inclusive).</param>
/// <param name="LastColumn">Right column (inclusive).</param>
public readonly record struct GridCellRange(int FirstRow, int FirstColumn, int LastRow, int LastColumn)
{
    /// <summary>Creates the range spanned by two corner cells, in any order.</summary>
    /// <param name="a">One corner.</param>
    /// <param name="b">The opposite corner.</param>
    /// <returns>The normalized range.</returns>
    public static GridCellRange FromCorners(GridCell a, GridCell b) => new(
        Math.Min(a.Row, b.Row),
        Math.Min(a.Column, b.Column),
        Math.Max(a.Row, b.Row),
        Math.Max(a.Column, b.Column));
}
