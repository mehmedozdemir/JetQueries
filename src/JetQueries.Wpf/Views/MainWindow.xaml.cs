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

        Loaded += (_, _) =>
        {
            viewModel.Connections.RefreshCommand.Execute(null);
            _ = viewModel.History.LoadAsync();
        };
    }

    private void UpdateThemeIcon() => Ui.SetIcon(ThemeButton, ThemeManager.Current == AppTheme.Dark ? Icons.Sun : Icons.Moon);
}
