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
        Editor.SignatureSource = (text, offset, token) => DataContext is QueryDocumentViewModel viewModel
            ? viewModel.SignatureHelpAsync(text, offset, token)
            : Task.FromResult<SignatureHelp?>(null);
        Editor.SnippetLookup = shortcut => (DataContext as QueryDocumentViewModel)?.Snippets?.Find(shortcut)?.Text;

        // Settings, SQL editor: a table's or column's card on hover, " = ''" after a text column in a condition.
        Editor.HoverSource = (text, offset, token) => Services.AppearanceService.HoverInfo && DataContext is QueryDocumentViewModel viewModel
            ? viewModel.HoverAsync(text, offset, token)
            : Task.FromResult<SqlHoverInfo?>(null);
        Editor.CompletionSuffix = (text, start, end, item) => Services.AppearanceService.ValueSnippets ? ValueSnippet.After(text, start, end, item) : null;
        Editor.NavigationRequested += (_, offset) =>
        {
            if (DataContext is QueryDocumentViewModel viewModel)
            {
                _ = viewModel.ShowDetailsAsync(Editor.Text, offset);
            }
        };

        // Another query or another connection may mean another language: check the text again.
        DataContextChanged += (_, _) => _ = Editor.RefreshDiagnosticsAsync();
        Loaded += (_, _) => Editor.FocusText();

        // Preview: the text editor would otherwise turn Ctrl+Enter into a new line before the shortcut is seen.
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>The editor shortcut for a key: Ctrl+Shift+F format, Ctrl+/ comment, Ctrl+Shift+U / Ctrl+U case.</summary>
    private static string? EditorAction(Key key, ModifierKeys modifiers) => (key, modifiers) switch
    {
        (Key.F, ModifierKeys.Control | ModifierKeys.Shift) => "Format",
        (Key.Oem2 or Key.Divide, ModifierKeys.Control) => "Comment",
        (Key.U, ModifierKeys.Control | ModifierKeys.Shift) => "Upper",
        (Key.U, ModifierKeys.Control) => "Lower",
        _ => null,
    };

    private void OnEditorMenuOpened(object sender, System.Windows.RoutedEventArgs e)
    {
        // Insert snippet: the snippets of now, each with its shortcut.
        SnippetItem.Items.Clear();
        foreach (var snippet in (DataContext as QueryDocumentViewModel)?.Snippets?.Current ?? [])
        {
            var item = new MenuItem { Header = snippet.Description, InputGestureText = snippet.Shortcut + " + Tab" };
            item.Click += (_, _) =>
            {
                Editor.InsertSnippet(snippet.Text);
                Editor.FocusText();
            };
            SnippetItem.Items.Add(item);
        }

        SnippetItem.IsEnabled = SnippetItem.Items.Count > 0;
        CutItem.IsEnabled = CopyItem.IsEnabled = Editor.HasSelection;
        UndoItem.IsEnabled = Editor.CanUndo;
        RedoItem.IsEnabled = Editor.CanRedo;
    }

    /// <summary>Runs the selection, or the statement at the caret, which flashes so it is clear what ran.</summary>
    private void RunStatement(QueryDocumentViewModel viewModel)
    {
        viewModel.SelectedText = Editor.SelectedText;
        var caret = Editor.CaretOffset;
        if (!Editor.HasSelection && viewModel.StatementAt(caret) is { } span)
        {
            Editor.Flash(span.Start, span.Length);
        }

        if (viewModel.RunStatementCommand.CanExecute(caret))
        {
            viewModel.RunStatementCommand.Execute(caret);
        }
    }

    private void OnMenuRun(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not QueryDocumentViewModel viewModel || sender is not MenuItem { Tag: string tag })
        {
            return;
        }

        if (tag == "Statement")
        {
            RunStatement(viewModel);
            return;
        }

        viewModel.SelectedText = Editor.SelectedText;
        var command = tag == "NewTab" ? viewModel.RunInNewTabCommand : viewModel.RunCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void OnMenuEdit(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag })
        {
            Perform(tag);
        }
    }

    /// <summary>Runs an editor action from the menu or a shortcut.</summary>
    internal void Perform(string action)
    {
        switch (action)
        {
            case "Cut":
                Editor.Cut();
                break;
            case "Copy":
                Editor.Copy();
                break;
            case "Paste":
                Editor.Paste();
                break;
            case "SelectAll":
                Editor.SelectAll();
                break;
            case "Undo":
                Editor.Undo();
                break;
            case "Redo":
                Editor.Redo();
                break;
            case "Format":
                _ = FormatAsync();
                break;
            case "Comment":
                Editor.ToggleLineComment();
                break;
            case "Upper":
                Editor.ChangeCase(upper: true);
                break;
            case "Lower":
                Editor.ChangeCase(upper: false);
                break;
            case "Details":
                if (DataContext is QueryDocumentViewModel detailsOf)
                {
                    _ = detailsOf.ShowDetailsAsync(Editor.Text, Editor.CaretOffset);
                }

                break;
            case "Definition":
                if (DataContext is QueryDocumentViewModel viewModel)
                {
                    _ = viewModel.GoToDefinitionAsync(Editor.Text, Editor.CaretOffset);
                }

                break;
            case "Completion":
                _ = Editor.ShowCompletionAsync();
                break;
            case "Signature":
                _ = Editor.ShowSignatureHelpAsync();
                break;
            default:
                break;
        }

        Editor.FocusText();
    }

    /// <summary>Formats the selection, or the whole text; Ctrl+Z takes it back.</summary>
    internal Task<bool> FormatAsync() =>
        DataContext is QueryDocumentViewModel viewModel ? Editor.TransformAsync(viewModel.FormatAsync) : Task.FromResult(false);

    private void OnResultTabMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is TabItem { DataContext: QueryResultSet set } && DataContext is QueryDocumentViewModel viewModel &&
            viewModel.CloseResultCommand.CanExecute(set))
        {
            viewModel.CloseResultCommand.Execute(set);
            e.Handled = true;
        }
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

        var modifiers = Keyboard.Modifiers;
        var run = e.Key == Key.F5 && modifiers == ModifierKeys.None;
        if (e.Key == Key.Enter && modifiers == ModifierKeys.Control)
        {
            RunStatement(viewModel);
            e.Handled = true;
            return;
        }


        // DBeaver's "Execute in new tab" is Ctrl+\; Ctrl+Shift+Enter is easier on keyboards where \ needs AltGr.
        var runInNewTab = (e.Key == Key.Enter && modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) ||
            (e.Key is Key.Oem5 or Key.OemBackslash && modifiers == ModifierKeys.Control);
        if (run || runInNewTab)
        {
            viewModel.SelectedText = Editor.SelectedText;
            var command = run ? viewModel.RunCommand : viewModel.RunInNewTabCommand;
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }

            e.Handled = true;
        }
        else if (EditorAction(e.Key, modifiers) is { } action && Editor.IsKeyboardFocusWithin)
        {
            Perform(action);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !Editor.IsCompletionOpen && viewModel.CancelCommand.CanExecute(null))
        {
            viewModel.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }
}

/// <summary>True for a result set tab (which can be closed), false for the messages tab.</summary>
public sealed class IsResultSetConverter : System.Windows.Data.IValueConverter
{
    public static IsResultSetConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is QueryResultSet;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
