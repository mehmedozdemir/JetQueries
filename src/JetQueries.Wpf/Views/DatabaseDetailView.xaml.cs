using System.ComponentModel;
using System.Windows.Controls;
using JetQueries.Presentation.ViewModels;
using UiKit.Controls;

namespace JetQueries.Wpf.Views;

/// <summary>A database in depth in a tab: tiles, the largest tables, settings, tables, files and backups.</summary>
public partial class DatabaseDetailView : UserControl
{
    private static readonly object TablesSchema = new();
    private static readonly object FilesSchema = new();
    private static readonly object BackupsSchema = new();
    private DatabaseDetailViewModel? _viewModel;

    public DatabaseDetailView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as DatabaseDetailViewModel);
    }

    /// <summary>The grids, for tests.</summary>
    internal (VirtualGrid Tables, VirtualGrid Files, VirtualGrid Backups) Grids => (TablesGrid, FilesGrid, BackupsGrid);

    private void Attach(DatabaseDetailViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelChanged;
            ShowAll();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DatabaseDetailViewModel.TableRows) or nameof(DatabaseDetailViewModel.FileRows) or nameof(DatabaseDetailViewModel.BackupRows))
        {
            ShowAll();
        }
    }

    private void ShowAll()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        Show(Tables, DatabaseDetailViewModel.TableColumns, [GridColumnKind.Text, GridColumnKind.Integer, GridColumnKind.Number, GridColumnKind.Number, GridColumnKind.Number],
            viewModel.TableRows, TablesSchema);
        Show(Files, DatabaseDetailViewModel.FileColumns,
            [GridColumnKind.Text, GridColumnKind.Text, GridColumnKind.Text, GridColumnKind.Number, GridColumnKind.Number, GridColumnKind.Number, GridColumnKind.Text, GridColumnKind.Text, GridColumnKind.Text],
            viewModel.FileRows, FilesSchema);
        Show(Backups, DatabaseDetailViewModel.BackupColumns,
            [GridColumnKind.Text, GridColumnKind.Text, GridColumnKind.Number, GridColumnKind.Number, GridColumnKind.Number, GridColumnKind.Text, GridColumnKind.Text],
            viewModel.BackupRows, BackupsSchema);
    }

    private static void Show(DbaGrid grid, IReadOnlyList<string> headers, GridColumnKind[] kinds, IReadOnlyList<IReadOnlyList<string?>> rows, object schema) =>
        grid.Show([.. headers], kinds, rows.Count, (r, c) => rows[r][c], schema);

    private DbaGrid Tables => _tables ??= new DbaGrid(TablesGrid, () => _viewModel?.Exporter, () => ExportName(Strings.DbLargestTables));

    private DbaGrid Files => _files ??= new DbaGrid(FilesGrid, () => _viewModel?.Exporter, () => ExportName(Strings.DbFiles));

    private DbaGrid Backups => _backups ??= new DbaGrid(BackupsGrid, () => _viewModel?.Exporter, () => ExportName(Strings.DbBackups));

    private DbaGrid? _tables;
    private DbaGrid? _files;
    private DbaGrid? _backups;

    private string ExportName(string table) => (_viewModel?.HeaderText ?? "database") + " - " + table;
}
