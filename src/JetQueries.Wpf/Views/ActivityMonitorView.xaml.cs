using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using JetQueries.Presentation.ViewModels;
using UiKit.Controls;

namespace JetQueries.Wpf.Views;

/// <summary>
/// A connection's activity monitor in a tab: counters, the sessions in a grid, the blocking chains and the chosen
/// session with its SQL. It reads only while it is on screen.
/// </summary>
public partial class ActivityMonitorView : UserControl
{
    // The grid's column layout stays the same between reads, so widths and scrolling are kept.
    private static readonly object Schema = new();
    private ActivityMonitorViewModel? _viewModel;
    private bool _moving;

    public ActivityMonitorView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as ActivityMonitorViewModel);
        IsVisibleChanged += (_, _) => UpdateShown();
        Unloaded += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.IsShown = false;
            }
        };
        Loaded += (_, _) => UpdateShown();
        SessionsGrid.CurrentCellChanged += OnCurrentCellChanged;
        _sessions = new DbaGrid(SessionsGrid, () => _viewModel?.Exporter, () => (_viewModel?.HeaderText ?? "activity") + " - " + Strings.ActivitySessions);
    }

    private readonly DbaGrid _sessions;

    private async void OnExport(object sender, RoutedEventArgs e) => await _sessions.ExportAsync();

    /// <summary>The sessions grid, for tests.</summary>
    internal VirtualGrid Grid => SessionsGrid;

    private void Attach(ActivityMonitorViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.IsShown = false;
            _viewModel.RowsChanged -= OnRowsChanged;
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = viewModel;
        if (viewModel is null)
        {
            return;
        }

        viewModel.RowsChanged += OnRowsChanged;
        viewModel.PropertyChanged += OnViewModelChanged;
        SqlView.LanguageService = viewModel.LanguageService;
        ShowRows();
        ShowSelected();
        UpdateShown();
    }

    private void UpdateShown()
    {
        if (_viewModel is not null)
        {
            _viewModel.IsShown = IsLoaded && IsVisible;
        }
    }

    private void OnRowsChanged(object? sender, EventArgs e) => ShowRows();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActivityMonitorViewModel.Selected))
        {
            ShowSelected();
        }
    }

    private void ShowRows()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        var rows = viewModel.Rows.ToList();
        var kinds = new[]
        {
            GridColumnKind.Integer, GridColumnKind.Text, GridColumnKind.Text, GridColumnKind.Text, GridColumnKind.Text, GridColumnKind.Text,
            GridColumnKind.Text, GridColumnKind.Text, GridColumnKind.Number, GridColumnKind.Integer, GridColumnKind.Number, GridColumnKind.Number,
            GridColumnKind.Integer, GridColumnKind.Integer, GridColumnKind.Integer, GridColumnKind.Text,
        };
        _moving = true;
        try
        {
            _sessions.Show([.. ActivityMonitorViewModel.Columns], kinds, rows.Count, (r, c) => rows[r].Cell(c), Schema);
            EmptyText.Visibility = rows.Count == 0 && viewModel.UpdatedText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var index = viewModel.Selected is { } chosen ? _sessions.ToShown(rows.IndexOf(chosen)) : -1;
            if (index >= 0)
            {
                SessionsGrid.MoveTo(index, Math.Max(0, SessionsGrid.CurrentColumn));
            }
        }
        finally
        {
            _moving = false;
        }
    }

    private void OnCurrentCellChanged()
    {
        if (_moving || _viewModel is not { } viewModel)
        {
            return;
        }

        var row = _sessions.ToSource(SessionsGrid.CurrentRow);
        viewModel.Selected = row >= 0 && row < viewModel.Rows.Count ? viewModel.Rows[row] : null;
    }

    private void ShowSelected() => SqlView.Text = _viewModel?.Selected?.Sql ?? string.Empty;

    // A session in a blocking chain is chosen in the grid too.
    private void OnChainSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is BlockingNode node && _viewModel is { } viewModel)
        {
            viewModel.Choose(node.Row.Id);
            var index = _sessions.ToShown(viewModel.Rows.IndexOf(viewModel.Selected!));
            if (index >= 0)
            {
                _moving = true;
                SessionsGrid.MoveTo(index, 0);
                _moving = false;
            }
        }
    }
}
