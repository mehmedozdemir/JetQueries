using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using JetQueries.Presentation.ViewModels;
using UiKit.Controls;

namespace JetQueries.Wpf.Views;

/// <summary>
/// A connection's performance analysis in a tab: heavy queries, missing indexes, index use and fragmentation in a
/// grid, the chosen row's SQL or script beside it.
/// </summary>
public partial class PerformanceView : UserControl
{
    // One column layout per part, so that widths are kept when a part is read again.
    private static readonly Dictionary<PerformanceSection, object> Schemas = Enum.GetValues<PerformanceSection>().ToDictionary(static s => s, static _ => new object());
    private PerformanceViewModel? _viewModel;
    private bool _moving;

    public PerformanceView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as PerformanceViewModel);
        RowsGrid.CurrentCellChanged += OnCurrentCellChanged;
        _rows = new DbaGrid(RowsGrid, () => _viewModel?.Exporter, () => (_viewModel?.HeaderText ?? "performance") + " - " + SectionName(_viewModel?.Section));
    }

    private readonly DbaGrid _rows;

    private static string SectionName(PerformanceSection? section) => section switch
    {
        PerformanceSection.MissingIndexes => Strings.PerfSectionMissing,
        PerformanceSection.IndexUsage => Strings.PerfSectionUsage,
        PerformanceSection.Fragmentation => Strings.PerfSectionFragmentation,
        _ => Strings.PerfSectionHeavy,
    };

    private async void OnExport(object sender, RoutedEventArgs e) => await _rows.ExportAsync();

    /// <summary>The grid, for tests.</summary>
    internal VirtualGrid Grid => RowsGrid;

    private void Attach(PerformanceViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = viewModel;
        if (viewModel is null)
        {
            return;
        }

        viewModel.PropertyChanged += OnViewModelChanged;
        DetailView.LanguageService = viewModel.LanguageService;
        ShowTable();
        DetailView.Text = viewModel.Detail;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PerformanceViewModel.Table):
                ShowTable();
                break;
            case nameof(PerformanceViewModel.Detail):
                DetailView.Text = _viewModel?.Detail ?? string.Empty;
                break;
            default:
                break;
        }
    }

    private void ShowTable()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        var table = viewModel.Table;
        var rows = table.Rows;
        _moving = true;
        try
        {
            _rows.Show(
                [.. table.Headers],
                [.. table.Numeric.Select(static n => n ? GridColumnKind.Number : GridColumnKind.Text)],
                rows.Count,
                (r, c) => c < rows[r].Cells.Count ? rows[r].Cells[c] : null,
                Schemas[viewModel.Section]);
            EmptyText.Visibility = rows.Count == 0 && !viewModel.IsLoading && !viewModel.NeedsDatabase ? Visibility.Visible : Visibility.Collapsed;
            var index = viewModel.Selected is { } chosen ? _rows.ToShown(IndexOf(rows, chosen)) : -1;
            if (index >= 0)
            {
                RowsGrid.MoveTo(index, 0);
            }
        }
        finally
        {
            _moving = false;
        }
    }

    private static int IndexOf(IReadOnlyList<PerformanceRow> rows, PerformanceRow row)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], row))
            {
                return i;
            }
        }

        return -1;
    }

    private void OnCurrentCellChanged()
    {
        if (_moving || _viewModel is not { } viewModel)
        {
            return;
        }

        var row = _rows.ToSource(RowsGrid.CurrentRow);
        var rows = viewModel.Table.Rows;
        viewModel.Selected = row >= 0 && row < rows.Count ? rows[row] : null;
    }
}
