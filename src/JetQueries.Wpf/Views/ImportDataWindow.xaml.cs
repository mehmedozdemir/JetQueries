using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.ViewModels;
using Microsoft.Win32;
using UiKit.Controls;
using UiKit.Theming;

namespace JetQueries.Wpf.Views;

/// <summary>
/// The Import data dialog of a table: the CSV file, how to read it, its first rows, the column mapping, and the
/// import itself (which runs in the background; the dialog shows the rows written and the outcome).
/// </summary>
public partial class ImportDataWindow : Window
{
    private static readonly CompositeFormat RowsWritten = CompositeFormat.Parse(Strings.ImportRows);
    private readonly ImportDataViewModel _viewModel;

    public ImportDataWindow(ImportDataViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        viewModel.PreviewRows.CollectionChanged += OnPreviewChanged;
        Loaded += (_, _) => ShowPreview();
        viewModel.PropertyChanged += OnViewModelChanged;

        // Closing while importing cancels the import.
        Closing += (_, _) =>
        {
            if (viewModel.CancelCommand.CanExecute(null))
            {
                viewModel.CancelCommand.Execute(null);
            }
        };
    }

    /// <summary>The preview's column headers, for tests.</summary>
    internal IReadOnlyList<string> PreviewColumns => [.. _viewModel.PreviewHeaders];

    private void OnPreviewChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.BeginInvoke(ShowPreview);

    private void ShowPreview()
    {
        var headers = _viewModel.PreviewHeaders.ToArray();
        var rows = _viewModel.PreviewRows.ToArray();
        PreviewGrid.Source = new ArrayGridDataSource(
            headers,
            [.. headers.Select(static _ => GridColumnKind.Text)],
            rows.Length,
            (r, c) => c < rows[r].Length ? rows[r][c] : null);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImportDataViewModel.Imported))
        {
            ProgressText.Text = string.Format(CultureInfo.CurrentCulture, RowsWritten, _viewModel.Imported);
        }
        else if (e.PropertyName == nameof(ImportDataViewModel.Failed))
        {
            StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, _viewModel.Failed ? "Brush.Danger" : "Brush.Text");
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.ImportBrowseTitle,
            Filter = $"{Strings.ImportTextFiles}|*.csv;*.txt;*.tsv|*.*|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.FilePath = dialog.FileName;
        }
    }
}
