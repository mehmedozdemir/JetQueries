using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JetQueries.Sdk;
using JetQueries.Wpf.Views;
using UiKit.Controls;
using UiKit.Theming;

namespace JetQueries.Wpf.Tests;

[Collection(WpfUi.Name)]
public sealed class ResultGridViewTests
{
    [Fact]
    public async Task Sorting_filtering_and_hiding_reach_the_grid_and_the_chips()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);

        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            App.LocalizeGrid();
            var results = new Dictionary<string, int>();

            string[] cities = ["İstanbul", "Ankara", "İzmir", "Bursa", "Antalya"];
            var set = new QueryResultSet(
                "Sonuç 1",
                [
                    new ResultColumn("Id", "bigint", typeof(long)), new ResultColumn("City", "nvarchar(40)", typeof(string)),
                    new ResultColumn("Balance", "decimal(12,2)", typeof(decimal)), new ResultColumn("IssuedAt", "date", typeof(DateTime)),
                ],
                null,
                null,
                new ImmediateDispatcher());
            set.AddRows([.. Enumerable.Range(1, 60).Select(i => new object?[] { (long)i, cities[i % cities.Length], i % 7 == 0 ? null : i * 37.5m, new DateTime(2026, 1, 1).AddDays(i) })]);

            var view = new ResultGridView { DataContext = set };
            var window = new Window
            {
                Content = new Border { Style = (Style)Application.Current.FindResource("Card.Surface"), Child = view },
                Width = 900,
                Height = 420,
                Left = -20000,
                ShowActivated = false,
                ShowInTaskbar = false,
                Style = (Style)Application.Current.FindResource("Window.App"),
            };
            window.Show();
            await Idle();

            // What the grid's events would do: sort by balance descending, filter the city, hide a column.
            await set.View.SetSortAsync(2, descending: true);
            await set.View.SetFilterAsync(1, new ResultFilter(1, ResultFilterOperator.Contains, "an"));
            view.HideColumn(3);
            await Idle();

            var grid = view.VirtualGrid;
            Assert.Equal([new GridSort(2, GridSortDirection.Descending)], grid.Sorts);
            Assert.Equal([1], grid.FilteredColumns!);
            Assert.Equal([0, 1, 2], grid.VisibleColumns!);
            Assert.Equal(set.View.RowCount, grid.Source.RowCount);
            Assert.All(Enumerable.Range(0, grid.Source.RowCount), r => Assert.Contains("an", grid.Source.GetText(r, 1), StringComparison.OrdinalIgnoreCase));
            Assert.True(decimal.Parse(grid.Source.GetText(0, 2), CultureInfo.InvariantCulture) >= decimal.Parse(grid.Source.GetText(1, 2), CultureInfo.InvariantCulture));

            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme, save: false);
                var suffix = theme == AppTheme.Dark ? "dark" : "light";
                view.OpenFilter(1);
                await Idle();
                results[$"grid-{suffix}"] = Capture((FrameworkElement)window.Content, Path.Combine(directory, $"wpf-results-filtered-{suffix}.png"));
                var popup = (Popup)view.FindName("FilterPopup");
                Assert.True(popup.IsOpen);
                results[$"filter-{suffix}"] = Capture((FrameworkElement)popup.Child, Path.Combine(directory, $"wpf-filter-panel-{suffix}.png"));
                popup.IsOpen = false;
            }

            // Clearing the filters keeps the sort and shows every row again.
            await set.View.ClearFiltersAsync();
            await Idle();
            Assert.Equal(60, grid.Source.RowCount);
            Assert.Equal([new GridSort(2, GridSortDirection.Descending)], grid.Sorts);
            Assert.Empty(grid.FilteredColumns!);

            window.Close();
            ThemeManager.Apply(AppTheme.Dark, save: false);
            return results;
        });

        Assert.Equal(4, colours.Count);
        Assert.All(colours, static pair => Assert.True(pair.Value > 12, $"{pair.Key} rendered only {pair.Value} distinct colours."));
    }

    [Fact]
    public async Task Text_and_record_views_show_the_rows_as_the_grid_orders_them()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);

        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var results = new Dictionary<string, int>();
            string[] cities = ["İstanbul", "Ankara", "İzmir", "Bursa", "Antalya"];
            var set = new QueryResultSet(
                "Sonuç 1",
                [
                    new ResultColumn("Id", "bigint", typeof(long)), new ResultColumn("City", "nvarchar(40)", typeof(string)),
                    new ResultColumn("Balance", "decimal(12,2)", typeof(decimal)), new ResultColumn("IssuedAt", "date", typeof(DateTime)),
                    new ResultColumn("Note", "nvarchar(max)", typeof(string)),
                ],
                null,
                null,
                new ImmediateDispatcher());
            set.AddRows([.. Enumerable.Range(1, 40).Select(i => new object?[]
            {
                (long)i, cities[i % cities.Length], i % 7 == 0 ? null : i * 37.5m, new DateTime(2026, 1, 1).AddDays(i),
                i % 3 == 0 ? "Teslimat adresi güncellendi, müşteri bilgilendirildi" : null,
            })]);
            await set.View.SetSortAsync(2, descending: true);
            set.CanFilter = true;
            set.SqlFilter = set.AppliedFilter = "Balance > 100 AND City LIKE N'İ%'";

            var view = new ResultGridView { DataContext = set };
            var window = new Window
            {
                Content = new Border { Style = (Style)Application.Current.FindResource("Card.Surface"), Child = view },
                Width = 900,
                Height = 440,
                Left = -20000,
                ShowActivated = false,
                ShowInTaskbar = false,
                Style = (Style)Application.Current.FindResource("Window.App"),
            };
            window.Show();
            await Idle();

            // The SQL filter box is a one-line SQL editor; OR between two column filters.
            Assert.Equal("Balance > 100 AND City LIKE N'İ%'", view.SqlFilterBox.Text);
            Assert.True(view.SqlFilterBox.IsSingleLine);
            await set.View.FilterByValueAsync(0, 1, exclude: false);
            await set.View.SetFilterAsync(2, new ResultFilter(2, ResultFilterOperator.GreaterThan, "1000"));
            await set.View.SetMatchAnyAsync(true);
            await Idle();
            results["filters"] = Capture((FrameworkElement)window.Content, Path.Combine(directory, "wpf-results-filters.png"));
            await set.View.ClearFiltersAsync();
            await set.View.SetMatchAnyAsync(false);
            set.SqlFilter = string.Empty;
            await Idle();
            Assert.Equal(view.SqlFilterBox.PlaceholderShown, JetQueries.Presentation.Resources.Strings.FilterPlaceholder);
            await Idle();

            view.VirtualGrid.SelectRow(4);
            set.ViewMode = ResultViewMode.Text;
            await Idle();
            var lines = view.ShownText.Split('\n');
            Assert.StartsWith("Id | City", lines[0], StringComparison.Ordinal);
            Assert.StartsWith("40 | İstanbul", lines[2], StringComparison.Ordinal);
            Assert.Contains("[NULL]", view.ShownText, StringComparison.Ordinal);

            // The record view starts on the grid's current row, in the grid's order.
            set.ViewMode = ResultViewMode.Record;
            await Idle();
            Assert.Equal(4, set.RecordIndex);
            Assert.Equal("Kayıt 5 / 40", set.RecordPosition);

            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme, save: false);
                var suffix = theme == AppTheme.Dark ? "dark" : "light";
                set.ViewMode = ResultViewMode.Text;
                await Idle();
                results[$"text-{suffix}"] = Capture((FrameworkElement)window.Content, Path.Combine(directory, $"wpf-results-text-{suffix}.png"));
                set.ViewMode = ResultViewMode.Record;
                await Idle();
                results[$"record-{suffix}"] = Capture((FrameworkElement)window.Content, Path.Combine(directory, $"wpf-results-record-{suffix}.png"));
            }

            // Back in the grid on the record's row.
            set.NextRecordCommand.Execute(null);
            set.ViewMode = ResultViewMode.Grid;
            await Idle();
            Assert.Equal(5, view.VirtualGrid.CurrentRow);

            window.Close();
            ThemeManager.Apply(AppTheme.Dark, save: false);
            return results;
        });

        Assert.Equal(5, colours.Count);
        Assert.All(colours, static pair => Assert.True(pair.Value > 12, $"{pair.Key} rendered only {pair.Value} distinct colours."));
    }

    private static async Task Idle()
    {
        for (var i = 0; i < 3; i++)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(60);
        }
    }

    internal static int Capture(FrameworkElement content, string path)
    {
        content.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(content);
        var bitmap = new RenderTargetBitmap(
            (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            context.DrawRectangle((Brush)Application.Current.FindResource("Brush.Window"), null, bounds);
            // One to one: a brush would otherwise stretch what was drawn (not the layout box) over the bounds.
            context.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds }, null, bounds);
        }

        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        var pixels = new uint[bitmap.PixelWidth * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels.Distinct().Count();
    }
}
