using System.Globalization;
using JetQueries.Core.Jobs;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

public sealed class ResultSetViewTests
{
    public ResultSetViewTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_header_click_sorts_then_turns_the_direction_and_shift_adds_keys()
    {
        var set = Cards();
        var view = set.View;

        await view.ToggleSortAsync(2, additive: false);
        Assert.Equal([new ResultSort(2, false)], view.Sorts);
        Assert.Equal(["5", "2", "1", "4", "3"], Ids(set));

        await view.ToggleSortAsync(2, additive: false);
        Assert.Equal([new ResultSort(2, true)], view.Sorts);
        // Equal balances keep their arrival order in either direction.
        Assert.Equal(["3", "1", "4", "2", "5"], Ids(set));

        // Balance first (a click on a descending column turns it ascending), then Id descending among equal balances.
        await view.ToggleSortAsync(2, additive: false);
        await view.ToggleSortAsync(0, additive: true);
        await view.ToggleSortAsync(0, additive: true);
        Assert.Equal([new ResultSort(2, false), new ResultSort(0, true)], view.Sorts);
        Assert.Equal(["5", "2", "4", "1", "3"], Ids(set));

        await view.SetSortAsync(0, null);
        Assert.Equal([new ResultSort(2, false)], view.Sorts);
        await view.SetSortAsync(2, null);
        Assert.False(view.IsActive);
        Assert.Null(view.RowMap);
    }

    [Fact]
    public void Nulls_come_first_numbers_compare_by_value_and_text_by_turkish_rules()
    {
        var turkish = CultureInfo.GetCultureInfo("tr-TR").CompareInfo;
        Assert.True(ResultSetView.CompareValues(null, 1, turkish) < 0);
        Assert.True(ResultSetView.CompareValues(2L, 10m, turkish) < 0);
        Assert.True(ResultSetView.CompareValues(2.5d, 2L, turkish) > 0);

        // ç after c, ı before i, ş after s; case does not decide unless all else is equal.
        Assert.True(ResultSetView.CompareValues("çiçek", "dal", turkish) < 0);
        Assert.True(ResultSetView.CompareValues("ışık", "ilk", turkish) < 0);
        Assert.True(ResultSetView.CompareValues("şeker", "tuz", turkish) < 0);
        Assert.True(ResultSetView.CompareValues("ankara", "Bursa", turkish) < 0);

        // SQLite may hold numbers and text in one column: numbers first.
        Assert.True(ResultSetView.CompareValues(9L, "1", turkish) < 0);
    }

    [Fact]
    public async Task Filters_compare_by_the_columns_kind_and_ignore_case_the_turkish_way()
    {
        var set = Cards();
        var view = set.View;

        await view.SetFilterAsync(1, new ResultFilter(1, ResultFilterOperator.Equals, "İSTANBUL"));
        Assert.Equal(["1", "3"], Ids(set));

        await view.SetFilterAsync(1, new ResultFilter(1, ResultFilterOperator.Contains, "zmi"));
        Assert.Equal(["2", "4"], Ids(set));

        // Numbers: "1250,5" (Turkish) and "1250.5" (typed with a point) mean the same.
        await view.SetFilterAsync(1, null);
        await view.SetFilterAsync(2, new ResultFilter(2, ResultFilterOperator.GreaterOrEqual, "1250,5"));
        Assert.Equal(["1", "3", "4"], Ids(set));
        await view.SetFilterAsync(2, new ResultFilter(2, ResultFilterOperator.GreaterOrEqual, "1250.5"));
        Assert.Equal(["1", "3", "4"], Ids(set));

        // NULL passes no comparison; IS NULL finds it.
        await view.SetFilterAsync(2, new ResultFilter(2, ResultFilterOperator.LessThan, "100"));
        Assert.Equal(["2"], Ids(set));
        await view.SetFilterAsync(2, new ResultFilter(2, ResultFilterOperator.IsNull));
        Assert.Equal(["5"], Ids(set));

        // Dates, in the user's format or ISO.
        await view.SetFilterAsync(2, null);
        await view.SetFilterAsync(3, new ResultFilter(3, ResultFilterOperator.GreaterThan, "01.02.2026"));
        Assert.Equal(["2", "4"], Ids(set));
        await view.SetFilterAsync(3, new ResultFilter(3, ResultFilterOperator.LessOrEqual, "2026-01-15"));
        Assert.Equal(["1", "3"], Ids(set));

        // Filters on two columns must both pass; clearing keeps the sort.
        await view.ToggleSortAsync(0, additive: false);
        await view.SetFilterAsync(1, new ResultFilter(1, ResultFilterOperator.StartsWith, "is"));
        Assert.Equal(["1", "3"], Ids(set));
        await view.ClearFiltersAsync();
        Assert.Equal(["1", "2", "3", "4", "5"], Ids(set));
        Assert.True(view.IsActive);
    }

    [Fact]
    public async Task Filter_by_a_cells_value_uses_the_value_itself_and_null_means_is_null()
    {
        var set = Cards();
        var view = set.View;
        await view.ToggleSortAsync(2, additive: true);

        // Shown row 0 is the NULL balance (card 5).
        await view.FilterByValueAsync(0, 2, exclude: true);
        Assert.Equal(ResultFilterOperator.IsNotNull, view.Filters[0].Operator);
        Assert.DoesNotContain("5", Ids(set));

        // Shown row 0 is now card 2 (balance 0): keep only rows with that balance.
        await view.FilterByValueAsync(0, 2, exclude: false);
        Assert.Equal(["2"], Ids(set));
        Assert.Equal(0m, view.Filters[1].Value);

        await view.RemoveFilterAsync(view.Filters[1]);
        Assert.Equal(4, view.RowCount);
    }

    [Fact]
    public async Task Work_runs_as_a_job_and_only_the_newest_answer_is_applied()
    {
        await using var jobs = new JobQueue();
        var ui = new ImmediateDispatcher();
        var set = new QueryResultSet("r", [new ResultColumn("n", "int", typeof(int))], null, jobs, ui);
        set.AddRows([.. Enumerable.Range(0, 200_000).Select(static i => new object?[] { (i * 7919) % 200_000 })]);
        var changes = 0;
        set.View.Changed += (_, _) => Interlocked.Increment(ref changes);

        var first = set.View.ToggleSortAsync(0, additive: false);
        var second = set.View.ToggleSortAsync(0, additive: false);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Equal([new ResultSort(0, true)], set.View.Sorts);
        Assert.Equal(199_999, set.GetValue(set.View.SourceRow(0), 0));
        Assert.Equal(0, set.GetValue(set.View.SourceRow(199_999), 0));
        Assert.InRange(changes, 1, 2);
    }

    [Fact]
    public async Task Rows_that_arrive_later_are_sorted_and_filtered_on_refresh()
    {
        var set = Cards();
        await set.View.SetFilterAsync(1, new ResultFilter(1, ResultFilterOperator.Equals, "Ankara"));
        Assert.Equal(["5"], Ids(set));

        set.AddRows([[6, "ANKARA", 10m, null]]);
        Assert.Equal(["5"], Ids(set));
        await set.View.RefreshAsync();
        Assert.Equal(["5", "6"], Ids(set));
    }

    /// <summary>Id, City, Balance, IssuedAt.</summary>
    [Fact]
    public async Task Filters_can_be_combined_with_or()
    {
        var set = Cards();
        var view = set.View;
        await view.FilterByValueAsync(0, 1, exclude: false);
        await view.SetFilterAsync(2, new ResultFilter(2, ResultFilterOperator.GreaterThan, "5000"));

        // İstanbul (Turkish case rules: "istanbul" too) AND balance > 5000.
        Assert.False(view.MatchAny);
        Assert.Equal(["3"], Ids(set));

        // İstanbul OR balance > 5000; then İstanbul OR balance < 1 (İzmir with 0).
        await view.SetMatchAnyAsync(true);
        Assert.True(view.MatchAny);
        Assert.Equal(["1", "3"], Ids(set));
        await view.SetFilterAsync(2, new ResultFilter(2, ResultFilterOperator.LessThan, "1"));
        Assert.Equal(["1", "2", "3"], Ids(set));
        await view.SetFilterAsync(2, new ResultFilter(2, ResultFilterOperator.GreaterThan, "5000"));

        // One filter, either way, is the same; none shows every row.
        await view.RemoveFilterAsync(view.Filters[1]);
        Assert.Equal(["1", "3"], Ids(set));
        await view.ClearFiltersAsync();
        Assert.Equal(5, view.RowCount);
    }

    private static QueryResultSet Cards()
    {
        var set = new QueryResultSet(
            "r",
            [
                new ResultColumn("Id", "int", typeof(int)), new ResultColumn("City", "nvarchar", typeof(string)),
                new ResultColumn("Balance", "decimal", typeof(decimal)), new ResultColumn("IssuedAt", "date", typeof(DateTime)),
            ]);
        set.AddRows(
        [
            [1, "İstanbul", 1250.50m, new DateTime(2026, 1, 15)],
            [2, "İzmir", 0m, new DateTime(2026, 2, 3)],
            [3, "istanbul", 9000m, new DateTime(2026, 1, 1)],
            [4, "izmir", 1250.50m, new DateTime(2026, 3, 1)],
            [5, "Ankara", null, null],
        ]);
        return set;
    }

    private static List<string> Ids(QueryResultSet set) =>
        [.. Enumerable.Range(0, set.View.RowCount).Select(i => set.GetText(set.View.SourceRow(i), 0)!)];
}
