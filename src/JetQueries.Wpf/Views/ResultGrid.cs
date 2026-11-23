using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UiKit.Controls;

namespace JetQueries.Wpf.Views;

/// <summary>
/// A result set as UiKit's grid data: the rows that have arrived so far. A snapshot has a fixed row count; the
/// grid gets a new one as rows stream in, with the same schema key so column widths and scroll position stay.
/// </summary>
public sealed class ResultGridSource : IGridDataSource
{
    private readonly QueryResultSet _set;
    private readonly GridColumnKind[] _kinds;

    private ResultGridSource(QueryResultSet set, GridColumnKind[] kinds, int rowCount)
    {
        _set = set;
        _kinds = kinds;
        RowCount = rowCount;
    }

    public object SchemaKey => _set;

    public int RowCount { get; }

    public int ColumnCount => _kinds.Length;

    /// <summary>The rows of <paramref name="set"/> available now.</summary>
    public static ResultGridSource Snapshot(QueryResultSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new ResultGridSource(set, [.. set.Columns.Select(static c => KindOf(c.ClrType))], set.RowCount);
    }

    public static GridColumnKind KindOf(Type type) =>
        type == typeof(bool) ? GridColumnKind.Boolean
        : type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(sbyte) ||
          type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) ? GridColumnKind.Integer
        : type == typeof(decimal) || type == typeof(double) || type == typeof(float) ? GridColumnKind.Number
        : type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) ? GridColumnKind.DateTime
        : GridColumnKind.Text;

    public string GetColumnName(int column) => _set.Columns[column].Header;

    public GridColumnKind GetColumnKind(int column) => _kinds[column];

    public string GetText(int row, int column) => _set.GetText(row, column) ?? string.Empty;

    public bool IsNull(int row, int column) => _set.GetText(row, column) is null;
}

/// <summary>Shows one result set in UiKit's virtual grid and follows it while rows stream in.</summary>
public sealed class ResultGridView : ContentControl
{
    private readonly VirtualGrid _grid = new() { NullText = "NULL", ShowFilterButtons = false };
    private readonly DispatcherTimer _refresh;
    private QueryResultSet? _set;
    private bool _pending;

    public ResultGridView()
    {
        Content = _grid;
        Focusable = false;

        // Rows arrive in batches of a few hundred; the grid is refreshed at most every 100 ms meanwhile.
        _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            if (_pending && _set is not null)
            {
                _pending = false;
                _grid.Source = ResultGridSource.Snapshot(_set);
            }
        };
        DataContextChanged += (_, e) => Attach(e.NewValue as QueryResultSet);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as QueryResultSet);

    }

    /// <summary>The grid, for tests.</summary>
    internal VirtualGrid Grid => _grid;

    private void Attach(QueryResultSet? set)
    {
        if (ReferenceEquals(set, _set))
        {
            return;
        }

        if (_set is not null)
        {
            _set.RowCountChanged -= OnRowCountChanged;
        }

        _set = set;
        if (set is null)
        {
            _grid.Source = ArrayGridDataSource.Empty;
            return;
        }

        set.RowCountChanged += OnRowCountChanged;
        _grid.Source = ResultGridSource.Snapshot(set);
    }

    private void OnRowCountChanged(object? sender, EventArgs e)
    {
        _pending = true;
        if (!_refresh.IsEnabled)
        {
            _refresh.Start();
        }
    }
}
