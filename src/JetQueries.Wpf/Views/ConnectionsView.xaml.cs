using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JetQueries.Wpf.Views;

public partial class ConnectionsView : UserControl
{
    // "More": the menu of the less frequent actions, under the button.
    private void OnMoreActionsClick(object sender, RoutedEventArgs e)
    {
        MoreActionsMenu.PlacementTarget = MoreActionsButton;
        MoreActionsMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MoreActionsMenu.IsOpen = true;
    }

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
            (DataContext as ConnectionsViewModel)?.RefreshPicks();
        }
    }

    private Point _dragStart;
    private IReadOnlyList<ExplorerNodeViewModel> _dragged = [];
    private ExplorerNodeViewModel? _dragNode;
    private ExplorerNodeViewModel? _dropTarget;

    // Connections and folders can be dragged onto folders (or a connection, meaning its folder, or empty space: the top).
    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Tree);
        var node = NodeAt(e.OriginalSource as DependencyObject);
        _dragNode = node is ConnectionNodeViewModel or FolderNodeViewModel ? node : null;
        if (DataContext is not ConnectionsViewModel viewModel)
        {
            return;
        }

        // Ctrl+click picks several connections and folders (to drag, connect, delete or colour them together),
        // Shift+click everything between the selected one and the clicked one; a plain click forgets them, except
        // on one of the picked ones, which starts dragging them all.
        if (Keyboard.Modifiers == ModifierKeys.Control && node is ConnectionNodeViewModel or FolderNodeViewModel)
        {
            if (viewModel.MarkedConnections().Count == 0 && viewModel.MarkedFolders().Count == 0 &&
                viewModel.Selected is ConnectionNodeViewModel or FolderNodeViewModel && !ReferenceEquals(viewModel.Selected, node))
            {
                viewModel.Selected!.IsMarked = true;
            }

            node!.IsMarked = !node.IsMarked;
            viewModel.RefreshPicks();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Shift && node is ConnectionNodeViewModel or FolderNodeViewModel &&
                 viewModel.Selected is ConnectionNodeViewModel or FolderNodeViewModel)
        {
            viewModel.ClearMarks();
            viewModel.MarkRange(viewModel.Selected, node!);
            viewModel.RefreshPicks();
            e.Handled = true;
        }
        else if (node is not { IsMarked: true })
        {
            viewModel.ClearMarks();
        }
    }

    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragNode is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var moved = e.GetPosition(Tree) - _dragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var node = _dragNode;
        _dragNode = null;
        _dragged = node.IsMarked && DataContext is ConnectionsViewModel viewModel ? [.. viewModel.MarkedConnections()] : [node];
        DragDrop.DoDragDrop(Tree, new DataObject(typeof(ExplorerNodeViewModel), node), DragDropEffects.Move);
        SetDropTarget(null);
        DragCaption.IsOpen = false;
        _dragged = [];
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        var dragged = e.Data.GetData(typeof(ExplorerNodeViewModel)) as ExplorerNodeViewModel;
        var target = NodeAt(e.OriginalSource as DependencyObject);

        // Over an object below a connection: no drop there.
        if (target is not null and not (ConnectionNodeViewModel or FolderNodeViewModel))
        {
            target = null;
            e.Effects = DragDropEffects.None;
            SetDropTarget(null);
            e.Handled = true;
            return;
        }

        var allowed = _dragged.Count > 1
            ? _dragged.Any(d => ConnectionsViewModel.CanMove(d, target))
            : ConnectionsViewModel.CanMove(dragged, target);
        e.Effects = allowed ? DragDropEffects.Move : DragDropEffects.None;

        // Beside the cursor: where the drop lands ("Move to Customer A", "Move 3 connections to the top level").
        DragCaptionText.Text = allowed ? ConnectionsViewModel.DropCaption(Math.Max(1, _dragged.Count), target) : string.Empty;
        var at = e.GetPosition(Tree);
        DragCaption.HorizontalOffset = at.X + 18;
        DragCaption.VerticalOffset = at.Y + 14;
        DragCaption.IsOpen = allowed;
        SetDropTarget(allowed ? target as FolderNodeViewModel ?? (target is ConnectionNodeViewModel ? FolderAbove(target) : null) : null);
        e.Handled = true;
    }

    private void OnTreeDragLeave(object sender, DragEventArgs e)
    {
        SetDropTarget(null);
        DragCaption.IsOpen = false;
    }

    private async void OnTreeDrop(object sender, DragEventArgs e)
    {
        SetDropTarget(null);
        if (DataContext is ConnectionsViewModel viewModel && e.Data.GetData(typeof(ExplorerNodeViewModel)) is ExplorerNodeViewModel dragged)
        {
            var target = NodeAt(e.OriginalSource as DependencyObject);
            if (target is null or ConnectionNodeViewModel or FolderNodeViewModel)
            {
                e.Handled = true;
                DragCaption.IsOpen = false;
                if (_dragged.Count > 1)
                {
                    await viewModel.MoveAsync(_dragged, target);
                }
                else
                {
                    await viewModel.MoveAsync(dragged, target);
                }
            }
        }
    }

    private void SetDropTarget(ExplorerNodeViewModel? node)
    {
        if (ReferenceEquals(node, _dropTarget))
        {
            return;
        }

        _dropTarget?.IsDropTarget = false;
        _dropTarget = node;
        _dropTarget?.IsDropTarget = true;
    }

    /// <summary>The folder that holds a node in the tree; null at the top.</summary>
    private FolderNodeViewModel? FolderAbove(ExplorerNodeViewModel node) =>
        DataContext is ConnectionsViewModel viewModel ? FindParent(viewModel.Roots, node) : null;

    private static FolderNodeViewModel? FindParent(IEnumerable<ExplorerNodeViewModel> nodes, ExplorerNodeViewModel target)
    {
        foreach (var container in nodes.Where(static n => n is FolderNodeViewModel or GroupNodeViewModel))
        {
            if (container is FolderNodeViewModel folder && folder.Children.Contains(target))
            {
                return folder;
            }

            if (FindParent(container.Children, target) is { } inside)
            {
                return inside;
            }
        }

        return null;
    }

    /// <summary>The node of the tree row an element belongs to; null over empty space.</summary>
    private static ExplorerNodeViewModel? NodeAt(DependencyObject? element)
    {
        while (element is not null and not TreeViewItem and not TreeView)
        {
            element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return (element as TreeViewItem)?.DataContext as ExplorerNodeViewModel;
    }

    // Double-click on a connection connects it; on anything else the tree's own expand/collapse applies.
    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ConnectionsViewModel { Selected: KeyNodeViewModel } keys && NodeAt(e.OriginalSource as DependencyObject) is KeyNodeViewModel)
        {
            keys.OpenKeyCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (DataContext is ConnectionsViewModel { Selected: ConnectionNodeViewModel { IsConnected: false } } viewModel &&
            viewModel.ToggleConnectionCommand.CanExecute(null))
        {
            viewModel.ToggleConnectionCommand.Execute(null);
            e.Handled = true;
        }
    }
}
