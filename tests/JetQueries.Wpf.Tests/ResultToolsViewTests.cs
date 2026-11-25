using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using JetQueries.Sdk;
using JetQueries.Wpf.Views;
using UiKit.Theming;

namespace JetQueries.Wpf.Tests;

/// <summary>Totals of the selection, Copy as, and the value panel in the real result view.</summary>
[Collection(WpfUi.Name)]
public sealed class ResultToolsViewTests
{
    [Fact]
    public async Task Totals_copy_formats_and_the_value_panel_work_in_the_view()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);

        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var results = new Dictionary<string, int>();
            var set = new QueryResultSet(
                "Sonuç 1",
                [
                    new ResultColumn("Id", "int", typeof(int)), new ResultColumn("Tutar", "decimal(12,2)", typeof(decimal)),
                    new ResultColumn("Detay", "nvarchar(max)", typeof(string)),
                ],
                null,
                null,
                new ImmediateDispatcher());
            set.AddRows(
            [
                [1, 10.5m, "{\"şehir\":\"İzmir\",\"etiketler\":[\"a\",\"b\"],\"limit\":5000}"],
                [2, 20m, "<kart no=\"5200\"><durum>aktif</durum></kart>"],
                [3, null, "düz metin"],
            ]);

            var view = new ResultGridView { DataContext = set };
            var window = new Window
            {
                Content = new Border { Style = (Style)Application.Current.FindResource("Card.Surface"), Child = view },
                Width = 1000,
                Height = 360,
                Left = -20000,
                ShowActivated = false,
                ShowInTaskbar = false,
                Style = (Style)Application.Current.FindResource("Window.App"),
            };
            window.Show();
            await EditorActionsTests.Idle();

            // The SQL filter bar waits behind its toolbar button; an applied filter keeps it open.
            Assert.False(view.SqlFilterShown);
            view.OpenSqlFilter();
            Assert.True(view.SqlFilterShown);
            var filtered = new QueryResultSet("Sonuç 2", [new ResultColumn("Id", "int", typeof(int))], null, null, new ImmediateDispatcher()) { AppliedFilter = "Id > 1" };
            view.DataContext = filtered;
            await EditorActionsTests.Idle();
            Assert.True(view.SqlFilterShown);
            filtered.AppliedFilter = null;
            Assert.False(view.SqlFilterShown);
            view.DataContext = set;
            await EditorActionsTests.Idle();

            // Totals: every cell selected; the numbers summed, NULLs and text counted only as cells.
            view.VirtualGrid.SelectAll();
            await EditorActionsTests.Idle();
            Assert.Equal("9 hücre · Toplam 36,5 · Ort. 7,3 · Min 1 · Maks 20", view.SelectionStatsShown);

            // Copy as: the selection in a format.
            Assert.StartsWith("| Id | Tutar | Detay |\n| ---: | ---: | --- |\n| 1 | 10.50 |", view.CopyAs(ResultCopyFormat.Markdown), StringComparison.Ordinal);
            Assert.StartsWith("(1, 10.50, '{", view.CopyAs(ResultCopyFormat.SqlInList), StringComparison.Ordinal);

            // The value panel follows the current cell (the last column after Select all); JSON is laid out.
            view.SetValuePanel(true);
            view.VirtualGrid.SelectRow(0);
            await EditorActionsTests.Idle();
            Assert.Equal("Detay", view.ValueShown.Column);
            Assert.StartsWith("{\n  \"şehir\": \"İzmir\",", view.ValueShown.Text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.Equal("nvarchar(max) · Json · 52 karakter", view.ValueShown.Info);

            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme, save: false);
                await EditorActionsTests.Idle();
                results[$"tools-{theme}"] = ResultGridViewTests.Capture((FrameworkElement)window.Content, Path.Combine(directory, $"wpf-result-tools-{(theme == AppTheme.Dark ? "dark" : "light")}.png"));
            }

            ThemeManager.Apply(AppTheme.Dark, save: false);
            window.Close();
            return results;
        });

        Assert.All(colours, static pair => Assert.True(pair.Value > 12, $"{pair.Key} rendered only {pair.Value} distinct colours."));
    }
}
