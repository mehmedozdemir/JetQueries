using Avalonia.Controls;
using Avalonia.Input;
using JetQueries.Desktop.ViewModels;
using JetQueries.Presentation.ViewModels;

namespace JetQueries.Desktop.Views;

public partial class ConnectionsView : UserControl
{
    public ConnectionsView() => InitializeComponent();

    // Double-click on a connection connects it; on a folder the tree's own expand/collapse applies.
    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ConnectionsViewModel { Selected: ConnectionNodeViewModel { IsConnected: false } } viewModel &&
            viewModel.ToggleConnectionCommand.CanExecute(null))
        {
            viewModel.ToggleConnectionCommand.Execute(null);
            e.Handled = true;
        }
    }
}
