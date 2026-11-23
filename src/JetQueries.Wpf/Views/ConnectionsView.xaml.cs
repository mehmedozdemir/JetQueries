using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JetQueries.Wpf.Views;

public partial class ConnectionsView : UserControl
{
    public ConnectionsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ConnectionsViewModel old)
            {
                old.RevealRequested -= OnRevealRequested;
            }

            if (e.NewValue is ConnectionsViewModel viewModel)
            {
                viewModel.RevealRequested += OnRevealRequested;
            }
        };
    }

    /// <summary>The tree item of <paramref name="node"/>, created level by level (parents are expanded first); null when not shown.</summary>
    internal TreeViewItem? ContainerOf(ExplorerNodeViewModel node)
    {
        if (DataContext is not ConnectionsViewModel viewModel || FindPath(viewModel.Roots, node) is not { } path)
        {
            return null;
        }

        ItemsControl parent = Tree;
        TreeViewItem? item = null;
        foreach (var step in path)
        {
            parent.UpdateLayout();
            item = parent.ItemContainerGenerator.ContainerFromItem(step) as TreeViewItem;
            if (item is null)
            {
                return null;
            }

            if (!ReferenceEquals(step, node))
            {
                item.IsExpanded = true;
            }

            parent = item;
        }

        return item;
    }

    // Go to object: select the node in the tree and scroll to it.
    private void OnRevealRequested(object? sender, ExplorerNodeViewModel node) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (ContainerOf(node) is { } item)
            {
                item.IsSelected = true;
                item.BringIntoView();
                item.Focus();
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);

    private static List<ExplorerNodeViewModel>? FindPath(IEnumerable<ExplorerNodeViewModel> nodes, ExplorerNodeViewModel target)
    {
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node, target))
            {
                return [node];
            }

            if (FindPath(node.Children, target) is { } below)
            {
                below.Insert(0, node);
                return below;
            }
        }

        return null;
    }

    // WPF's TreeView.SelectedItem is read-only; the selection is handed to the view model from here.
    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is ConnectionsViewModel viewModel)
        {
            viewModel.Selected = e.NewValue as ExplorerNodeViewModel;
        }
    }

    // A right-click selects the node first, so that the menu acts on it.
    private void OnItemRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem item && e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(item, source) is null)
        {
            item.IsSelected = true;
            item.Focus();
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
