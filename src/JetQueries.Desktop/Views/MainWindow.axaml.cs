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
        Opened += (_, _) => viewModel.Connections.RefreshCommand.Execute(null);
    }
}
