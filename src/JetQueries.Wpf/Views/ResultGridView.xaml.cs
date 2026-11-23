using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UiKit.Controls;

namespace JetQueries.Wpf.Views;

/// <summary>An active filter as a chip above the grid.</summary>
public sealed record FilterChip(ResultFilter Filter, string Column, string Condition);

/// <summary>A filter operator in the panel's list.</summary>
public sealed record FilterOperatorOption(ResultFilterOperator Operator, string Text);

/// <summary>
/// One result set in UiKit's virtual grid: follows the rows while they stream in, sorts on header clicks,
/// filters from the header's filter panel and from cell menus, and shows the active filters as chips. The
/// sorting and filtering themselves are the result set's <see cref="ResultSetView"/>.
/// </summary>
public partial class ResultGridView : UserControl
{
    private readonly DispatcherTimer _refresh;
    private readonly List<int> _hidden = [];
    private QueryResultSet? _set;
    private bool _pending;
    private int _filterColumn = -1;
    private double _zoom = 1;

    public ResultGridView()
    {
        InitializeComponent();

        // Rows arrive in batches of a few hundred; the grid is refreshed at most every 100 ms meanwhile.
        _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _refresh.Tick += (_, _) => OnRefreshTick();
        DataContextChanged += (_, e) => Attach(e.NewValue as QueryResultSet);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as QueryResultSet);

        Grid.SortRequested += (column, additive) => _ = _set?.View.ToggleSortAsync(column, additive);
        Grid.SortDirectionRequested += (column, direction) =>
            _ = _set?.View.SetSortAsync(column, direction is null ? null : direction == GridSortDirection.Descending);
        Grid.FilterRequested += OpenFilter;
        Grid.FilterByValueRequested += (column, row, exclude) => _ = _set?.View.FilterByValueAsync(row, column, exclude);
        Grid.HideColumnRequested += HideColumn;

        // Ctrl+wheel zooms the grid, Ctrl+0 goes back to 100 %; the base size comes from the settings.
        PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                Zoom = Editor.SqlEditor.StepZoom(Zoom, e.Delta > 0);
                e.Handled = true;
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D0 or Key.NumPad0)
            {
                Zoom = 1;
                e.Handled = true;
            }
        };
        Loaded += (_, _) =>
        {
            Services.AppearanceService.Changed += OnAppearanceChanged;
            ApplyScale();
        };
        Unloaded += (_, _) => Services.AppearanceService.Changed -= OnAppearanceChanged;
    }

    /// <summary>Zoom of the grid on top of the grid text size setting (1 = 100 %).</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, Editor.SqlEditor.MinZoom, Editor.SqlEditor.MaxZoom);
            ApplyScale();
        }
    }

    private void OnAppearanceChanged(object? sender, EventArgs e) => ApplyScale();

    private void ApplyScale()
    {
        var scale = Services.AppearanceService.GridScale * _zoom;
        Grid.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
        ZoomBadge.Content = ZoomTextConverter.Text(_zoom);
        ZoomBadge.Visibility = Math.Abs(_zoom - 1) < 0.001 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnZoomReset(object sender, RoutedEventArgs e) => Zoom = 1;

    // Enter in the page size box takes the value at once, as leaving the box does.
    private void OnFetchSizeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FetchSizeBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }

    /// <summary>The grid, for tests.</summary>
    internal VirtualGrid VirtualGrid => Grid;

    /// <summary>Hides a column, as the header menu does; the chip bar offers to show it again.</summary>
    internal void HideColumn(int column)
    {
        _hidden.Add(column);
        ApplyVisibleColumns();
        UpdateChips();
    }

    /// <summary>Opens the filter panel of a column, as the header's filter button does.</summary>
    internal void OpenFilter(int column) => OpenFilter(column, new Rect(0, 0, 160, 36));

    private void Attach(QueryResultSet? set)
    {
        if (ReferenceEquals(set, _set))
        {
            return;
        }

        if (_set is not null)
        {
            _set.RowCountChanged -= OnRowCountChanged;
            _set.View.Changed -= OnViewChanged;
        }

        _set = set;
        _hidden.Clear();
        FilterPopup.IsOpen = false;
        if (set is null)
        {
            Grid.Source = ArrayGridDataSource.Empty;
            return;
        }

        set.RowCountChanged += OnRowCountChanged;
        set.View.Changed += OnViewChanged;
        ApplyVisibleColumns();
        ShowView();
    }

    private void OnRowCountChanged(object? sender, EventArgs e)
    {
        _pending = true;
        if (!_refresh.IsEnabled)
        {
            _refresh.Start();
        }
    }

    private void OnRefreshTick()
    {
        _refresh.Stop();
        if (!_pending || _set is null)
        {
            return;
        }

        _pending = false;
        if (_set.View.IsActive)
        {
            // New rows go through the order and filters too; the view raises Changed when done.
            _ = _set.View.RefreshAsync();
        }
        else
        {
            ShowView();
        }
    }

    private void OnViewChanged(object? sender, EventArgs e)
    {
        // Raised on the UI thread by the view (its dispatcher); tests without one call it inline.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowView);
            return;
        }

        ShowView();
    }

    private void ShowView()
    {
        if (_set is null)
        {
            return;
        }

        var view = _set.View;
        Grid.Source = ResultGridSource.Snapshot(_set);
        Grid.Sorts = [.. view.Sorts.Select(static s => new GridSort(s.Column, s.Descending ? GridSortDirection.Descending : GridSortDirection.Ascending))];
        Grid.FilteredColumns = view.Filters.Select(static f => f.Column).ToHashSet();
        UpdateChips();
    }

    private void UpdateChips()
    {
        if (_set is null)
        {
            return;
        }

        var view = _set.View;
        Chips.ItemsSource = view.Filters.Select(f => new FilterChip(f, _set.Columns[f.Column].Header, Describe(f))).ToList();
        ClearAll.Visibility = view.Filters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowColumns.Visibility = _hidden.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowColumns.Content = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.HiddenColumns), _hidden.Count);
        RowsText.Text = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ResultRowsShown), view.RowCount, _set.RowCount);
        ChipBar.Visibility = view.Filters.Count > 0 || _hidden.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyVisibleColumns() =>
        Grid.VisibleColumns = _set is null || _hidden.Count == 0 ? null : Enumerable.Range(0, _set.Columns.Count).Where(c => !_hidden.Contains(c)).ToList();

    internal static string OperatorText(ResultFilterOperator op) => Strings.Find("FilterOp_" + op) ?? op.ToString();

    private static string Describe(ResultFilter filter) =>
        ResultFilter.NeedsValue(filter.Operator) ? $"{OperatorText(filter.Operator)} “{filter.Text}”" : OperatorText(filter.Operator);

    private void OpenFilter(int column, Rect header)
    {
        if (_set is null)
        {
            return;
        }

        _filterColumn = column;
        var info = _set.Columns[column];
        FilterTitle.Text = info.Header;
        FilterType.Text = info.DataType.Length > 0 ? info.DataType : info.ClrType.Name;

        // Text columns are searched; other kinds are compared.
        var isText = ResultGridSource.KindOf(info.ClrType) == GridColumnKind.Text;
        ResultFilterOperator[] operators = isText
            ?
            [
                ResultFilterOperator.Contains, ResultFilterOperator.NotContains, ResultFilterOperator.Equals, ResultFilterOperator.NotEquals,
                ResultFilterOperator.StartsWith, ResultFilterOperator.EndsWith, ResultFilterOperator.IsNull, ResultFilterOperator.IsNotNull,
            ]
            :
            [
                ResultFilterOperator.Equals, ResultFilterOperator.NotEquals, ResultFilterOperator.GreaterThan, ResultFilterOperator.GreaterOrEqual,
                ResultFilterOperator.LessThan, ResultFilterOperator.LessOrEqual, ResultFilterOperator.Contains, ResultFilterOperator.IsNull,
                ResultFilterOperator.IsNotNull,
            ];
        var options = operators.Select(static op => new FilterOperatorOption(op, OperatorText(op))).ToList();
        FilterOperator.ItemsSource = options;

        // The column's current filter, if it has one.
        var current = _set.View.Filters.LastOrDefault(f => f.Column == column);
        FilterOperator.SelectedItem = options.Find(o => o.Operator == current?.Operator) ?? options[0];
        FilterValue.Text = current?.Text ?? string.Empty;
        FilterError.Visibility = Visibility.Collapsed;

        FilterPopup.HorizontalOffset = Math.Max(0, Math.Min(header.Left, Grid.ActualWidth - 344));
        FilterPopup.VerticalOffset = header.Bottom + 2;
        FilterPopup.IsOpen = true;
        Dispatcher.BeginInvoke(() =>
        {
            FilterValue.Focus();
            FilterValue.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnOperatorChanged(object sender, SelectionChangedEventArgs e) =>
        FilterValue.Visibility = FilterOperator.SelectedItem is FilterOperatorOption { Operator: var op } && !ResultFilter.NeedsValue(op)
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void OnFilterApply(object sender, RoutedEventArgs e)
    {
        if (_set is null || _filterColumn < 0 || FilterOperator.SelectedItem is not FilterOperatorOption option)
        {
            return;
        }

        if (ResultFilter.NeedsValue(option.Operator) && string.IsNullOrWhiteSpace(FilterValue.Text))
        {
            FilterError.Visibility = Visibility.Visible;
            return;
        }

        var text = ResultFilter.NeedsValue(option.Operator) ? FilterValue.Text.Trim() : string.Empty;
        _ = _set.View.SetFilterAsync(_filterColumn, new ResultFilter(_filterColumn, option.Operator, text));
        FilterPopup.IsOpen = false;
    }

    private void OnFilterClear(object sender, RoutedEventArgs e)
    {
        if (_set is not null && _filterColumn >= 0)
        {
            _ = _set.View.SetFilterAsync(_filterColumn, null);
        }

        FilterPopup.IsOpen = false;
    }

    private void OnFilterCancel(object sender, RoutedEventArgs e) => FilterPopup.IsOpen = false;

    // Enter applies, Esc closes (DESIGN.md: filter popup).
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnFilterApply(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            FilterPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnRemoveChip(object sender, RoutedEventArgs e)
    {
        if (_set is not null && sender is FrameworkElement { DataContext: FilterChip chip })
        {
            _ = _set.View.RemoveFilterAsync(chip.Filter);
        }
    }

    private void OnClearAll(object sender, RoutedEventArgs e) => _ = _set?.View.ClearFiltersAsync();

    private void OnClearFiltersShortcut(object sender, ExecutedRoutedEventArgs e) => _ = _set?.View.ClearFiltersAsync();

    private void OnShowColumns(object sender, RoutedEventArgs e)
    {
        _hidden.Clear();
        ApplyVisibleColumns();
        UpdateChips();
    }
}
