using System.Windows;
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

        // The theme button shows the theme it switches to.
        ThemeManager.ThemeChanged += UpdateThemeIcon;
        Closed += (_, _) => ThemeManager.ThemeChanged -= UpdateThemeIcon;
        UpdateThemeIcon();

        // Go to object opens over the window; the chosen object is revealed in the explorer.
        viewModel.GoToObjectRequested += (_, goTo) => new GoToObjectWindow(goTo) { Owner = this }.ShowDialog();

        viewModel.HistoryRequested += (_, _) => ShowHistory(viewModel);
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

        // The explorer comes back as it was left (shown or hidden, and its width).
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

    private void OnExit(object sender, RoutedEventArgs e) => Close();

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
        if (!shown && ExplorerColumn.Width.Value > 0)
        {
            _explorerWidth = ExplorerColumn.Width;
        }

        ExplorerMenuItem.IsChecked = shown;
        ExplorerPanel.Visibility = ExplorerSplitter.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        ExplorerColumn.MinWidth = shown ? 200 : 0;
        ExplorerColumn.Width = shown ? _explorerWidth : new GridLength(0);
    }

    /// <summary>Puts a remembered layout into effect.</summary>
    internal void ApplyLayout(ShellLayout layout)
    {
        _explorerWidth = new GridLength(Math.Clamp(layout.ExplorerWidth, 200, 900));
        ShowExplorer(layout.ExplorerVisible);
    }

    /// <summary>The layout as it is now.</summary>
    internal ShellLayout CurrentLayout()
    {
        var visible = ExplorerMenuItem.IsChecked;
        var width = visible ? ExplorerColumn.ActualWidth : _explorerWidth.Value;
        return new ShellLayout { ExplorerVisible = visible, ExplorerWidth = width > 0 ? width : 280 };
    }

    private void UpdateThemeIcon() => Ui.SetIcon(ThemeButton, ThemeManager.Current == AppTheme.Dark ? Icons.Sun : Icons.Moon);
}
