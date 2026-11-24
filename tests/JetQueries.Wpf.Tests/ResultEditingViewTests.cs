using System.IO;
using System.Windows;
using System.Windows.Input;
using JetQueries.Presentation.Tests;
using JetQueries.Presentation.ViewModels;
using JetQueries.Sdk;
using JetQueries.Wpf.Views;
using UiKit.Controls;

namespace JetQueries.Wpf.Tests;

/// <summary>The data editor in the grid: a cell edited in place, its marks, rows added and deleted.</summary>
[Collection(WpfUi.Name)]
public sealed class ResultEditingViewTests
{
    [Fact]
    public async Task A_cell_is_edited_in_place_and_changes_are_marked()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        var colours = await WpfTestHost.RunAsync(async () =>
        {
            var set = new QueryResultSet(
                "Sonuç 1",
                [new ResultColumn("Id", "int", typeof(int)), new ResultColumn("CardNo", "nvarchar(20)", typeof(string)), new ResultColumn("Balance", "decimal(12,2)", typeof(decimal))],
                null, null, new ImmediateDispatcher());
            set.AddRows([[1, "5200 0001", 10m], [2, "5200 0002", 20m], [3, "5200 0003", 30m]]);
            ColumnInfo[] columns = [new("Id", "int", false, 1, IsPrimaryKey: true), new("CardNo", "nvarchar(20)", false, 2), new("Balance", "decimal(12,2)", true, 3)];
            var editor = new ResultEditor(set, new DatabaseObjectName(null, "dbo", "Cards"), null, columns, [0], canInsert: true);
            set.Editor = editor;

            var view = new ResultGridView { DataContext = set };
            var window = new Window { Content = view, Width = 640, Height = 260, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            await EditorActionsTests.Idle();
            var grid = view.Grid;

            // F2 on a cell opens the editor with its value; Enter keeps the text and moves down.
            grid.MoveTo(0, 1);
            Assert.True(grid.BeginEdit());
            await EditorActionsTests.Idle();
            Assert.True(grid.IsEditing);
            Assert.Equal("5200 0001", grid.ActiveEditor!.Text);
            grid.ActiveEditor!.Text = "Müşteri İşlemleri";
            grid.CommitEdit(Key.Down);
            Assert.False(grid.IsEditing);
            Assert.Equal("Müşteri İşlemleri", set.GetText(0, 1));
            Assert.True(editor.IsModified(0, 1));
            Assert.Equal(1, grid.CurrentRow);

            // Esc drops what was typed.
            Assert.True(grid.BeginEdit("9"));
            grid.CancelEdit();
            Assert.Equal("5200 0002", set.GetText(1, 1));

            // The key's cell of a deleted row cannot be edited; a row is added at the end.
            grid.MoveTo(2, 0);
            view.ToggleDeleteSelected();
            Assert.True(editor.IsDeleted(2));
            Assert.False(grid.BeginEdit());
            view.AddRow();
            await EditorActionsTests.Idle();
            Assert.Equal(4, set.RowCount);
            Assert.True(grid.IsEditing);
            grid.ActiveEditor!.Text = "4";
            grid.CommitEdit(Key.Right);
            Assert.Equal(4L, set.GetValue(3, 0));
            Assert.Equal(3, editor.ChangeCount);

            var result = ResultGridViewTests.Capture(window, Path.Combine(directory, "wpf-result-editing.png"));
            window.Close();
            return result;
        });

        Assert.True(colours > 12, $"rendered only {colours} distinct colours.");
    }
}
