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

    private void UpdateThemeIcon() => Ui.SetIcon(ThemeButton, ThemeManager.Current == AppTheme.Dark ? Icons.Sun : Icons.Moon);
}
