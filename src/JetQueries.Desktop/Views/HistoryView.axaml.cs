using Avalonia.Controls;
using Avalonia.Input;
using JetQueries.Desktop.ViewModels;

namespace JetQueries.Desktop.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    // Double-click opens the run's text in a new query tab.
    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is HistoryViewModel viewModel && viewModel.OpenSelectedCommand.CanExecute(null))
        {
            viewModel.OpenSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }
}
