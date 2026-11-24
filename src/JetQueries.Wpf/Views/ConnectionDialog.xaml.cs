using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using JetQueries.Wpf.Services;
using Microsoft.Win32;
using UiKit.Theming;

namespace JetQueries.Wpf.Views;

public partial class ConnectionDialog : Window
{
    private readonly ConnectionEditorViewModel _viewModel;

    public ConnectionDialog(ConnectionEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        viewModel.CloseRequested += (_, saved) =>
        {
            DialogResult = saved;
            Close();
        };

        // A failed test is shown in the danger colour, a successful one in the success colour.
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.PasswordProvided += OnPasswordProvided;
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.PasswordProvided -= OnPasswordProvided;
        };
        Loaded += (_, _) => FocusStep();
    }

    // A password read from a pasted connection string: into the box, from where it reaches the view model as typed ones do.
    private void OnPasswordProvided(object? sender, string password) => PasswordInput.Password = password;

    // Keyboard focus follows the step: the first type card, the connection string box, or the name.
    private void FocusStep() => Dispatcher.BeginInvoke(
        () =>
        {
            if (_viewModel.IsChoosingType)
            {
                TypeList.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
            }
            else if (_viewModel.IsConnectionStringMode)
            {
                ConnectionStringInput.Focus();
            }
            else if (_viewModel.Name.Length == 0)
            {
                NameInput.Focus();
            }
        },
        System.Windows.Threading.DispatcherPriority.Input);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionEditorViewModel.IsChoosingType) or nameof(ConnectionEditorViewModel.IsConnectionStringMode))
        {
            FocusStep();
        }

        if (e.PropertyName == nameof(ConnectionEditorViewModel.TestFailed))
        {
            TestResult.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, _viewModel.TestFailed ? "Brush.Danger" : "Brush.Success");
            TestResultCard.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, _viewModel.TestFailed ? "Brush.Danger" : "Brush.Success");
        }
    }

    // PasswordBox does not bind its password (by design); it is handed over as it is typed and kept nowhere else.
    private void OnPasswordChanged(object sender, RoutedEventArgs e) => _viewModel.Password = PasswordInput.Password;

    // A file setting (an SQLite database): pick an existing file; a new one can still be typed. A folder setting
    // (Oracle's TNS and wallet folders): pick a folder.
    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SettingFieldViewModel field })
        {
            return;
        }

        if (field.IsFolderPath)
        {
            var folder = new OpenFolderDialog { Title = Strings.BrowseFolder };
            if (System.IO.Directory.Exists(field.Value))
            {
                folder.InitialDirectory = field.Value;
            }

            if (folder.ShowDialog(this) == true)
            {
                field.Value = folder.FolderName;
            }

            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = Strings.BrowseDatabaseFile,
            Filter = $"{Strings.DatabaseFiles}|*.db;*.sqlite;*.sqlite3;*.db3|*.*|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            field.Value = dialog.FileName;
        }
    }
}
