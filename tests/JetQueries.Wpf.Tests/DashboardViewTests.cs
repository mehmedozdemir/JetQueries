using System.Globalization;
using System.IO;
using System.Windows;
using JetQueries.Presentation.ViewModels;
using JetQueries.Wpf.Views;
using UiKit.Theming;

namespace JetQueries.Wpf.Tests;

/// <summary>The server dashboard rendered in both themes, with a minute of samples in its charts.</summary>
[Collection(WpfUi.Name)]
public sealed class DashboardViewTests
{
    [Fact]
    public async Task Tiles_charts_and_databases_render()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            await using var env = new TestEnvironment();
            var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar — üretim", environment: Core.Connections.ConnectionEnvironment.Production));
            var session = env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
            await using var dashboard = new DashboardViewModel(session, env.Jobs) { AutoRefresh = false };
            var view = new DashboardView { DataContext = dashboard };
            var window = new Window { Content = view, Width = 1280, Height = 1000, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.SetResourceReference(Window.BackgroundProperty, "Brush.Surface");
            window.SetResourceReference(Window.ForegroundProperty, "Brush.Text");
            window.Show();
            await EditorActionsTests.Idle();

            // Shown: it reads once by itself; a minute of samples more.
            Assert.True(dashboard.IsShown);
            while (env.Provider.SampleCount < 13)
            {
                await dashboard.RefreshAsync();
            }

            await EditorActionsTests.Idle();
            Assert.Equal(5, view.Grid.Source.RowCount);
            Assert.Equal(12, dashboard.BatchSeries[0].Points.Count);

            var results = new List<int>();
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme, save: false);
                await EditorActionsTests.Idle();
                results.Add(ResultGridViewTests.Capture(window, Path.Combine(directory, $"wpf-dashboard-{(theme == AppTheme.Dark ? "dark" : "light")}.png")));
            }

            ThemeManager.Apply(AppTheme.Dark, save: false);
            window.Close();
            Assert.False(dashboard.IsShown);
            return results.Min();
        });

        Assert.True(colours > 10, $"rendered only {colours} distinct colours.");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(37, 50)]
    [InlineData(100, 200)]
    [InlineData(1120, 2000)]
    [InlineData(9.5, 20)]
    [InlineData(0.42, 0.5)]
    public void Axis_tops_are_round_numbers_above_the_data(double value, double expected) => Assert.Equal(expected, LineChart.Nice(value), 6);
}
