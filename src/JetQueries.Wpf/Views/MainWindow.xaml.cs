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

    // View, Explorer: hides the explorer panel and its splitter; showing it again restores its width.
    private void OnToggleExplorer(object sender, RoutedEventArgs e)
    {
        var shown = ExplorerMenuItem.IsChecked;
        if (!shown)
        {
            _explorerWidth = ExplorerColumn.Width;
        }

        ExplorerPanel.Visibility = ExplorerSplitter.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        ExplorerColumn.MinWidth = shown ? 200 : 0;
        ExplorerColumn.Width = shown ? _explorerWidth : new GridLength(0);
    }

    private void UpdateThemeIcon() => Ui.SetIcon(ThemeButton, ThemeManager.Current == AppTheme.Dark ? Icons.Sun : Icons.Moon);
}
