using JetQueries.Sdk;
using JetQueries.Wpf.Views;
using UiKit.Controls;

namespace JetQueries.Wpf.Tests;

public sealed class ResultGridSourceTests
{
    [Fact]
    public void A_snapshot_shows_the_rows_that_have_arrived_with_column_kinds()
    {
        var set = new QueryResultSet(
            "Result 1",
            [
                new ResultColumn("Id", "bigint", typeof(long)), new ResultColumn("Balance", "decimal", typeof(decimal)),
                new ResultColumn("Active", "bit", typeof(bool)), new ResultColumn("IssuedAt", "datetime2", typeof(DateTime)),
                new ResultColumn("Note", "nvarchar", typeof(string)),
            ]);
        set.AddRows([[1L, 1250.5m, true, new DateTime(2026, 1, 15), "Müşteri"], [2L, null, false, null, null]]);

        var first = ResultGridSource.Snapshot(set);
        set.AddRows([[3L, 0m, true, null, "x"]]);
        var second = ResultGridSource.Snapshot(set);

        Assert.Equal(2, first.RowCount);
        Assert.Equal(3, second.RowCount);
        Assert.Same(first.SchemaKey, second.SchemaKey);
        Assert.Equal(
            [GridColumnKind.Integer, GridColumnKind.Number, GridColumnKind.Boolean, GridColumnKind.DateTime, GridColumnKind.Text],
            Enumerable.Range(0, first.ColumnCount).Select(first.GetColumnKind));
        Assert.Equal("Balance", first.GetColumnName(1));
        Assert.Equal("1250.5", first.GetText(0, 1));
        Assert.Equal("2026-01-15", first.GetText(0, 3));
        Assert.Equal("Müşteri", first.GetText(0, 4));
        Assert.True(first.IsNull(1, 1));
        Assert.False(first.IsNull(1, 2));
        Assert.Equal(string.Empty, first.GetText(1, 4));
    }
}
