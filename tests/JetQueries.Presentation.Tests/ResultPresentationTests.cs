using System.Globalization;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

public sealed class ResultPresentationTests
{
    public ResultPresentationTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    [Fact]
    public void The_text_view_aligns_columns_and_shows_null_and_line_breaks_plainly()
    {
        var set = Cards();

        var text = ResultTextFormatter.Format(set.Columns, set.SnapshotRows(), null, CancellationToken.None);

        Assert.Equal(
            """
            Id | City     | Balance | Note
            ---+----------+---------+----------
             1 | İstanbul | 1250.50 | first line second
             2 | Ankara   |  [NULL] | [NULL]
            10 | İzmir    |       0 | x

            """.ReplaceLineEndings("\n").Replace("---+----------+---------+----------", "---+-" + new string('-', 8) + "-+-" + new string('-', 7) + "-+-" + new string('-', 17), StringComparison.Ordinal),
            text);
    }

    [Fact]
    public void The_text_view_follows_the_columns_shown_and_cuts_what_is_too_long()
    {
        var set = new QueryResultSet("r", [new ResultColumn("Long", "nvarchar", typeof(string)), new ResultColumn("Id", "int", typeof(int))]);
        set.AddRows([[new string('x', 200), 1]]);

        var text = ResultTextFormatter.Format(set.Columns, set.SnapshotRows(), [1, 0], CancellationToken.None);

        var lines = text.Split('\n');
        Assert.StartsWith("Id | Long", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("…", lines[2], StringComparison.Ordinal);
        Assert.Equal(ResultTextFormatter.MaxColumnWidth, lines[2].Length - "Id | ".Length);
    }

    [Fact]
    public async Task The_record_view_walks_the_rows_in_the_order_shown()
    {
        var set = Cards();
        await set.View.SetSortAsync(0, descending: true);

        Assert.Equal("Record 1 / 3", set.RecordPosition);
        Assert.Equal(["Id", "City", "Balance", "Note"], set.RecordFields.Select(static f => f.Name));
        Assert.Equal(("10", "int"), (set.RecordFields[0].Value, set.RecordFields[0].DataType));
        Assert.False(set.FirstRecordCommand.CanExecute(null));
        Assert.True(set.NextRecordCommand.CanExecute(null));

        set.NextRecordCommand.Execute(null);
        Assert.Equal("Ankara", set.RecordFields[1].Value);
        Assert.True(set.RecordFields[3].IsNull);
        Assert.True(set.RecordFields[2].IsNull);

        set.LastRecordCommand.Execute(null);
        Assert.Equal("Record 3 / 3", set.RecordPosition);
        Assert.False(set.NextRecordCommand.CanExecute(null));

        // Columns hidden in the grid are left out; an index out of range is brought back in.
        set.RecordColumns = [1];
        Assert.Equal(["City"], set.RecordFields.Select(static f => f.Name));
        set.RecordIndex = 99;
        Assert.Equal(2, set.RecordIndex);
    }

    [Fact]
    public async Task The_text_is_built_in_the_order_shown()
    {
        var set = Cards();
        await set.View.SetFilterAsync(1, new ResultFilter(1, ResultFilterOperator.Contains, "z"));

        var text = await set.BuildTextAsync(null);

        Assert.Equal(3, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("İzmir", text, StringComparison.Ordinal);
    }

    private static QueryResultSet Cards()
    {
        var set = new QueryResultSet(
            "r",
            [
                new ResultColumn("Id", "int", typeof(int)), new ResultColumn("City", "nvarchar", typeof(string)),
                new ResultColumn("Balance", "decimal", typeof(decimal)), new ResultColumn("Note", "nvarchar", typeof(string)),
            ]);
        set.AddRows([[1, "İstanbul", 1250.50m, "first line\r\nsecond"], [2, "Ankara", null, null], [10, "İzmir", 0m, "x"]]);
        return set;
    }
}
