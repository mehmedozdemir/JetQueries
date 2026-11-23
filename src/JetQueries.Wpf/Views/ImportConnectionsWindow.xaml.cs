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
        if (discover)
        {
            Loaded += (_, _) => _ = viewModel.DiscoverAsync();
        }
    }

    /// <summary>Something was imported.</summary>
    public bool Imported { get; private set; }

    private void OnChooseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = Strings.ImportFileFilter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            _ = _viewModel.AddFileAsync(dialog.FileName);
        }
    }
}
