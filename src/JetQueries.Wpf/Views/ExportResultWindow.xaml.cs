using System.IO;
using System.Windows;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.ViewModels;
using Microsoft.Win32;
using UiKit.Theming;

namespace JetQueries.Wpf.Views;

/// <summary>The Export dialog of a result: format and options, which rows, the file.</summary>
public partial class ExportResultWindow : Window
{
    private readonly ExportResultViewModel _viewModel;

    public ExportResultWindow(ExportResultViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var extension = ResultExportOptions.Extension(_viewModel.SelectedFormat.Value);
        var dialog = new SaveFileDialog
        {
            Title = Strings.ExportBrowseTitle,
            FileName = Path.GetFileName(_viewModel.Path),
            InitialDirectory = Path.GetDirectoryName(_viewModel.Path),
            DefaultExt = extension,
            Filter = $"{_viewModel.SelectedFormat.Text}|*{extension}|*.*|*.*",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.Path = dialog.FileName;
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsValid)
        {
            DialogResult = true;
        }
        else
        {
            PathBox.Focus();
        }
    }
}
