using System.Windows.Controls;
using System.Windows.Input;
using JetQueries.Sdk;

namespace JetQueries.Wpf.Views;

public partial class QueryDocumentView : UserControl
{
    public QueryDocumentView()
    {
        InitializeComponent();
        Editor.SelectionChanged += (_, _) =>
        {
            if (DataContext is QueryDocumentViewModel viewModel)
            {
                viewModel.SelectedText = Editor.SelectedText;
            }
        };

        // The editor asks whichever query it is showing at that moment.
        Editor.CompletionSource = (text, offset, token) => DataContext is QueryDocumentViewModel viewModel
            ? viewModel.CompleteAsync(text, offset, token)
            : Task.FromResult<IReadOnlyList<CompletionItem>>([]);
        Editor.DiagnosticSource = (text, token) => DataContext is QueryDocumentViewModel viewModel
            ? viewModel.DiagnoseAsync(text, token)
            : Task.FromResult<IReadOnlyList<SqlDiagnostic>>([]);
        Editor.NavigationRequested += (_, offset) =>
        {
            if (DataContext is QueryDocumentViewModel viewModel)
            {
                _ = viewModel.GoToDefinitionAsync(Editor.Text, offset);
            }
        };

        // Another query or another connection may mean another language: check the text again.
        DataContextChanged += (_, _) => _ = Editor.RefreshDiagnosticsAsync();
        Loaded += (_, _) => Editor.FocusText();

        // Preview: the text editor would otherwise turn Ctrl+Enter into a new line before the shortcut is seen.
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnZoomReset(object sender, System.Windows.RoutedEventArgs e)
    {
        Editor.Zoom = 1;
        Editor.FocusText();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not QueryDocumentViewModel viewModel)
        {
            return;
        }

        var run = e.Key == Key.F5 || (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control);
        if (run)
        {
            viewModel.SelectedText = Editor.SelectedText;
            if (viewModel.RunCommand.CanExecute(null))
            {
                viewModel.RunCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !Editor.IsCompletionOpen && viewModel.CancelCommand.CanExecute(null))
        {
            viewModel.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }
}
