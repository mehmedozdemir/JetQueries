using System.Globalization;
using System.IO;
using System.Windows;
using JetQueries.Presentation.ViewModels;
using JetQueries.Wpf.Views;
using UiKit.Theming;

namespace JetQueries.Wpf.Tests;

/// <summary>The performance analysis tab rendered: heavy queries (dark) and index use (light).</summary>
[Collection(WpfUi.Name)]
public sealed class PerformanceViewTests
{
    [Fact]
    public async Task Heavy_queries_and_index_findings_render()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            await using var env = new TestEnvironment();
            var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar — test"));
            var session = env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
            await using var analysis = new PerformanceViewModel(session, env.Jobs);
            var view = new PerformanceView { DataContext = analysis };
            var window = new Window { Content = view, Width = 1280, Height = 600, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.SetResourceReference(Window.BackgroundProperty, "Brush.Surface");
            window.SetResourceReference(Window.ForegroundProperty, "Brush.Text");
            window.Show();
            await analysis.StartAsync();
            await EditorActionsTests.Idle();
            Assert.Equal(2, view.Grid.Source.RowCount);

            var results = new List<int>();
            ThemeManager.Apply(AppTheme.Dark, save: false);
            await EditorActionsTests.Idle();
            results.Add(ResultGridViewTests.Capture(window, Path.Combine(directory, "wpf-performance-heavy-dark.png")));

            analysis.Databases.Add("Cards");
            analysis.Database = "Cards";
            analysis.Section = PerformanceSection.IndexUsage;
            for (var i = 0; i < 50 && (analysis.IsLoading || analysis.Table.Rows.Count == 0); i++)
            {
                await EditorActionsTests.Idle();
            }

            Assert.Equal(3, view.Grid.Source.RowCount);
            ThemeManager.Apply(AppTheme.Light, save: false);
            await EditorActionsTests.Idle();
            results.Add(ResultGridViewTests.Capture(window, Path.Combine(directory, "wpf-performance-usage-light.png")));
            ThemeManager.Apply(AppTheme.Dark, save: false);
            window.Close();
            return results.Min();
        });

        Assert.True(colours > 10, $"rendered only {colours} distinct colours.");
    }
}
