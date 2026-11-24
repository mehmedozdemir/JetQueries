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
        ShowPage(viewModel.SelectedSection.Key);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.SelectedSection))
            {
                ShowPage(viewModel.SelectedSection.Key);
            }
        };
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

    private void OnBrowseSyncFolder(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel { Sync: { } sync })
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Strings.SyncBrowseTitle, InitialDirectory = sync.Folder.Trim().Length > 0 ? sync.Folder.Trim() : null };
        if (dialog.ShowDialog(this) == true)
        {
            sync.Folder = dialog.FolderName;
        }
    }

    /// <summary>Shows one page and scrolls it to its top.</summary>
    private void ShowPage(string key)
    {
        AppearancePage.Visibility = Show(key == "appearance");
        EditorPage.Visibility = Show(key == "editor");
        ResultsPage.Visibility = Show(key == "results");
        QueryPage.Visibility = Show(key == "query");
        FormatPage.Visibility = Show(key == "format");
        SnippetsPage.Visibility = Show(key == "snippets");
        UpdatesPage.Visibility = Show(key == "updates");
        SyncPage.Visibility = Show(key == "sync");
        Pages.ScrollToTop();

        static Visibility Show(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
    }
}
