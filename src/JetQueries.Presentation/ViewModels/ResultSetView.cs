using System.Globalization;
using JetQueries.Core.Jobs;
using JetQueries.Presentation.Services;

namespace JetQueries.Presentation.ViewModels;

/// <summary>One sort key of a result view.</summary>
/// <param name="Column">The column index in the result set.</param>
/// <param name="Descending">Largest first.</param>
public sealed record ResultSort(int Column, bool Descending);

/// <summary>How a filter compares a cell with its value.</summary>
public enum ResultFilterOperator
{
    /// <summary>Equal to the value (text: ignoring case).</summary>
    Equals,

    /// <summary>Not equal to the value; NULL cells are kept out, as in SQL.</summary>
    NotEquals,

    /// <summary>Text contains the value, ignoring case.</summary>
    Contains,

    /// <summary>Text does not contain the value.</summary>
    NotContains,

    /// <summary>Text starts with the value.</summary>
    StartsWith,

    /// <summary>Text ends with the value.</summary>
    EndsWith,

    /// <summary>Greater than the value.</summary>
    GreaterThan,

    /// <summary>Greater than or equal to the value.</summary>
    GreaterOrEqual,

    /// <summary>Less than the value.</summary>
    LessThan,

    /// <summary>Less than or equal to the value.</summary>
    LessOrEqual,

    /// <summary>The cell is NULL.</summary>
    IsNull,

    /// <summary>The cell is not NULL.</summary>
    IsNotNull,
}

/// <summary>
/// A filter on one column. <paramref name="Text"/> is what the user typed (or the cell's text); when
/// <paramref name="Value"/> is set (filter by a cell's value) the cell's own value is compared instead.
/// </summary>
public sealed record ResultFilter(int Column, ResultFilterOperator Operator, string Text = "", object? Value = null)
{
    /// <summary>Whether the operator takes a value.</summary>
    public static bool NeedsValue(ResultFilterOperator op) => op is not (ResultFilterOperator.IsNull or ResultFilterOperator.IsNotNull);
}

/// <summary>
/// Sorting and filtering of a result set on the client, over the rows it holds. The grid shows the rows in
/// <see cref="RowMap"/> order. Work runs as a background job; while a newer request is pending an older one is
/// canceled, and only the newest answer is applied, on the UI thread.
/// </summary>
public sealed class ResultSetView
{
    private readonly QueryResultSet _set;
    private readonly IJobQueue? _jobs;
    private readonly IUiDispatcher? _ui;
    private List<ResultSort> _sorts = [];
    private List<ResultFilter> _filters = [];
    private bool _matchAny;
    private JobHandle<int[]?>? _job;
    private int _version;

    /// <param name="set">The rows.</param>
    /// <param name="jobs">Where the work runs; without one it runs at once on the calling thread (tests).</param>
    /// <param name="ui">Where answers are applied; without one, on the thread that computed them.</param>
    public ResultSetView(QueryResultSet set, IJobQueue? jobs = null, IUiDispatcher? ui = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        _set = set;
        _jobs = jobs;
        _ui = ui;
    }

    /// <summary>Raised after <see cref="RowMap"/>, <see cref="Sorts"/> or <see cref="Filters"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The sort keys, most significant first.</summary>
    public IReadOnlyList<ResultSort> Sorts => _sorts;

    /// <summary>The filters; a row must pass all of them, or any one with <see cref="MatchAny"/>.</summary>
    public IReadOnlyList<ResultFilter> Filters => _filters;

    /// <summary>Whether a row passing any one filter is shown (OR) instead of only rows passing all (AND).</summary>
    public bool MatchAny => _matchAny;

    /// <summary>Combines the filters with OR (<see langword="true"/>) or AND.</summary>
    public Task SetMatchAnyAsync(bool matchAny)
    {
        _matchAny = matchAny;
        return ApplyAsync(_sorts, _filters);
    }

    /// <summary>The result-set row shown at each position; null shows every row in arrival order.</summary>
    public int[]? RowMap { get; private set; }

    /// <summary>Whether anything is sorted or filtered.</summary>
    public bool IsActive => _sorts.Count > 0 || _filters.Count > 0;

    /// <summary>The number of rows shown.</summary>
    public int RowCount => RowMap?.Length ?? _set.RowCount;

    /// <summary>The result-set row behind a shown row.</summary>
    public int SourceRow(int shownRow) => RowMap is { } map ? map[shownRow] : shownRow;

    /// <summary>A header click: sorts by the column, or turns its direction; with <paramref name="additive"/> it is added to the keys.</summary>
    public Task ToggleSortAsync(int column, bool additive)
    {
        var current = _sorts.Find(s => s.Column == column);
        var next = new ResultSort(column, current is { Descending: false });
        var sorts = additive ? _sorts.Where(s => s.Column != column).ToList() : [];
        if (additive && current is not null)
        {
            sorts.Insert(_sorts.IndexOf(current), next);
        }
        else
        {
            sorts.Add(next);
        }

        return ApplyAsync(sorts, _filters);
    }

    /// <summary>Sorts by the column alone in the given direction, or removes it from the keys (null).</summary>
    public Task SetSortAsync(int column, bool? descending) =>
        ApplyAsync(descending is { } d ? [new ResultSort(column, d)] : _sorts.Where(s => s.Column != column).ToList(), _filters);

    /// <summary>Replaces the filters of a column; null removes them.</summary>
    public Task SetFilterAsync(int column, ResultFilter? filter)
    {
        var filters = _filters.Where(f => f.Column != column).ToList();
        if (filter is not null)
        {
            filters.Add(filter with { Column = column });
        }

        return ApplyAsync(_sorts, filters);
    }

    /// <summary>Keeps only the rows whose cell equals (or, with <paramref name="exclude"/>, differs from) this shown cell.</summary>
    public Task FilterByValueAsync(int shownRow, int column, bool exclude)
    {
        var value = _set.GetValue(SourceRow(shownRow), column);
        var text = _set.GetText(SourceRow(shownRow), column) ?? "NULL";
        var filter = value is null
            ? new ResultFilter(column, exclude ? ResultFilterOperator.IsNotNull : ResultFilterOperator.IsNull)
            : new ResultFilter(column, exclude ? ResultFilterOperator.NotEquals : ResultFilterOperator.Equals, text, value);
        return ApplyAsync(_sorts, [.. _filters, filter]);
    }

    /// <summary>Removes one filter.</summary>
    public Task RemoveFilterAsync(ResultFilter filter) => ApplyAsync(_sorts, _filters.Where(f => !ReferenceEquals(f, filter)).ToList());

    /// <summary>Removes every filter; the sort stays.</summary>
    public Task ClearFiltersAsync() => ApplyAsync(_sorts, []);

    /// <summary>More rows arrived: the order and the filters are applied to them too.</summary>
    public Task RefreshAsync() => IsActive ? ApplyAsync(_sorts, _filters) : Task.CompletedTask;

    private async Task ApplyAsync(List<ResultSort> sorts, List<ResultFilter> filters)
    {
        // The view's state changes at once (the header shows it); the rows follow when computed.
        _sorts = sorts;
        _filters = filters;
        var version = Interlocked.Increment(ref _version);
        if (sorts.Count == 0 && filters.Count == 0)
        {
            _job?.Cancel();
            Apply(version, null);
            return;
        }

        // Rows are appended on the UI thread while a query streams: the job works on a copy of the list.
        var rows = _set.SnapshotRows();
        var columns = _set.Columns;
        var matchAny = _matchAny;
        if (_jobs is null)
        {
            Apply(version, Compute(rows, columns, sorts, filters, CancellationToken.None, matchAny));
            return;
        }

        _job?.Cancel();
        var job = _jobs.Enqueue<int[]?>(
            "Sort and filter results",
            context => Task.FromResult<int[]?>(Compute(rows, columns, sorts, filters, context.CancellationToken, matchAny)));
        _job = job;
        int[]? map;
        try
        {
            map = await job.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_ui is null)
        {
            Apply(version, map);
        }
        else
        {
            _ui.Post(() => Apply(version, map));
        }
    }

    private void Apply(int version, int[]? map)
    {
        if (version != Volatile.Read(ref _version))
        {
            return;
        }

        RowMap = map;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The shown rows: those passing every filter (or, with <paramref name="matchAny"/>, at least one), in sort
    /// order (ties keep arrival order).
    /// </summary>
    internal static int[] Compute(
        IReadOnlyList<object?[]> rows, IReadOnlyList<ResultGridColumn> columns, IReadOnlyList<ResultSort> sorts, IReadOnlyList<ResultFilter> filters,
        CancellationToken cancellationToken, bool matchAny = false)
    {
        var tests = filters.Select(f => CellTest.Create(f, columns[f.Column])).ToArray();
        var kept = new List<int>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            if ((i & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var row = rows[i];
            var pass = tests.Length == 0 || !matchAny;
            foreach (var test in tests)
            {
                if (test.Passes(row[test.Column]) == matchAny)
                {
                    pass = matchAny;
                    break;
                }
            }

            if (pass)
            {
                kept.Add(i);
            }
        }

        var map = kept.ToArray();
        if (sorts.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var culture = CultureInfo.CurrentCulture.CompareInfo;
            Array.Sort(map, (a, b) =>
            {
                foreach (var sort in sorts)
                {
                    var order = CompareValues(rows[a][sort.Column], rows[b][sort.Column], culture);
                    if (order != 0)
                    {
                        return sort.Descending ? -order : order;
                    }
                }

                return a.CompareTo(b);
            });
        }

        return map;
    }

    /// <summary>NULL first; numbers by value whatever their .NET type; text by the user's culture, ignoring case first.</summary>
    internal static int CompareValues(object? a, object? b, CompareInfo culture)
    {
        if (a is null || b is null)
        {
            return a is null ? (b is null ? 0 : -1) : 1;
        }

        if (IsNumber(a) && IsNumber(b))
        {
            return CompareNumbers(a, b);
        }

        if (a is string sa && b is string sb)
        {
            var order = culture.Compare(sa, sb, CompareOptions.IgnoreCase);
            return order != 0 ? order : culture.Compare(sa, sb, CompareOptions.None);
        }

        if (a.GetType() == b.GetType() && a is IComparable comparable)
        {
            return comparable.CompareTo(b);
        }

        // Mixed storage classes (an SQLite column holding numbers and text): numbers before text, as SQLite does.
        var rank = Rank(a).CompareTo(Rank(b));
        return rank != 0 ? rank : string.CompareOrdinal(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture));
    }

    internal static bool IsNumber(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    internal static int CompareNumbers(object a, object b)
    {
        if (a is not (float or double) && b is not (float or double))
        {
            try
            {
                return Convert.ToDecimal(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture));
            }
            catch (OverflowException)
            {
                // ulong beyond decimal cannot happen; kept for safety with exotic values.
            }
        }

        return Convert.ToDouble(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture));
    }

    private static int Rank(object value) => IsNumber(value) ? 0 : value is string ? 1 : value is byte[] ? 3 : 2;

    /// <summary>One filter, prepared: the typed value parsed once.</summary>
    private sealed class CellTest
    {
        private readonly ResultFilterOperator _op;
        private readonly object? _value;
        private readonly string _text;
        private readonly CompareInfo _culture = CultureInfo.CurrentCulture.CompareInfo;

        private CellTest(int column, ResultFilterOperator op, object? value, string text)
        {
            Column = column;
            _op = op;
            _value = value;
            _text = text;
        }

        public int Column { get; }

        public static CellTest Create(ResultFilter filter, ResultGridColumn column) =>
            new(filter.Column, filter.Operator, filter.Value ?? Parse(filter.Text, column.ClrType), filter.Text);

        public bool Passes(object? cell)
        {
            switch (_op)
            {
                case ResultFilterOperator.IsNull:
                    return cell is null;
                case ResultFilterOperator.IsNotNull:
                    return cell is not null;
            }

            // As in SQL, a NULL cell passes no comparison.
            if (cell is null)
            {
                return false;
            }

            var cellText = cell as string ?? Format(cell);
            switch (_op)
            {
                case ResultFilterOperator.Contains:
                    return _culture.IndexOf(cellText, _text, CompareOptions.IgnoreCase) >= 0;
                case ResultFilterOperator.NotContains:
                    return _culture.IndexOf(cellText, _text, CompareOptions.IgnoreCase) < 0;
                case ResultFilterOperator.StartsWith:
                    return _culture.IsPrefix(cellText, _text, CompareOptions.IgnoreCase);
                case ResultFilterOperator.EndsWith:
                    return _culture.IsSuffix(cellText, _text, CompareOptions.IgnoreCase);
            }

            var order = Compare(cell, cellText);
            return _op switch
            {
                ResultFilterOperator.Equals => order == 0,
                ResultFilterOperator.NotEquals => order != 0,
                ResultFilterOperator.GreaterThan => order > 0,
                ResultFilterOperator.GreaterOrEqual => order >= 0,
                ResultFilterOperator.LessThan => order < 0,
                ResultFilterOperator.LessOrEqual => order <= 0,
                _ => true,
            };
        }

        private int Compare(object cell, string cellText)
        {
            if (_value is not null && IsNumber(cell) && IsNumber(_value))
            {
                return CompareNumbers(cell, _value);
            }

            if (_value is not null && !IsNumber(_value) && _value is not string && cell.GetType() == _value.GetType() && cell is IComparable comparable)
            {
                return comparable.CompareTo(_value);
            }

            // Text, or a value that did not parse as the column's type: compare what the grid shows.
            return _culture.Compare(cellText, _value as string ?? _text, CompareOptions.IgnoreCase);
        }

        /// <summary>The typed text as the column's kind of value: the user's culture first, then the invariant one.</summary>
        private static object? Parse(string text, Type type)
        {
            text = text.Trim();
            if (text.Length == 0)
            {
                return text;
            }

            if (type == typeof(double) || type == typeof(float) || type == typeof(decimal) || type == typeof(int) || type == typeof(long) ||
                type == typeof(short) || type == typeof(byte) || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte))
            {
                // "1250.5" typed on a Turkish system means 1250.5, not 12505: the user's culture is used only when
                // the text contains its decimal separator.
                var culture = text.Contains(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal)
                    ? CultureInfo.CurrentCulture
                    : CultureInfo.InvariantCulture;
                return decimal.TryParse(text, NumberStyles.Number, culture, out var number) ? number : text;
            }

            if (type == typeof(DateTime))
            {
                return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date) ||
                       DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
                    ? date
                    : text;
            }

            if (type == typeof(DateTimeOffset))
            {
                return DateTimeOffset.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date) ||
                       DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
                    ? date
                    : text;
            }

            if (type == typeof(bool))
            {
                return text.ToUpperInvariant() switch
                {
                    "1" or "TRUE" or "EVET" or "YES" => true,
                    "0" or "FALSE" or "HAYIR" or "NO" => false,
                    _ => text,
                };
            }

            return text;
        }

        // The text the grid shows, so that "contains" finds what the user sees.
        private static string Format(object value) => QueryResultSet.FormatValue(value) ?? string.Empty;
    }
}
