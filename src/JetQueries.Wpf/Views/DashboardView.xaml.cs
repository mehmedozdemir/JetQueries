using System.Windows.Controls;
using JetQueries.Presentation.ViewModels;
using UiKit.Controls;

namespace JetQueries.Wpf.Views;

/// <summary>A server's dashboard in a tab: tiles, charts and the databases. It reads only while on screen.</summary>
public partial class DashboardView : UserControl
{
    private static readonly object Schema = new();
    private DashboardViewModel? _viewModel;

    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as DashboardViewModel);
        IsVisibleChanged += (_, _) => UpdateShown();
        Loaded += (_, _) => UpdateShown();
        Unloaded += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.IsShown = false;
            }
        };
    }

    /// <summary>The databases grid, for tests.</summary>
    internal VirtualGrid Grid => DatabasesGrid;

    private void Attach(DashboardViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.IsShown = false;
            _viewModel.DatabasesChanged -= OnDatabasesChanged;
        }

        _viewModel = viewModel;
        if (viewModel is null)
        {
            return;
        }

        viewModel.DatabasesChanged += OnDatabasesChanged;
        ShowDatabases();
        UpdateShown();
    }

    private void UpdateShown()
    {
        if (_viewModel is not null)
        {
            _viewModel.IsShown = IsLoaded && IsVisible;
        }
    }

    private void OnDatabasesChanged(object? sender, EventArgs e) => ShowDatabases();

    private void ShowDatabases()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        var rows = viewModel.Databases.ToList();
        var kinds = new[]
        {
            GridColumnKind.Text, GridColumnKind.Text, GridColumnKind.Text, GridColumnKind.Number, GridColumnKind.Number,
            GridColumnKind.Text, GridColumnKind.Text, GridColumnKind.Text,
        };
        _databases ??= new DbaGrid(DatabasesGrid, () => _viewModel?.Exporter, () => _viewModel?.HeaderText ?? "dashboard");
        _databases.Show([.. DashboardViewModel.DatabaseColumns], kinds, rows.Count, (r, c) => rows[r].Cells[c], Schema);
    }

    private DbaGrid? _databases;
}
