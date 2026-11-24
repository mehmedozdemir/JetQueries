using System.Globalization;
using System.IO;
using System.Windows;
using JetQueries.Sdk;
using JetQueries.Wpf.Editor;
using UiKit.Theming;

namespace JetQueries.Wpf.Tests;

/// <summary>The completion list's look, signature help with parameter values, the Ctrl+hover link and the busy chip.</summary>
[Collection(WpfUi.Name)]
public sealed class EditorAssistTests
{
    [Fact]
    public async Task Completing_a_function_opens_its_signature_and_its_values()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);

        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            var results = new Dictionary<string, int>();
            var service = new JetQueries.Provider.SqlServer.SqlServerProvider().LanguageService!;
            var editor = new SqlEditor
            {
                LanguageService = service,
                CompletionSource = (text, offset, token) => service.GetCompletionsAsync(new SqlDocument(text), offset, token),
                SignatureSource = (text, offset, token) => ((ISignatureHelpProvider)service).GetSignatureHelpAsync(new SqlDocument(text), offset, token),
            };
            var window = new Window { Content = editor, Width = 900, Height = 420, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            editor.Text = "SELECT DATEA";
            editor.MoveCaret(editor.Text.Length);
            await EditorActionsTests.Idle();

            // The list: DATEADD with its short signature, the first row selected.
            await editor.ShowCompletionAsync();
            await EditorActionsTests.Idle();
            var list = editor.CompletionWindowShown!;
            Assert.Equal("DATEADD", Assert.Single(editor.CompletionLabels));
            Assert.NotNull(list.CompletionList.SelectedItem);
            results["list"] = ResultGridViewTests.Capture((FrameworkElement)list.Content, Path.Combine(directory, "wpf-completion-list.png"));

            // Enter: DATEADD() with the caret inside, the signature card and the datepart values.
            list.CompletionList.RequestInsertion(EventArgs.Empty);
            for (var i = 0; i < 50 && (editor.ShownSignature is null || !editor.IsCompletionOpen); i++)
            {
                await Task.Delay(20);
                await EditorActionsTests.Idle();
            }

            Assert.Equal("SELECT DATEADD()", editor.Text);
            Assert.Equal("DATEADD", editor.ShownSignature!.Signatures[0].Name);
            Assert.Equal(0, editor.ShownSignature.ActiveParameter);
            Assert.Contains("month", editor.CompletionLabels);
            results["signature"] = ResultGridViewTests.Capture(editor.SignatureCardShown!, Path.Combine(directory, "wpf-signature-help.png"));
            results["values"] = ResultGridViewTests.Capture((FrameworkElement)editor.CompletionWindowShown!.Content, Path.Combine(directory, "wpf-completion-values.png"));

            // Ctrl+hover: the name is the link (a keyword is not).
            editor.CompletionWindowShown!.Close();
            editor.Text = "SELECT * FROM dbo.Cards";
            await EditorActionsTests.Idle();
            Assert.Equal(new TextSpan(18, 5), editor.NameAt(20));
            Assert.Null(editor.NameAt(2));
            editor.ShowLink(editor.NameAt(20));
            Assert.Equal(new TextSpan(18, 5), editor.LinkSpan);

            // The busy chip while Ctrl+Click looks the object up.
            editor.BusyText = "Nesne aranıyor…";
            editor.IsBusy = true;
            Assert.Equal("Nesne aranıyor…", editor.BusyShown);
            editor.BusyText = null;
            Assert.Null(editor.BusyShown);

            ThemeManager.Apply(AppTheme.Dark, save: false);
            window.Close();
            return results;
        });

        Assert.All(colours, static pair => Assert.True(pair.Value > 8, $"{pair.Key} rendered only {pair.Value} distinct colours."));
    }

    [Fact]
    public async Task A_dot_or_a_filter_with_no_match_never_leaves_an_empty_list_open()
    {
        await WpfTestHost.RunAsync(async () =>
        {
            // Words offer tables; after "ts." the alias's columns.
            static Task<IReadOnlyList<CompletionItem>> Source(string text, int offset, CancellationToken token)
            {
                var start = offset;
                while (start > 0 && char.IsLetterOrDigit(text[start - 1]))
                {
                    start--;
                }

                var span = new TextSpan(start, offset - start);
                IReadOnlyList<CompletionItem> items = start > 0 && text[start - 1] == '.'
                    ? [new CompletionItem("MIFAREID", CompletionItemKind.Column, span), new CompletionItem("AMOUNT", CompletionItemKind.Column, span)]
                    : [new CompletionItem("TS_TABLE", CompletionItemKind.Table, span), new CompletionItem("TSQL_LOG", CompletionItemKind.Table, span)];
                return Task.FromResult(items);
            }

            var editor = new SqlEditor { CompletionSource = Source };
            var window = new Window { Content = editor, Width = 700, Height = 300, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            editor.Text = "SELECT * FROM T ts WHERE ts";
            editor.MoveCaret(editor.Text.Length);
            await EditorActionsTests.Idle();

            // The text keeps a gap from the line numbers' rule.
            var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(directory);
            Assert.True(ResultGridViewTests.Capture(editor, Path.Combine(directory, "wpf-editor-text.png")) > 2);
            await editor.ShowCompletionAsync();
            await EditorActionsTests.Idle();
            Assert.True(editor.IsCompletionOpen);

            // "." closes the word list and opens the columns.
            editor.TypeText(".");
            for (var i = 0; i < 50 && !editor.CompletionLabels.Contains("MIFAREID"); i++)
            {
                await Task.Delay(20);
                await EditorActionsTests.Idle();
            }

            Assert.Equal(["MIFAREID", "AMOUNT"], editor.CompletionLabels);

            // Typing what no column matches closes the list instead of leaving an empty card.
            editor.TypeText("x");
            editor.TypeText("y");
            await EditorActionsTests.Idle();
            Assert.False(editor.IsCompletionOpen);

            window.Close();
            return 0;
        });
    }
}
