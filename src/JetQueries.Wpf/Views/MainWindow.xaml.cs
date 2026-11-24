using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JetQueries.Wpf.ViewModels;
using UiKit.Controls;
using UiKit.Theming;

namespace JetQueries.Wpf.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        StateChanged += OnStateChanged;

        // The theme button shows the theme it switches to.
        ThemeManager.ThemeChanged += UpdateThemeIcon;
        Closed += (_, _) => ThemeManager.ThemeChanged -= UpdateThemeIcon;
        UpdateThemeIcon();

        // Go to object opens over the window; the chosen object is revealed in the explorer.
        viewModel.GoToObjectRequested += (_, goTo) => new GoToObjectWindow(goTo) { Owner = this }.ShowDialog();

        viewModel.HistoryRequested += (_, _) => ShowHistory(viewModel);
        viewModel.SavedQueriesRequested += (_, _) => ShowSavedQueries(viewModel);
        viewModel.ShortcutsRequested += (_, _) =>
        {
            if (OwnedWindows.OfType<ShortcutsWindow>().FirstOrDefault() is { } open)
            {
                open.Activate();
            }
            else
            {
                new ShortcutsWindow(new KeyboardShortcutsViewModel()) { Owner = this }.Show();
            }
        };

        // The explorer's pin; a click elsewhere closes it while it floats.
        ExplorerView.PinButton.Click += OnExplorerPinClick;
        PreviewMouseDown += OnShellPreviewMouseDown;

        // The explorer comes back as it was left (shown or hidden, pinned or not, and its width).
        Loaded += async (_, _) => ApplyLayout(await viewModel.LoadLayoutAsync());

        Loaded += (_, _) =>
        {
            viewModel.Connections.RefreshCommand.Execute(null);
            _ = viewModel.History.LoadAsync();
        };
    }

    // .sql files dropped on the window open in tabs.
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        e.Handled = true;
        foreach (var file in files.Where(static f => System.IO.File.Exists(f)))
        {
            await viewModel.Documents.OpenFileAsync(file);
        }
    }

    private HistoryWindow? _history;
    private GridLength _explorerWidth = new(280);
    private bool _explorerPinned = true;

    /// <summary>The history window; one, brought forward when it is open already.</summary>
    internal HistoryWindow ShowHistory(MainWindowViewModel viewModel)
    {
        if (_history is null)
        {
            _history = new HistoryWindow(viewModel.History) { Owner = this };
            _history.Closed += (_, _) => _history = null;
            _history.Show();
        }
        else
        {
            if (_history.WindowState == WindowState.Minimized)
            {
                _history.WindowState = WindowState.Normal;
            }

            _history.Activate();
        }

        return _history;
    }

    private SavedQueriesWindow? _savedQueries;

    /// <summary>The saved queries window; one, brought forward when it is open already.</summary>
    internal SavedQueriesWindow? ShowSavedQueries(MainWindowViewModel viewModel)
    {
        if (viewModel.SavedQueries is not { } library)
        {
            return null;
        }

        if (_savedQueries is null)
        {
            _savedQueries = new SavedQueriesWindow(library) { Owner = this };
            _savedQueries.Closed += (_, _) => _savedQueries = null;
            _ = library.LoadAsync();
            _savedQueries.Show();
        }
        else
        {
            if (_savedQueries.WindowState == WindowState.Minimized)
            {
                _savedQueries.WindowState = WindowState.Normal;
            }

            _savedQueries.Activate();
        }

        return _savedQueries;
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    // A maximized window without Windows' frame reaches past the screen by the resize border: the content is
    // kept inside, and the maximize button shows "restore".
    private void OnStateChanged(object? sender, EventArgs e)
    {
        var border = SystemParameters.WindowResizeBorderThickness;
        Root.Margin = WindowState == WindowState.Maximized
            ? new Thickness(border.Left + 2, border.Top + 2, border.Right + 2, border.Bottom + 2)
            : new Thickness(0);
        UiKit.Controls.Ui.SetIcon(MaximizeButton, WindowState == WindowState.Maximized ? "\uE923" : "\uE922");
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? Strings.WindowRestore : Strings.WindowMaximize;
    }

    private void OnCommandPalette(object sender, System.Windows.Input.ExecutedRoutedEventArgs e) => ShowCommandPalette();

    private void OnCommandPaletteClick(object sender, RoutedEventArgs e) => ShowCommandPalette();

    /// <summary>The command palette over the top of the window: every menu command (but the palette itself).</summary>
    internal CommandPaletteWindow ShowCommandPalette()
    {
        var commands = CommandPaletteWindow.FromMenu(MainMenu).Where(static c => c.Title != Strings.MenuCommandPalette);
        var palette = new CommandPaletteWindow(new CommandPaletteViewModel(commands)) { Owner = this };
        var origin = WindowState == WindowState.Maximized ? new Point(0, 0) : new Point(Left, Top);
        palette.Left = origin.X + Math.Max(0, (ActualWidth - palette.Width) / 2);
        palette.Top = origin.Y + 70;
        palette.Show();
        return palette;
    }

    // View, Explorer: hides the explorer panel and its splitter; showing it again restores its width.
    private void OnToggleExplorer(object sender, RoutedEventArgs e)
    {
        ShowExplorer(ExplorerMenuItem.IsChecked);
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SaveLayout(CurrentLayout());
        }
    }

    // A width is remembered once the splitter is let go (closing the window may not wait for a write).
    private void OnExplorerResized(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SaveLayout(CurrentLayout());
        }
    }

    private void ShowExplorer(bool shown)
    {
        if (ExplorerColumn.Width.Value > 0)
        {
            _explorerWidth = ExplorerColumn.Width;
        }

        ExplorerMenuItem.IsChecked = shown;
        var docked = shown && _explorerPinned;
        var floating = shown && !_explorerPinned && ExplorerRailButton.IsChecked == true;
        ExplorerSplitter.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        ExplorerColumn.MinWidth = docked ? 200 : 0;
        ExplorerColumn.Width = docked ? _explorerWidth : new GridLength(0);
        SplitterColumn.Width = new GridLength(docked ? 8 : 0);
        ExplorerRail.Visibility = shown && !_explorerPinned ? Visibility.Visible : Visibility.Collapsed;
        if (!floating)
        {
            ExplorerRailButton.IsChecked = false;
        }

        ExplorerView.PinButton.IsChecked = _explorerPinned;
        ExplorerView.PinButton.ToolTip = _explorerPinned ? Strings.ExplorerUnpinTip : Strings.ExplorerPinTip;
        Ui.SetIcon(ExplorerView.PinButton, _explorerPinned ? "\uE718" : "\uE77A");

        // Docked: in its column. Unpinned: over the tabs, from the rail, while open.
        Grid.SetColumnSpan(ExplorerPanel, docked ? 1 : 3);
        ExplorerPanel.HorizontalAlignment = docked ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        ExplorerPanel.Width = docked ? double.NaN : _explorerWidth.Value;
        ExplorerPanel.Effect = docked ? null : new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = 0.3 };
        ExplorerPanel.Visibility = docked || floating ? Visibility.Visible : Visibility.Collapsed;
    }

    // The pin in the explorer: pinned keeps the panel beside the tabs; unpinned gives their room back and leaves a
    // rail at the left edge that opens the panel over them.
    private void OnExplorerPinClick(object sender, RoutedEventArgs e)
    {
        _explorerPinned = ExplorerView.PinButton.IsChecked == true;
        ExplorerRailButton.IsChecked = false;
        ShowExplorer(ExplorerMenuItem.IsChecked);
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SaveLayout(CurrentLayout());
        }
    }

    private void OnExplorerRailClick(object sender, RoutedEventArgs e)
    {
        ShowExplorer(ExplorerMenuItem.IsChecked);
        if (ExplorerRailButton.IsChecked == true)
        {
            ExplorerView.Tree.Focus();
        }
    }

    /// <summary>Opens or closes the unpinned explorer over the tabs (nothing while it is pinned).</summary>
    internal void SetExplorerFlyout(bool open)
    {
        if (_explorerPinned)
        {
            return;
        }

        ExplorerRailButton.IsChecked = open;
        ShowExplorer(ExplorerMenuItem.IsChecked);
    }

    /// <summary>Pins or unpins the explorer, as its pin button does.</summary>
    internal void SetExplorerPinned(bool pinned)
    {
        ExplorerView.PinButton.IsChecked = pinned;
        OnExplorerPinClick(this, new RoutedEventArgs());
    }

    /// <summary>Whether the explorer is pinned beside the tabs.</summary>
    internal bool IsExplorerPinned => _explorerPinned;

    // A click outside the floating explorer (and its rail) closes it.
    private void OnShellPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_explorerPinned || ExplorerPanel.Visibility != Visibility.Visible || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (!IsWithin(source, ExplorerPanel) && !IsWithin(source, ExplorerRail))
        {
            SetExplorerFlyout(false);
        }
    }

    // Esc in the floating explorer closes it (once its filter is empty: Esc clears that first).
    private void OnExplorerPanelKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_explorerPinned && ExplorerView.Filter.Text.Length == 0)
        {
            SetExplorerFlyout(false);
            e.Handled = true;
        }
    }

    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    {
        for (var current = node; current is not null;
             current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Puts a remembered layout into effect.</summary>
    internal void ApplyLayout(ShellLayout layout)
    {
        _explorerWidth = new GridLength(Math.Clamp(layout.ExplorerWidth, 200, 900));
        _explorerPinned = layout.ExplorerPinned;
        ShowExplorer(layout.ExplorerVisible);
    }

    /// <summary>The layout as it is now.</summary>
    internal ShellLayout CurrentLayout()
    {
        var visible = ExplorerMenuItem.IsChecked;
        var width = visible && _explorerPinned ? ExplorerColumn.ActualWidth : _explorerWidth.Value;
        return new ShellLayout { ExplorerVisible = visible, ExplorerWidth = width > 0 ? width : 280, ExplorerPinned = _explorerPinned };
    }

    private void UpdateThemeIcon() => Ui.SetIcon(ThemeButton, ThemeManager.Current == AppTheme.Dark ? Icons.Sun : Icons.Moon);
}
