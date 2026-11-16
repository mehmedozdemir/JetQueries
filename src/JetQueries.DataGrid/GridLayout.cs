namespace JetQueries.DataGrid;

/// <summary>Column geometry: prefix sums of the column widths, so every lookup is O(log n) or O(1).</summary>
internal sealed class ColumnLayout
{
    public const double MinColumnWidth = 24;

    // _edges[i] is the left edge of column i; _edges[Count] is the total width.
    private double[] _edges = [0];

    public int Count => _edges.Length - 1;

    public double TotalWidth => _edges[^1];

    public void Reset(IReadOnlyList<double> widths)
    {
        _edges = new double[widths.Count + 1];
        for (var i = 0; i < widths.Count; i++)
        {
            _edges[i + 1] = _edges[i] + Math.Max(MinColumnWidth, widths[i]);
        }
    }

    public double Left(int column) => _edges[column];

    public double Right(int column) => _edges[column + 1];

    public double Width(int column) => _edges[column + 1] - _edges[column];

    public void SetWidth(int column, double width)
    {
        var delta = Math.Max(MinColumnWidth, width) - Width(column);
        for (var i = column + 1; i < _edges.Length; i++)
        {
            _edges[i] += delta;
        }
    }

    /// <summary>The column containing content coordinate <paramref name="x"/>, or -1 if outside all columns.</summary>
    public int ColumnAt(double x)
    {
        if (x < 0 || x >= TotalWidth)
        {
            return -1;
        }

        var index = Array.BinarySearch(_edges, x);
        return index >= 0 ? index : ~index - 1;
    }

    /// <summary>First and last (inclusive) column intersecting the viewport; Last &lt; First when none does.</summary>
    public (int First, int Last) VisibleRange(double offsetX, double viewportWidth)
    {
        if (Count == 0 || viewportWidth <= 0 || offsetX >= TotalWidth)
        {
            return (0, -1);
        }

        var first = ColumnAt(Math.Max(0, offsetX));
        var last = ColumnAt(Math.Min(TotalWidth - 0.001, offsetX + viewportWidth - 0.001));
        return (first, last);
    }

    /// <summary>The column whose right edge is within <paramref name="tolerance"/> of <paramref name="x"/>, or -1.</summary>
    public int ResizeHandleAt(double x, double tolerance)
    {
        // The nearest edge is either the left or the right edge of the column under x (or the last edge).
        var column = x >= TotalWidth ? Count - 1 : ColumnAt(x);
        if (column < 0)
        {
            return -1;
        }

        if (Math.Abs(Right(column) - x) <= tolerance)
        {
            return column;
        }

        return column > 0 && Math.Abs(Left(column) - x) <= tolerance ? column - 1 : -1;
    }
}

internal static class RowLayout
{
    /// <summary>First and last (inclusive) row intersecting the body viewport; Last &lt; First when none does.</summary>
    public static (int First, int Last) VisibleRange(double offsetY, double bodyHeight, double rowHeight, int rowCount)
    {
        if (rowCount == 0 || bodyHeight <= 0)
        {
            return (0, -1);
        }

        var first = Math.Clamp((int)(offsetY / rowHeight), 0, rowCount - 1);
        var last = Math.Min(rowCount - 1, (int)Math.Ceiling((offsetY + bodyHeight) / rowHeight) - 1);
        return (first, last);
    }
}
