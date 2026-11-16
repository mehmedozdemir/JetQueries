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
        DataContext = viewModel;
    }
}
