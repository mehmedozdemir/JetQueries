using System.Globalization;
using JetQueries.Presentation.Resources;

namespace JetQueries.Presentation.ViewModels;

/// <summary>How a column filter of a DBA table tests a cell.</summary>
public enum GridTextFilterOperator
{
    /// <summary>The text contains the value (case-insensitive).</summary>
    Contains,

    /// <summary>The text does not contain the value.</summary>
    NotContains,

    /// <summary>The text is the value (case-insensitive).</summary>
    Equals,

    /// <summary>The text is not the value.</summary>
    NotEquals,

    /// <summary>The number is greater than the value (units such as ms, s, MB and % are understood).</summary>
    Greater,

    /// <summary>The number is less than the value.</summary>
    Less,

    /// <summary>The cell is empty.</summary>
    IsEmpty,

    /// <summary>The cell is not empty.</summary>
    IsNotEmpty,
}

/// <summary>A filter on one column of a DBA table.</summary>
public sealed record GridTextFilter(int Column, GridTextFilterOperator Operator, string Text = "")
{
    /// <summary>Whether the operator compares with a value.</summary>
    public static bool NeedsValue(GridTextFilterOperator op) => op is not (GridTextFilterOperator.IsEmpty or GridTextFilterOperator.IsNotEmpty);
}

/// <summary>
/// The rows of a DBA screen's table as shown: sorted by any columns and narrowed by column filters, in memory
/// (these tables hold hundreds or a few thousand rows). Cells are display texts; numeric columns sort and compare
/// by the number they show, with its unit ("12 ms" &lt; "1,2 s", "800 KB" &lt; "1,5 MB"). Sorts and filters stay
/// when the rows are refreshed.
/// </summary>
public sealed class GridRowsView
{
    private readonly List<ResultSort> _sorts = [];
    private readonly List<GridTextFilter> _filters = [];
    private Func<int, int, string?> _cell = static (_, _) => null;
    private int _count;
    private int[] _map = [];

    /// <param name="headers">The column names.</param>
    /// <param name="numeric">Which columns show numbers (sorted and compared as numbers).</param>
    public GridRowsView(IReadOnlyList<string> headers, IReadOnlyList<bool> numeric)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(numeric);
        Headers = headers;
        Numeric = numeric;
    }

    public IReadOnlyList<string> Headers { get; }

    public IReadOnlyList<bool> Numeric { get; }

    public IReadOnlyList<ResultSort> Sorts => _sorts;

    public IReadOnlyList<GridTextFilter> Filters => _filters;

    /// <summary>The columns with a filter.</summary>
    public IReadOnlyCollection<int> FilteredColumns => _filters.Select(static f => f.Column).ToHashSet();

    /// <summary>Rows shown (after the filters).</summary>
    public int RowCount => _map.Length;

    /// <summary>Rows in all.</summary>
    public int TotalCount => _count;

    /// <summary>The source row of a shown row.</summary>
    public int SourceRow(int shownRow) => _map[shownRow];

    /// <summary>The shown row of a source row; -1 when it is filtered out.</summary>
    public int ShownRow(int sourceRow) => Array.IndexOf(_map, sourceRow);

    /// <summary>The text of a shown cell.</summary>
    public string? Cell(int shownRow, int column) => _cell(_map[shownRow], column);

    /// <summary>The rows are new (a refresh); the sorts and filters apply to them.</summary>
    public void SetRows(int count, Func<int, int, string?> cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        _count = count;
        _cell = cell;
        Apply();
    }

    /// <summary>A header click: this column only (or added to the others), ascending, then descending, then off.</summary>
    public void ToggleSort(int column, bool additive)
    {
        var existing = _sorts.FindIndex(s => s.Column == column);
        var next = existing < 0 ? new ResultSort(column, false) : _sorts[existing].Descending ? null : _sorts[existing] with { Descending = true };
        if (!additive)
        {
            _sorts.Clear();
            if (next is not null)
            {
                _sorts.Add(next);
            }
        }
        else if (existing >= 0)
        {
            if (next is null)
            {
                _sorts.RemoveAt(existing);
            }
            else
            {
                _sorts[existing] = next;
            }
        }
        else if (next is not null)
        {
            _sorts.Add(next);
        }

        Apply();
    }

    /// <summary>Sorts by one column (null: no sorting).</summary>
    public void SetSort(int column, bool? descending)
    {
        _sorts.Clear();
        if (descending is { } down)
        {
            _sorts.Add(new ResultSort(column, down));
        }

        Apply();
    }

    /// <summary>Sets or removes (null) the filter of a column.</summary>
    public void SetFilter(int column, GridTextFilter? filter)
    {
        _filters.RemoveAll(f => f.Column == column);
        if (filter is not null)
        {
            _filters.Add(filter with { Column = column });
        }

        Apply();
    }

    /// <summary>Keeps only (or leaves out) the rows with this cell's value.</summary>
    public void FilterByValue(int shownRow, int column, bool exclude)
    {
        var text = Cell(shownRow, column);
        SetFilter(column, text is null
            ? new GridTextFilter(column, exclude ? GridTextFilterOperator.IsNotEmpty : GridTextFilterOperator.IsEmpty)
            : new GridTextFilter(column, exclude ? GridTextFilterOperator.NotEquals : GridTextFilterOperator.Equals, text));
    }

    /// <summary>Removes every filter.</summary>
    public void ClearFilters()
    {
        _filters.Clear();
        Apply();
    }

    /// <summary>
    /// The shown rows for an export: plain numbers as numbers (so that Excel can add them up), everything else
    /// (texts, numbers with a unit) as shown.
    /// </summary>
    public IEnumerable<object?[]> ExportRows()
    {
        for (var row = 0; row < _map.Length; row++)
        {
            var values = new object?[Headers.Count];
            for (var column = 0; column < values.Length; column++)
            {
                var text = Cell(row, column);
                values[column] = Numeric[column] && text is not null && decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var number) ? number : text;
            }

            yield return values;
        }
    }

    /// <summary>
    /// The number a display text shows, in a common unit: durations in seconds ("850 ms", "1,2 s", "04:10",
    /// "1:02:03"), sizes in bytes ("12 KB" … "1,5 TB"), percentages and plain numbers as they are; null when the
    /// text is not a number.
    /// </summary>
    public static double? NumberOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        if (value.Contains(':', StringComparison.Ordinal))
        {
            var parts = value.Split(':');
            double seconds = 0;
            foreach (var part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var piece))
                {
                    return null;
                }

                seconds = seconds * 60 + piece;
            }

            return seconds;
        }

        var space = value.LastIndexOf(' ');
        var unit = space < 0 ? string.Empty : value[(space + 1)..];
        var factor = unit.ToUpperInvariant() switch
        {
            "" => 1d,
            "%" => 1d,
            "MS" => 0.001,
            "S" or "SN" or "SEC" => 1d,
            "B" => 1d,
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            "TB" => 1024d * 1024 * 1024 * 1024,
            _ when string.Equals(unit, Strings.UnitSeconds, StringComparison.OrdinalIgnoreCase) => 1d,
            _ => double.NaN,
        };
        if (double.IsNaN(factor))
        {
            return null;
        }

        var number = space < 0 ? value : value[..space];
        return double.TryParse(number, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsed) ? parsed * factor : null;
    }

    private void Apply()
    {
        var rows = Enumerable.Range(0, _count);
        foreach (var filter in _filters)
        {
            rows = rows.Where(row => Passes(filter, _cell(row, filter.Column)));
        }

        var list = rows.ToList();
        if (_sorts.Count > 0)
        {
            var text = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);
            list.Sort((a, b) =>
            {
                foreach (var sort in _sorts)
                {
                    var compared = Compare(_cell(a, sort.Column), _cell(b, sort.Column), Numeric[sort.Column], text);
                    if (compared != 0)
                    {
                        return sort.Descending ? -compared : compared;
                    }
                }

                return a.CompareTo(b);
            });
        }

        _map = [.. list];
    }

    // Empty cells sort before any value (first ascending, last descending).
    private static int Compare(string? a, string? b, bool numeric, StringComparer text)
    {
        if (a is null || b is null)
        {
            return a is null ? (b is null ? 0 : -1) : 1;
        }

        if (numeric && NumberOf(a) is { } x && NumberOf(b) is { } y)
        {
            return x.CompareTo(y);
        }

        return text.Compare(a, b);
    }

    private static bool Passes(GridTextFilter filter, string? cell)
    {
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        var value = filter.Text.Trim();
        return filter.Operator switch
        {
            GridTextFilterOperator.IsEmpty => string.IsNullOrEmpty(cell),
            GridTextFilterOperator.IsNotEmpty => !string.IsNullOrEmpty(cell),
            GridTextFilterOperator.Contains => cell is not null && compare.IndexOf(cell, value, CompareOptions.IgnoreCase) >= 0,
            GridTextFilterOperator.NotContains => cell is null || compare.IndexOf(cell, value, CompareOptions.IgnoreCase) < 0,
            GridTextFilterOperator.Equals => cell is not null && compare.Compare(cell.Trim(), value, CompareOptions.IgnoreCase) == 0,
            GridTextFilterOperator.NotEquals => cell is null || compare.Compare(cell.Trim(), value, CompareOptions.IgnoreCase) != 0,
            GridTextFilterOperator.Greater => NumberOf(cell) is { } a && NumberOf(value) is { } b && a > b,
            GridTextFilterOperator.Less => NumberOf(cell) is { } c && NumberOf(value) is { } d && c < d,
            _ => true,
        };
    }
}
