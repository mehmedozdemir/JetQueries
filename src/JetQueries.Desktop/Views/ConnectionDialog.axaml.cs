using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using JetQueries.Desktop.Resources;
using JetQueries.Desktop.ViewModels;

namespace JetQueries.Desktop.Views;

public partial class ConnectionDialog : Window
{
    /// <summary>Used by the XAML previewer only.</summary>
    public ConnectionDialog() => InitializeComponent();

    public ConnectionDialog(ConnectionEditorViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, saved) => Close(saved);
    }

    // A file setting (an SQLite database): pick an existing file; a new one can still be typed.
    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SettingFieldViewModel field })
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.BrowseDatabaseFile,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.DatabaseFiles) { Patterns = ["*.db", "*.sqlite", "*.sqlite3", "*.db3"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files is [var file] && file.TryGetLocalPath() is { } path)
        {
            field.Value = path;
        }
    }
}
