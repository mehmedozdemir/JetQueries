using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using UiKit.Theming;

namespace JetQueries.Wpf.Views;

/// <summary>
/// The query history in its own window (main menu: Tools, Query history): runs on the left, the selected run's
/// full text on the right, opened in a new query tab with Enter or a double-click. One window is reused.
/// </summary>
public partial class HistoryWindow : Window
{
    private readonly HistoryViewModel _viewModel;

    public HistoryWindow(HistoryViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelChanged;
        Loaded += (_, _) =>
        {
            ShowSelected();
            Search.Focus();
        };
    }

    /// <summary>The text shown for the selected run, for tests.</summary>
    internal string PreviewText => Preview.Text;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HistoryViewModel.Selected))
        {
            ShowSelected();
        }
    }

    private void ShowSelected() => Preview.Text = _viewModel.Selected?.Entry.Sql ?? string.Empty;

    private void OnClose(object sender, ExecutedRoutedEventArgs e) => Close();

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected(e);

    // Enter opens the run, Delete removes it.
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenSelected(e);
        }
        else if (e.Key == Key.Delete && _viewModel.DeleteSelectedCommand.CanExecute(null))
        {
            _viewModel.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OpenSelected(RoutedEventArgs e)
    {
        if (_viewModel.OpenSelectedCommand.CanExecute(null))
        {
            _viewModel.OpenSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Selected is { } selected)
        {
            try
            {
                Clipboard.SetDataObject(selected.Entry.Sql, true);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
            }
        }
    }
}
