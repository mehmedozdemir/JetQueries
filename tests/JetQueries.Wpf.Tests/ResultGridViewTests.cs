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

    private static async Task Idle()
    {
        for (var i = 0; i < 3; i++)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(60);
        }
    }

    private static int Capture(FrameworkElement content, string path)
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
            context.DrawRectangle(new VisualBrush(content), null, bounds);
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
