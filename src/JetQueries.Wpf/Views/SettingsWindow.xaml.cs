using System.Windows;
using UiKit.Theming;

namespace JetQueries.Wpf.Views;

public partial class SettingsWindow : Window
{
    private bool _closingFromViewModel;
    private bool _closing;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        viewModel.CloseRequested += (_, _) =>
        {
            _closingFromViewModel = true;
            if (!_closing)
            {
                Close();
            }
        };

        // Closing with the title bar's × is a Cancel: the preview is undone.
        Closing += (_, _) =>
        {
            _closing = true;
            if (!_closingFromViewModel)
            {
                viewModel.CancelCommand.Execute(null);
            }
        };
    }
}
