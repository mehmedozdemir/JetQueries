using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.ViewModels;
using UiKit.Controls;

namespace JetQueries.Wpf.Views;

/// <summary>
/// Gives a DBA screen's grid sorting (header clicks, Shift for more columns), column filters (the header's filter
/// button, a cell's "filter by this value"), and export of the rows as shown (cell menu). The rows stay the
/// screen's; <see cref="GridRowsView"/> keeps the order and the filters across refreshes, and the chosen row
/// stays chosen.
/// </summary>
internal sealed class DbaGrid
{
    private readonly VirtualGrid _grid;
    private readonly Func<GridExporter?> _exporter;
    private readonly Func<string> _exportName;
    private string[] _headers = [];
    private GridColumnKind[] _kinds = [];
    private object _schema = new();
    private Popup? _popup;

    /// <param name="grid">The grid.</param>
    /// <param name="exporter">The screen's exporter (null: no export item).</param>
    /// <param name="exportName">The file name offered for an export.</param>
    public DbaGrid(VirtualGrid grid, Func<GridExporter?> exporter, Func<string> exportName)
    {
        _grid = grid;
        _exporter = exporter;
        _exportName = exportName;
        grid.ShowFilterButtons = true;
        grid.SortRequested += (column, additive) => Change(() => View?.ToggleSort(column, additive));
        grid.SortDirectionRequested += (column, direction) => Change(() => View?.SetSort(column, direction is null ? null : direction == GridSortDirection.Descending));
        grid.FilterByValueRequested += (column, row, exclude) => Change(() => View?.FilterByValue(row, column, exclude));
        grid.FilterRequested += OpenFilter;
        grid.CellMenuOpening += AddMenuItems;
    }

    /// <summary>The rows as shown; null before the first rows.</summary>
    public GridRowsView? View { get; private set; }

    /// <summary>Whether a filter hides rows.</summary>
    public bool IsFiltered => View?.Filters.Count > 0;

    /// <summary>Shows new rows (a refresh): the sorts, filters and the chosen row stay.</summary>
    /// <param name="schema">The grid's schema key (an empty table gets a fresh one, so that its widths are not kept).</param>
    public void Show(string[] headers, GridColumnKind[] kinds, int count, Func<int, int, string?> cell, object schema)
    {
        if (View is null || !headers.SequenceEqual(_headers, StringComparer.Ordinal))
        {
            View = new GridRowsView(headers, [.. kinds.Select(static k => k is GridColumnKind.Number or GridColumnKind.Integer)]);
        }

        _headers = headers;
        _kinds = kinds;
        _schema = schema;
        View.SetRows(count, cell);
        Render();
    }

    /// <summary>The source row of a grid row; -1 when there is none.</summary>
    public int ToSource(int shownRow) => View is { } view && shownRow >= 0 && shownRow < view.RowCount ? view.SourceRow(shownRow) : -1;

    /// <summary>The grid row of a source row; -1 when it is filtered out.</summary>
    public int ToShown(int sourceRow) => View?.ShownRow(sourceRow) ?? -1;

    private void Render()
    {
        if (View is not { } view)
        {
            return;
        }

        _grid.Source = new ArrayGridDataSource(_headers, _kinds, view.RowCount, view.Cell, view.RowCount == 0 ? new object() : _schema);
        _grid.Sorts = [.. view.Sorts.Select(static s => new GridSort(s.Column, s.Descending ? GridSortDirection.Descending : GridSortDirection.Ascending))];
        _grid.FilteredColumns = view.FilteredColumns;
    }

    // A sort or filter change: the chosen row is found again in the new order.
    private void Change(Action change)
    {
        var chosen = ToSource(_grid.CurrentRow);
        var column = Math.Max(0, _grid.CurrentColumn);
        change();
        Render();
        if (chosen >= 0 && ToShown(chosen) is var shown and >= 0)
        {
            _grid.MoveTo(shown, column);
        }
    }

    private void AddMenuItems(ContextMenu menu, int row, int column)
    {
        menu.Items.Add(new Separator());
        if (IsFiltered)
        {
            var clear = new MenuItem { Header = Strings.GridClearFilters };
            Ui.SetIcon(clear, Icons.Clear);
            clear.Click += (_, _) => Change(() => View?.ClearFilters());
            menu.Items.Add(clear);
        }

        if (_exporter() is { } exporter && View is { } view)
        {
            var export = new MenuItem { Header = Strings.GridExport };
            Ui.SetIcon(export, Icons.Export);
            export.Click += async (_, _) => await exporter.ExportAsync(view, _exportName());
            menu.Items.Add(export);
        }
    }

    /// <summary>Exports the rows as shown (the screen's own Export button).</summary>
    public async Task ExportAsync()
    {
        if (_exporter() is { } exporter && View is { } view)
        {
            await exporter.ExportAsync(view, _exportName());
        }
    }

    private void OpenFilter(int column, Rect header)
    {
        if (View is not { } view)
        {
            return;
        }

        _popup?.IsOpen = false;
        var numeric = view.Numeric[column];
        GridTextFilterOperator[] operators = numeric
            ? [GridTextFilterOperator.Greater, GridTextFilterOperator.Less, GridTextFilterOperator.Equals, GridTextFilterOperator.NotEquals, GridTextFilterOperator.Contains,
                GridTextFilterOperator.IsEmpty, GridTextFilterOperator.IsNotEmpty]
            : [GridTextFilterOperator.Contains, GridTextFilterOperator.NotContains, GridTextFilterOperator.Equals, GridTextFilterOperator.NotEquals,
                GridTextFilterOperator.IsEmpty, GridTextFilterOperator.IsNotEmpty];
        var current = view.Filters.FirstOrDefault(f => f.Column == column);
        var choices = operators.Select(static op => new KeyValuePair<GridTextFilterOperator, string>(op, OperatorText(op))).ToList();
        var opBox = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Value", SelectedValuePath = "Key", Margin = new Thickness(0, 0, 0, 8) };
        opBox.SelectedValue = current?.Operator ?? operators[0];
        var valueBox = new TextBox { Text = current?.Text ?? string.Empty, Margin = new Thickness(0, 0, 0, 6) };
        var hint = new TextBlock { Text = Strings.GridNumberHint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(1, 0, 0, 10), Visibility = numeric ? Visibility.Visible : Visibility.Collapsed };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        hint.SetResourceReference(TextBlock.FontSizeProperty, "FontSize.Small");
        opBox.SelectionChanged += (_, _) =>
            valueBox.IsEnabled = opBox.SelectedValue is GridTextFilterOperator op && GridTextFilter.NeedsValue(op);
        valueBox.IsEnabled = GridTextFilter.NeedsValue((GridTextFilterOperator)opBox.SelectedValue);

        var apply = new Button { Content = Strings.FilterApply, MinWidth = 80, IsDefault = true };
        apply.SetResourceReference(FrameworkElement.StyleProperty, "Btn.Primary");
        var clear = new Button { Content = Strings.FilterClear, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var title = new TextBlock { Text = view.Headers[column], FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        var panel = new StackPanel { Width = 250 };
        panel.Children.Add(title);
        panel.Children.Add(opBox);
        panel.Children.Add(valueBox);
        panel.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(clear);
        buttons.Children.Add(apply);
        panel.Children.Add(buttons);
        var card = new Border { Child = panel, Padding = new Thickness(12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        card.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.Text");

        var popup = new Popup
        {
            Child = card, PlacementTarget = _grid, Placement = PlacementMode.Relative, HorizontalOffset = Math.Max(0, header.Right - 250), VerticalOffset = header.Bottom,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
        };
        void Apply()
        {
            var op = (GridTextFilterOperator)opBox.SelectedValue;
            if (GridTextFilter.NeedsValue(op) && valueBox.Text.Trim().Length == 0)
            {
                valueBox.Focus();
                return;
            }

            popup.IsOpen = false;
            Change(() => view.SetFilter(column, new GridTextFilter(column, op, valueBox.Text.Trim())));
        }

        apply.Click += (_, _) => Apply();
        clear.Click += (_, _) =>
        {
            popup.IsOpen = false;
            Change(() => view.SetFilter(column, null));
        };
        card.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Apply();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                popup.IsOpen = false;
                e.Handled = true;
            }
        };
        _popup = popup;
        popup.IsOpen = true;
        valueBox.Focus();
        valueBox.SelectAll();
    }

    internal static string OperatorText(GridTextFilterOperator op) => op switch
    {
        GridTextFilterOperator.Greater => ResultGridView.OperatorText(ResultFilterOperator.GreaterThan),
        GridTextFilterOperator.Less => ResultGridView.OperatorText(ResultFilterOperator.LessThan),
        GridTextFilterOperator.Contains => ResultGridView.OperatorText(ResultFilterOperator.Contains),
        GridTextFilterOperator.NotContains => ResultGridView.OperatorText(ResultFilterOperator.NotContains),
        GridTextFilterOperator.Equals => ResultGridView.OperatorText(ResultFilterOperator.Equals),
        GridTextFilterOperator.NotEquals => ResultGridView.OperatorText(ResultFilterOperator.NotEquals),
        GridTextFilterOperator.IsEmpty => Strings.GridOpIsEmpty,
        _ => Strings.GridOpIsNotEmpty,
    };
}
