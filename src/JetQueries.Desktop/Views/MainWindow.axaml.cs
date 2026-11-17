using Avalonia.Controls;
using JetQueries.Desktop.ViewModels;

namespace JetQueries.Desktop.Views;

public partial class MainWindow : Window
{
    /// <summary>Used by the XAML previewer only.</summary>
    public MainWindow() => InitializeComponent();

    public MainWindow(MainWindowViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;

        // The window is on screen first; the saved connections arrive through a background job.
        Opened += (_, _) =>
        {
            viewModel.Connections.RefreshCommand.Execute(null);
            _ = viewModel.History.LoadAsync();
        };

        // Phase 0 grid benchmark: "--grid-spike" or "--grid-benchmark=<file>" shows it instead of the query tabs.
        if (Environment.GetCommandLineArgs().Any(static a => a == "--grid-spike" || a.StartsWith("--grid-benchmark=", StringComparison.Ordinal)))
        {
            Centre.Child = new GridSpikeView();
        }
    }
}
