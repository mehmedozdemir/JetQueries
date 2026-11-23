using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JetQueries.Wpf.Views;

public partial class ConnectionsView : UserControl
{
    public ConnectionsView() => InitializeComponent();

    // WPF's TreeView.SelectedItem is read-only; the selection is handed to the view model from here.
    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is ConnectionsViewModel viewModel)
        {
            viewModel.Selected = e.NewValue as ExplorerNodeViewModel;
        }
    }

    // Double-click on a connection connects it; on anything else the tree's own expand/collapse applies.
    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ConnectionsViewModel { Selected: ConnectionNodeViewModel { IsConnected: false } } viewModel &&
            viewModel.ToggleConnectionCommand.CanExecute(null))
        {
            viewModel.ToggleConnectionCommand.Execute(null);
            e.Handled = true;
        }
    }
}
