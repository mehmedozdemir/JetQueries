using System.Globalization;
using System.Windows;
using JetQueries.Sdk;
using JetQueries.Wpf.Editor;

namespace JetQueries.Wpf.Tests;

/// <summary>A completion that waits for the database says so at the caret, and still opens when the answer comes late.</summary>
[Collection(WpfUi.Name)]
public sealed class EditorSlowCompletionTests
{
    [Fact]
    public async Task A_slow_list_shows_a_reading_hint_and_opens_filtered_by_what_was_typed_meanwhile()
    {
        var outcome = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            var answer = new TaskCompletionSource<IReadOnlyList<CompletionItem>>();
            var editor = new SqlEditor
            {
                CompletionSource = (text, offset, token) => answer.Task,
            };
            var window = new Window { Content = editor, Width = 700, Height = 300, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            editor.Text = "SELECT * FROM Cards";
            editor.MoveCaret(editor.Text.Length);
            await EditorActionsTests.Idle();

            var asking = editor.ShowCompletionAsync();
            await Task.Delay(SqlEditor.ReadingHintDelay + TimeSpan.FromMilliseconds(150));
            var hintWhileWaiting = editor.ReadingHintShown;

            // A digit does not ask again: the late answer is the one to show.
            editor.TypeText("2");
            var start = "SELECT * FROM ".Length;
            answer.SetResult(
            [
                new CompletionItem("Cards1", CompletionItemKind.Table, new TextSpan(start, 5)),
                new CompletionItem("Cards2", CompletionItemKind.Table, new TextSpan(start, 5)),
            ]);
            await asking;
            await EditorActionsTests.Idle();
            var list = editor.CompletionWindowShown;
            var selected = (list?.CompletionList.SelectedItem as ICSharpCode.AvalonEdit.CodeCompletion.ICompletionData)?.Text;
            var hintAfter = editor.ReadingHintShown;
            list?.Close();
            window.Close();
            return (hintWhileWaiting, Shown: list is not null, selected, hintAfter);
        });

        Assert.True(outcome.hintWhileWaiting);
        Assert.True(outcome.Shown);
        Assert.Equal("Cards2", outcome.selected);
        Assert.False(outcome.hintAfter);
    }
}
