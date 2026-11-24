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
        Grid.CellMenuOpening += AddCellMenuItems;
        Grid.SelectionChanged += () => _ = ShowSelectionStatsAsync();
        Grid.CurrentCellChanged += ShowValue;

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

    private void OnAppearanceChanged(object? sender, EventArgs e)
    {
        ApplyScale();

        // The data-type line under the headers may have been switched on or off.
        ShowView();
    }

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

    /// <summary>Enter applies the SQL filter; Esc puts back the one applied.</summary>
    private void OnSqlFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (_set is null)
        {
            return;
        }

        if (e.Key == Key.Enter && ApplySqlFilterButton.Command is { } apply && apply.CanExecute(_set))
        {
            apply.Execute(_set);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _set.SqlFilter = _set.AppliedFilter ?? string.Empty;
            SqlFilterBox.CaretIndex = SqlFilterBox.Text.Length;
            e.Handled = true;
        }
    }

    private int _statsVersion;

    /// <summary>Copy as (CSV, JSON, Markdown, INSERT, IN list) and Show value, under the grid's own copy items.</summary>
    private void AddCellMenuItems(ContextMenu menu, int row, int column)
    {
        if (_set is null)
        {
            return;
        }

        var copyAs = new MenuItem { Header = Strings.CopyAs };
        Ui.SetIcon(copyAs, Icons.Copy);
        foreach (var (format, header) in new[]
        {
            (ResultCopyFormat.Csv, Strings.CopyAsCsv), (ResultCopyFormat.Json, Strings.CopyAsJson), (ResultCopyFormat.Markdown, Strings.CopyAsMarkdown),
            (ResultCopyFormat.SqlInsert, Strings.CopyAsInsert), (ResultCopyFormat.SqlInList, Strings.CopyAsInList),
        })
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => CopyAs(format);
            copyAs.Items.Add(item);
        }

        var show = new MenuItem { Header = Strings.ShowValue };
        Ui.SetIcon(show, "\uE7B3");
        show.Click += (_, _) => SetValuePanel(true);

        // After "Copy" and "Copy with headers".
        menu.Items.Insert(Math.Min(2, menu.Items.Count), copyAs);
        menu.Items.Insert(Math.Min(3, menu.Items.Count), show);
    }

    /// <summary>The selected cells in a format, on the clipboard.</summary>
    internal string? CopyAs(ResultCopyFormat format)
    {
        if (_set is null || Grid.Selection is not { } selection)
        {
            return null;
        }

        var text = ResultCopy.Format(format, _set.Columns, _set.ShownRows(selection.FirstRow, selection.LastRow), selection.Columns);
        try
        {
            Clipboard.SetDataObject(text, true);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }

        return text;
    }

    /// <summary>Count, and for numbers sum, average, min and max of the selection, under the grid (computed in the background).</summary>
    private async Task ShowSelectionStatsAsync()
    {
        var version = ++_statsVersion;
        if (_set is not { } set || Grid.Selection is not { } selection || selection.RowCount * selection.Columns.Count < 2)
        {
            SelectionStatsText.Text = string.Empty;
            return;
        }

        SelectionStats stats;
        try
        {
            stats = await set.SelectionStatsAsync(selection.FirstRow, selection.LastRow, selection.Columns);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (version != _statsVersion)
        {
            return;
        }

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        SelectionStatsText.Text = stats.Numbers == 0
            ? string.Format(culture, System.Text.CompositeFormat.Parse(Strings.SelectionCells), stats.Cells)
            : string.Format(
                culture, System.Text.CompositeFormat.Parse(Strings.SelectionNumbers), stats.Cells, Number(stats.Sum), Number(stats.Average), Number(stats.Min), Number(stats.Max));

        string Number(decimal value) => Math.Round(value, 6).ToString("#,0.######", culture);
    }

    /// <summary>The selection totals as shown, for tests.</summary>
    internal string SelectionStatsShown => SelectionStatsText.Text;

    private void OnValuePanelToggle(object sender, RoutedEventArgs e) => SetValuePanel(ValuePanelToggle.IsChecked == true);

    private void OnCloseValuePanel(object sender, RoutedEventArgs e) => SetValuePanel(false);

    internal void SetValuePanel(bool shown)
    {
        ValuePanelHost.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        ValuePanelToggle.IsChecked = shown;
        ShowValue();
    }

    /// <summary>Fills the value panel with the current cell (when it is open).</summary>
    private void ShowValue()
    {
        if (ValuePanelHost.Visibility != Visibility.Visible || _set is null)
        {
            return;
        }

        var column = Grid.CurrentColumn;
        var row = Grid.CurrentRow;
        if (column < 0 || row < 0)
        {
            ValueColumn.Text = ValueInfo.Text = ValueText.Text = string.Empty;
            return;
        }

        var preview = ValuePreview.Describe(_set.ShownValue(row, column));
        ValueColumn.Text = _set.Columns[column].Header;
        ValueInfo.Text = string.Join(" · ", new[] { _set.Columns[column].DataType, preview.Kind.ToString(), preview.Size }.Where(static t => !string.IsNullOrEmpty(t)));
        ValueText.Text = preview.Text;
        ValueText.Foreground = (Brush)FindResource(preview.Kind == ValuePreviewKind.Null ? "Brush.TextMuted" : "Brush.Text");
        ValueText.TextWrapping = preview.Kind is ValuePreviewKind.Binary or ValuePreviewKind.Json or ValuePreviewKind.Xml ? TextWrapping.NoWrap : TextWrapping.Wrap;
        ValueText.HorizontalScrollBarVisibility = ValueText.TextWrapping == TextWrapping.NoWrap ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        var image = preview.Kind == ValuePreviewKind.Image ? Decode(preview.Bytes!) : null;
        ValueImage.Source = image;
        ValueImageHost.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        ValueText.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static System.Windows.Media.Imaging.BitmapImage? Decode(byte[] bytes)
    {
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.StreamSource = new System.IO.MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (System.IO.IOException)
        {
            return null;
        }
    }

    /// <summary>What the value panel shows, for tests.</summary>
    internal (string Column, string Info, string Text, bool Image) ValueShown => (ValueColumn.Text, ValueInfo.Text, ValueText.Text, ValueImage.Source is not null);

    /// <summary>The text view, for tests.</summary>
    internal string ShownText => TextView.Text;

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
            _set.PropertyChanged -= OnSetPropertyChanged;
        }

        _set = set;
        _hidden.Clear();
        FilterPopup.IsOpen = false;
        _sqlFilterOpen = false;
        UpdateSqlFilterBar();
        if (set is null)
        {
            Grid.Source = ArrayGridDataSource.Empty;
            return;
        }

        set.RowCountChanged += OnRowCountChanged;
        set.View.Changed += OnViewChanged;
        set.PropertyChanged += OnSetPropertyChanged;
        ApplyVisibleColumns();
        ShowView();
        ApplyMode(previous: ResultViewMode.Grid);
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
        if (_set.ViewMode == ResultViewMode.Text)
        {
            _ = ShowTextAsync();
        }
    }

    private ResultViewMode _mode = ResultViewMode.Grid;
    private int _textVersion;

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (_set is not null && sender is FrameworkElement { Tag: string tag } && Enum.TryParse<ResultViewMode>(tag, out var mode))
        {
            _set.ViewMode = mode;
        }

        // The toggles show the mode, not their own clicks.
        ApplyToggles();
    }

    private void OnSetPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryResultSet.ViewMode))
        {
            ApplyMode(_mode);
        }
        else if (e.PropertyName == nameof(QueryResultSet.IsFiltered))
        {
            UpdateSqlFilterBar();
        }
    }

    private bool _sqlFilterOpen;

    /// <summary>Opens or closes the SQL filter bar (it stays while a filter is applied).</summary>
    private void OnSqlFilterToggle(object sender, RoutedEventArgs e)
    {
        _sqlFilterOpen = SqlFilterToggle.IsChecked == true;
        UpdateSqlFilterBar();
        if (_sqlFilterOpen)
        {
            Dispatcher.BeginInvoke(() => SqlFilterBox.Focus(), DispatcherPriority.Input);
        }
    }

    /// <summary>Whether the SQL filter bar shows, for tests.</summary>
    internal bool SqlFilterShown => SqlFilterBar.Visibility == Visibility.Visible;

    /// <summary>Opens the SQL filter bar, as its toolbar button does.</summary>
    internal void OpenSqlFilter()
    {
        SqlFilterToggle.IsChecked = true;
        OnSqlFilterToggle(SqlFilterToggle, new RoutedEventArgs());
    }

    private void UpdateSqlFilterBar()
    {
        var shown = _sqlFilterOpen || _set?.IsFiltered == true;
        SqlFilterBar.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        SqlFilterToggle.IsChecked = shown;
    }

    /// <summary>Shows the grid, the text or the record view; the record view starts at the grid's current row and the grid comes back on the record's row.</summary>
    private void ApplyMode(ResultViewMode previous)
    {
        if (_set is null)
        {
            return;
        }

        var mode = _set.ViewMode;
        _mode = mode;
        Grid.Visibility = mode == ResultViewMode.Grid ? Visibility.Visible : Visibility.Collapsed;
        TextView.Visibility = mode == ResultViewMode.Text ? Visibility.Visible : Visibility.Collapsed;
        RecordView.Visibility = mode == ResultViewMode.Record ? Visibility.Visible : Visibility.Collapsed;
        ApplyToggles();

        // The grid's current row is the one place the row is kept: leaving the record view moves it there.
        if (previous == ResultViewMode.Record && mode != ResultViewMode.Record)
        {
            var row = _set.RecordIndex;
            Dispatcher.BeginInvoke(() => Grid.SelectRow(row), DispatcherPriority.Loaded);
        }

        switch (mode)
        {
            case ResultViewMode.Text:
                _ = ShowTextAsync();
                break;
            case ResultViewMode.Record:
                _set.RecordColumns = Grid.VisibleColumns;
                if (previous != ResultViewMode.Record && Grid.CurrentRow >= 0)
                {
                    _set.RecordIndex = Grid.CurrentRow;
                }

                break;
        }
    }

    private void ApplyToggles()
    {
        var mode = _set?.ViewMode ?? ResultViewMode.Grid;
        GridMode.IsChecked = mode == ResultViewMode.Grid;
        TextMode.IsChecked = mode == ResultViewMode.Text;
        RecordMode.IsChecked = mode == ResultViewMode.Record;
    }

    /// <summary>Builds the text table in the background; only the newest build is shown.</summary>
    private async Task ShowTextAsync()
    {
        if (_set is not { } set)
        {
            return;
        }

        var version = ++_textVersion;
        string text;
        try
        {
            text = await set.BuildTextAsync(Grid.VisibleColumns);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (version == _textVersion && ReferenceEquals(set, _set))
        {
            TextView.Text = text;
        }
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

    private void ApplyVisibleColumns()
    {
        Grid.VisibleColumns = _set is null || _hidden.Count == 0 ? null : Enumerable.Range(0, _set.Columns.Count).Where(c => !_hidden.Contains(c)).ToList();
        if (_set is not null)
        {
            _set.RecordColumns = Grid.VisibleColumns;
            if (_set.ViewMode == ResultViewMode.Text)
            {
                _ = ShowTextAsync();
            }
        }
    }

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

        // The header's filter button acts on mouse down. A popup that closes on outside clicks, opened while the
        // button is still down, would close again on its release; it opens once the button is up.
        var window = Window.GetWindow(this);
        if (Mouse.LeftButton == MouseButtonState.Pressed && window is not null)
        {
            void OnReleased(object sender, MouseButtonEventArgs e)
            {
                window.PreviewMouseLeftButtonUp -= OnReleased;
                Dispatcher.BeginInvoke(ShowFilterPopup, DispatcherPriority.Input);
            }

            window.PreviewMouseLeftButtonUp += OnReleased;
            return;
        }

        ShowFilterPopup();
    }

    private void ShowFilterPopup()
    {
        if (_filterColumn < 0 || _set is null)
        {
            return;
        }

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
