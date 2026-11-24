using System.Windows;
using Microsoft.Win32;
using UiKit.Theming;

namespace JetQueries.Wpf.Views;

/// <summary>Import connections from DBeaver and SSMS: tick, then Import.</summary>
public partial class ImportConnectionsWindow : Window
{
    private readonly ImportConnectionsViewModel _viewModel;

    /// <param name="viewModel">The list.</param>
    /// <param name="discover">Look for DBeaver and SSMS settings on this computer when shown (tests pass false).</param>
    public ImportConnectionsWindow(ImportConnectionsViewModel viewModel, bool discover = true)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        viewModel.CloseRequested += (_, imported) =>
        {
            Imported = imported;
            Close();
        };
        viewModel.FileRequested += OnFileRequested;
        if (discover)
        {
            Loaded += (_, _) => _ = viewModel.DiscoverAsync();
        }
    }

    /// <summary>Something was imported.</summary>
    public bool Imported { get; private set; }

    // A file of the chosen source (DBeaver's JSON, SSMS's XML, or either).
    private void OnFileRequested(object? sender, string source)
    {
        var filter = source switch
        {
            ImportSource.DBeaver => "DBeaver (data-sources*.json)|data-sources*.json|JSON (*.json)|*.json",
            ImportSource.Ssms => "SSMS (RegSrvr*.xml)|RegSrvr*.xml|XML (*.xml)|*.xml",
            _ => Strings.ImportFileFilter,
        };
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            _ = _viewModel.AddFileAsync(dialog.FileName, _viewModel.SelectedSource?.Key == ImportSource.File ? null : _viewModel.SelectedSource?.Title);
        }
    }
}
