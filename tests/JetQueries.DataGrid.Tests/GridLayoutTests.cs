namespace JetQueries.DataGrid.Tests;

public sealed class GridLayoutTests
{
    private static ColumnLayout Columns(params double[] widths)
    {
        var layout = new ColumnLayout();
        layout.Reset(widths);
        return layout;
    }

    [Fact]
    public void Edges_are_prefix_sums_of_the_widths()
    {
        var layout = Columns(100, 50, 200);

        Assert.Equal(3, layout.Count);
        Assert.Equal(350, layout.TotalWidth);
        Assert.Equal(100, layout.Left(1));
        Assert.Equal(150, layout.Right(1));
        Assert.Equal(50, layout.Width(1));
    }

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(0, 0)]
    [InlineData(99.9, 0)]
    [InlineData(100, 1)]
    [InlineData(149.9, 1)]
    [InlineData(150, 2)]
    [InlineData(349.9, 2)]
    [InlineData(350, -1)]
    public void ColumnAt_maps_a_coordinate_to_its_column(double x, int expected)
    {
        Assert.Equal(expected, Columns(100, 50, 200).ColumnAt(x));
    }

    [Theory]
    [InlineData(0, 120, 0, 1)]
    [InlineData(0, 100, 0, 0)]
    [InlineData(99, 2, 0, 1)]
    [InlineData(120, 1000, 1, 2)]
    [InlineData(350, 100, 0, -1)]
    public void VisibleRange_returns_only_columns_intersecting_the_viewport(double offset, double width, int first, int last)
    {
        Assert.Equal((first, last), Columns(100, 50, 200).VisibleRange(offset, width));
    }

    [Fact]
    public void VisibleRange_is_empty_without_columns()
    {
        var (first, last) = Columns().VisibleRange(0, 500);

        Assert.True(last < first);
    }

    [Fact]
    public void SetWidth_shifts_later_columns_and_enforces_the_minimum()
    {
        var layout = Columns(100, 50, 200);

        layout.SetWidth(0, 130);
        Assert.Equal(130, layout.Left(1));
        Assert.Equal(380, layout.TotalWidth);

        layout.SetWidth(1, 1);
        Assert.Equal(ColumnLayout.MinColumnWidth, layout.Width(1));
    }

    [Theory]
    [InlineData(98, 0)]
    [InlineData(103, 0)]
    [InlineData(150, 1)]
    [InlineData(352, 2)]
    [InlineData(50, -1)]
    [InlineData(2, -1)]
    public void ResizeHandleAt_finds_the_column_whose_right_edge_is_near(double x, int expected)
    {
        Assert.Equal(expected, Columns(100, 50, 200).ResizeHandleAt(x, 4));
    }

    [Theory]
    [InlineData(0, 220, 0, 9)]
    [InlineData(11, 220, 0, 10)]
    [InlineData(22, 220, 1, 10)]
    [InlineData(21_999_780, 220, 999_990, 999_999)]
    public void Visible_rows_cover_exactly_the_viewport(double offset, double height, int first, int last)
    {
        Assert.Equal((first, last), RowLayout.VisibleRange(offset, height, 22, 1_000_000));
    }

    [Fact]
    public void Visible_rows_are_empty_without_rows_and_clamped_to_the_last_row()
    {
        var (first, last) = RowLayout.VisibleRange(0, 220, 22, 0);
        Assert.True(last < first);

        Assert.Equal((0, 2), RowLayout.VisibleRange(0, 220, 22, 3));
    }
}
