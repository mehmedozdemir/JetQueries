using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using JetQueries.Presentation.ViewModels;
using JetQueries.Sdk;
using JetQueries.Wpf.Editor;

namespace JetQueries.Wpf.Tests;

/// <summary>The editor adds " = N''" after a column chosen in a condition, and shows a table's card on hover.</summary>
[Collection(WpfUi.Name)]
public sealed class EditorValueHoverTests
{
    [Fact]
    public async Task A_chosen_column_gets_its_comparison_and_a_table_shows_its_card()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            var editor = new SqlEditor
            {
                CompletionSource = (text, offset, token) => Task.FromResult<IReadOnlyList<CompletionItem>>(
                [
                    new CompletionItem("CardNo", CompletionItemKind.Column, new TextSpan(offset - 3, 3)) { Detail = "nvarchar(19) · c" },
                ]),
                CompletionSuffix = ValueSnippet.After,
                HoverSource = (text, offset, token) => Task.FromResult<SqlHoverInfo?>(new SqlHoverInfo("dbo.Cards", "Tablo", "4 sütun")
                {
                    Columns =
                    [
                        new ColumnInfo("Id", "bigint", false, 1, IsPrimaryKey: true), new ColumnInfo("CardNo", "nvarchar(19)", false, 2),
                        new ColumnInfo("City", "nvarchar(40)", true, 3), new ColumnInfo("Balance", "decimal(12,2)", true, 4),
                    ],
                }),
            };
            var window = new Window { Content = editor, Width = 900, Height = 320, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            editor.Text = "SELECT * FROM dbo.Cards c WHERE c.Car";
            editor.MoveCaret(editor.Text.Length);
            await EditorActionsTests.Idle();

            await editor.ShowCompletionAsync();
            await EditorActionsTests.Idle();
            editor.CompletionWindowShown!.CompletionList.RequestInsertion(EventArgs.Empty);
            await EditorActionsTests.Idle();

            Assert.Equal("SELECT * FROM dbo.Cards c WHERE c.CardNo = N''", editor.Text);
            Assert.Equal(editor.Text.Length - 1, editor.CaretOffset);

            await editor.ShowHoverAsync(20);
            await EditorActionsTests.Idle();
            Assert.Equal("dbo.Cards", editor.LastHover!.Title);
            var host = new Window { Content = new Border { Child = SqlEditor.Card(editor.LastHover), Padding = new Thickness(12) }, SizeToContent = SizeToContent.WidthAndHeight, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            host.SetResourceReference(Window.BackgroundProperty, "Brush.Surface");
            host.SetResourceReference(Window.ForegroundProperty, "Brush.Text");
            host.Show();
            await EditorActionsTests.Idle();
            var result = ResultGridViewTests.Capture(host, Path.Combine(directory, "wpf-editor-hover.png"));
            host.Close();
            window.Close();
            return result;
        });

        Assert.True(colours > 6, $"rendered only {colours} distinct colours.");
    }
}
