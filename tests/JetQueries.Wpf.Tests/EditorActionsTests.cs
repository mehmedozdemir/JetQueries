using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using JetQueries.Wpf.Services;
using JetQueries.Wpf.ViewModels;
using JetQueries.Wpf.Views;

namespace JetQueries.Wpf.Tests;

/// <summary>Query tabs and the editor driven through the real WPF controls.</summary>
[Collection(WpfUi.Name)]
public sealed class EditorActionsTests
{
    [Fact]
    public async Task The_close_button_of_a_query_tab_closes_it()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            await using var env = new TestEnvironment();
            var ui = new WpfUiDispatcher();
            var documents = new DocumentsViewModel(env.Manager, env.Metadata, env.History, env.Jobs, env.Dialogs, ui);
            documents.NewQueryCommand.Execute(null);
            documents.NewQueryCommand.Execute(null);
            var connections = env.Connections();
            var history = new HistoryViewModel(env.History, env.Jobs, env.Dialogs, ui, documents);
            var shell = new MainWindowViewModel(new NoTheme(), ui, connections, documents, history);
            var window = new MainWindow(shell) { Width = 1100, Height = 700, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            await Idle();

            var buttons = All<Button>(window).Where(static b => b.Style == (Style)Application.Current.FindResource("Btn.TabClose") && b.DataContext is QueryDocumentViewModel).ToList();
            Assert.Equal(3, buttons.Count);
            Assert.True(buttons[2].Command is not null, "no command bound");
            Assert.True(buttons[2].IsEnabled, "disabled");
            ((IInvokeProvider)new ButtonAutomationPeer(buttons[2]).GetPattern(PatternInterface.Invoke)).Invoke();
            await Idle();

            Assert.Equal(2, documents.Documents.Count);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task A_shortcut_and_tab_expand_a_snippet_in_the_editor()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var snippets = new SnippetService();
            var editor = new JetQueries.Wpf.Editor.SqlEditor { Text = "WITH x AS (\n    sel", SnippetLookup = s => snippets.Find(s)?.Text };
            var window = new Window { Content = editor, Width = 500, Height = 300, Left = -20000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            await Idle();

            // The snippet takes the line's indentation; the caret goes to its $0; one undo takes it back.
            editor.CaretOffset = editor.Text.Length;
            Assert.True(editor.ExpandSnippetAtCaret());
            Assert.Equal("WITH x AS (\n    SELECT *\n    FROM ", editor.Text);
            Assert.Equal(editor.Text.Length, editor.CaretOffset);
            editor.Undo();
            Assert.Equal("WITH x AS (\n    sel", editor.Text);

            // Not a whole word, or not a shortcut: Tab stays a Tab.
            editor.Text = "SELECT t.sel";
            editor.CaretOffset = editor.Text.Length;
            Assert.False(editor.ExpandSnippetAtCaret());
            editor.Text = "xsel";
            editor.CaretOffset = 4;
            Assert.False(editor.ExpandSnippetAtCaret());

            // From the menu, in place of the selection.
            editor.Text = "-- here";
            editor.SelectAll();
            editor.InsertSnippet("ORDER BY $0 DESC");
            Assert.Equal("ORDER BY  DESC", editor.Text);
            Assert.Equal("ORDER BY ".Length, editor.CaretOffset);

            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task The_editor_comments_changes_case_and_formats_as_single_undo_steps()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var editor = new JetQueries.Wpf.Editor.SqlEditor { Text = "select a\n  from t\nwhere x = 1" };
            var window = new Window { Content = editor, Width = 500, Height = 300, Left = -20000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            await Idle();

            // Caret on line 2, nothing selected: that line is commented, at its indent; again: uncommented.
            editor.CaretOffset = "select a\n  fr".Length;
            editor.ToggleLineComment();
            Assert.Equal("select a\n  -- from t\nwhere x = 1", editor.Text);
            editor.ToggleLineComment();
            Assert.Equal("select a\n  from t\nwhere x = 1", editor.Text);

            // The word at the caret; invariant case, so "i" stays "I" (not "İ").
            editor.Text = "select id from t";
            editor.CaretOffset = 8;
            editor.ChangeCase(upper: true);
            Assert.Equal("select ID from t", editor.Text);

            // The whole text is replaced in one step that Undo takes back.
            var changed = await editor.TransformAsync(static text => Task.FromResult<string?>(text.Replace(" ", "\n", StringComparison.Ordinal)));
            Assert.True(changed);
            Assert.Equal("select\nID\nfrom\nt", editor.Text);
            editor.Undo();
            Assert.Equal("select ID from t", editor.Text);
            Assert.False(await editor.TransformAsync(static _ => Task.FromResult<string?>(null)));

            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task The_editor_has_a_context_menu_with_run_format_and_editing()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            await using var env = new TestEnvironment();
            var query = env.Query();
            var view = new QueryDocumentView { DataContext = query };
            var window = new Window { Content = view, Width = 900, Height = 600, Left = -20000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            await Idle();

            var editor = (JetQueries.Wpf.Editor.SqlEditor)view.FindName("Editor");
            var menu = Assert.IsType<ContextMenu>(editor.ContextMenu);
            var headers = menu.Items.OfType<MenuItem>().Select(static m => (string)m.Header).ToList();
            Assert.Equal(
                ["Run statement", "Run", "Run in new tab", "Cut", "Copy", "Paste", "Select all", "Insert snippet", "Format SQL", "Comment / uncomment lines", "Upper case", "Lower case",
                 "Go to definition", "Show completion", "Undo", "Redo"],
                headers);

            query.Text = "select 1";
            await Idle();
            editor.CaretOffset = 0;
            view.Perform("Upper");
            await Idle();
            Assert.Equal("SELECT 1", query.Text);

            // The menu in both themes, for looking at.
            var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "screenshots");
            System.IO.Directory.CreateDirectory(directory);
            menu.PlacementTarget = editor;
            menu.IsOpen = true;
            foreach (var theme in new[] { UiKit.Theming.AppTheme.Dark, UiKit.Theming.AppTheme.Light })
            {
                UiKit.Theming.ThemeManager.Apply(theme, save: false);
                await Idle();
                var name = theme == UiKit.Theming.AppTheme.Dark ? "dark" : "light";
                Assert.True(ResultGridViewTests.Capture(menu, System.IO.Path.Combine(directory, $"wpf-editor-menu-{name}.png")) > 8);
            }

            menu.IsOpen = false;
            UiKit.Theming.ThemeManager.Apply(UiKit.Theming.AppTheme.Dark, save: false);

            // No connection: formatting says what it needs and leaves the text alone.
            Assert.False(await view.FormatAsync());
            Assert.StartsWith("Connect the query to a database", query.Status, StringComparison.Ordinal);
            window.Close();
            return 0;
        });
    }

    internal static IEnumerable<T> All<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var inner in All<T>(child))
            {
                yield return inner;
            }
        }
    }

    internal static async Task Idle()
    {
        for (var i = 0; i < 3; i++)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(30);
        }
    }

    private sealed class NoTheme : IThemeService
    {
#pragma warning disable CS0067 // Never raised: the theme does not change in these tests.
        public event EventHandler? Changed;
#pragma warning restore CS0067

        public bool IsDark => true;

        public void Toggle()
        {
        }

        public void SetDark(bool dark)
        {
        }
    }
}
