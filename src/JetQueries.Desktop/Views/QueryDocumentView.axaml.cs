using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using JetQueries.Desktop.ViewModels;

namespace JetQueries.Desktop.Views;

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
            : Task.FromResult<IReadOnlyList<JetQueries.Sdk.CompletionItem>>([]);

        // Tunnel: the text editor would otherwise turn Ctrl+Enter into a new line before the shortcut is seen.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not QueryDocumentViewModel viewModel)
        {
            return;
        }

        var run = e.Key == Key.F5 || (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control);
        if (run)
        {
            viewModel.SelectedText = Editor.SelectedText;
            if (viewModel.RunCommand.CanExecute(null))
            {
                viewModel.RunCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape && viewModel.CancelCommand.CanExecute(null))
        {
            viewModel.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }
}
