using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using JetQueries.Presentation.ViewModels;
using UiKit.Theming;

namespace JetQueries.Wpf.Views;

/// <summary>
/// The library of saved queries in its own window (Tools, Saved queries): the queries by folder on the left, the
/// selected one's text on the right; Enter or a double-click opens it in a new tab. One window is reused.
/// </summary>
public partial class SavedQueriesWindow : Window
{
    private readonly SavedQueriesViewModel _viewModel;

    public SavedQueriesWindow(SavedQueriesViewModel viewModel)
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

    /// <summary>The text shown for the selected query, for tests.</summary>
    internal string PreviewText => Preview.Text;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SavedQueriesViewModel.Selected))
        {
            ShowSelected();
        }
    }

    private void ShowSelected() => Preview.Text = _viewModel.Selected?.Query.Sql ?? string.Empty;

    private void OnClose(object sender, ExecutedRoutedEventArgs e) => Close();

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected(e);

    // Enter opens the query, Ctrl+Enter runs it, Delete removes it, F2 edits it.
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && _viewModel.RunSelectedCommand.CanExecute(null))
        {
            _viewModel.RunSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OpenSelected(e);
        }
        else if (e.Key == Key.Delete && _viewModel.DeleteSelectedCommand.CanExecute(null))
        {
            _viewModel.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && _viewModel.EditSelectedCommand.CanExecute(null))
        {
            _viewModel.EditSelectedCommand.Execute(null);
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
}
