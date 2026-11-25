using System.Globalization;

namespace JetQueries.Presentation.Tests;

/// <summary>DBA tables sorted and filtered by what they show, units included.</summary>
public sealed class GridRowsViewTests
{
    private static readonly string?[][] Rows =
    [
        ["Kartlar", "1,2 sn", "12,5 MB", "1.250"],
        ["arşiv", "850 ms", "800 KB", "3"],
        ["İşlemler", "04:10", "1,5 GB", null],
        ["Sales", null, "2,0 MB", "40"],
    ];

    public GridRowsViewTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
    }

    private static GridRowsView View()
    {
        var view = new GridRowsView(["Ad", "Süre", "Boyut", "Satır"], [false, true, true, true]);
        view.SetRows(Rows.Length, static (r, c) => Rows[r][c]);
        return view;
    }

    // The shown cells of a column, "|"-separated, an empty cell as "∅".
    private static string Column(GridRowsView view, int column) => string.Join("|", Enumerable.Range(0, view.RowCount).Select(r => view.Cell(r, column) ?? "∅"));

    [Theory]
    [InlineData("850 ms", 0.85)]
    [InlineData("1,2 sn", 1.2)]
    [InlineData("04:10", 250d)]
    [InlineData("1:02:03", 3723d)]
    [InlineData("800 KB", 800 * 1024d)]
    [InlineData("1,5 GB", 1.5 * 1024 * 1024 * 1024)]
    [InlineData("85 %", 85d)]
    [InlineData("1.250", 1250d)]
    [InlineData("—", null)]
    [InlineData("abc", null)]
    public void Numbers_are_read_with_their_units(string text, double? expected)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
        var number = GridRowsView.NumberOf(text);
        if (expected is null)
        {
            Assert.Null(number);
        }
        else
        {
            Assert.Equal(expected.Value, number!.Value, 6);
        }
    }

    [Fact]
    public void Columns_sort_by_number_and_unit_and_text_by_culture()
    {
        var view = View();
        view.ToggleSort(1, additive: false);
        Assert.Equal("∅|850 ms|1,2 sn|04:10", Column(view, 1));
        view.ToggleSort(1, additive: false);
        Assert.Equal("04:10|1,2 sn|850 ms|∅", Column(view, 1));
        view.ToggleSort(1, additive: false);
        Assert.Empty(view.Sorts);

        view.SetSort(2, descending: false);
        Assert.Equal("800 KB|2,0 MB|12,5 MB|1,5 GB", Column(view, 2));
        view.SetSort(0, descending: false);
        Assert.Equal("arşiv|İşlemler|Kartlar|Sales", Column(view, 0));
        Assert.Equal(2, view.SourceRow(1));
        Assert.Equal(1, view.ShownRow(2));
    }

    [Fact]
    public void Filters_narrow_the_rows_and_stay_on_new_rows()
    {
        var view = View();
        view.SetFilter(2, new GridTextFilter(2, GridTextFilterOperator.Greater, "1 MB"));
        Assert.Equal("Kartlar|İşlemler|Sales", Column(view, 0));
        view.SetFilter(0, new GridTextFilter(0, GridTextFilterOperator.Contains, "işlem"));
        Assert.Equal("İşlemler", Column(view, 0));
        Assert.Equal([0, 2], view.FilteredColumns.Order());

        view.SetRows(2, static (r, c) => Rows[r][c]);
        Assert.Equal(0, view.RowCount);
        Assert.Equal(2, view.TotalCount);
        view.ClearFilters();
        Assert.Equal(2, view.RowCount);

        view.FilterByValue(0, 3, exclude: false);
        Assert.Equal("Kartlar", Column(view, 0));
        view.SetRows(Rows.Length, static (r, c) => Rows[r][c]);
        view.SetFilter(3, null);
        view.SetFilter(3, new GridTextFilter(3, GridTextFilterOperator.IsEmpty));
        Assert.Equal("İşlemler", Column(view, 0));
    }

    [Fact]
    public void An_export_has_plain_numbers_as_numbers_and_units_as_shown()
    {
        var view = View();
        view.SetSort(0, descending: true);
        var rows = view.ExportRows().ToList();
        Assert.Equal("Sales", rows[0][0]);
        Assert.Equal(40m, rows[0][3]);
        Assert.Equal("2,0 MB", rows[0][2]);
        Assert.Equal(1250m, rows[1][3]);
    }
}
