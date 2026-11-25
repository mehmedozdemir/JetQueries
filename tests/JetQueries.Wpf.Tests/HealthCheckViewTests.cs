using System.Globalization;
using System.IO;
using System.Windows;
using JetQueries.Presentation.ViewModels;
using JetQueries.Wpf.Views;
using UiKit.Theming;

namespace JetQueries.Wpf.Tests;

/// <summary>The health check tab rendered in both themes, with a finding chosen.</summary>
[Collection(WpfUi.Name)]
public sealed class HealthCheckViewTests
{
    [Fact]
    public async Task Findings_and_the_chosen_one_render()
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
            await using var health = new HealthCheckViewModel(session, env.Jobs);
            await health.RunAsync();
            health.Selected = health.Rows.Single(static r => r.Finding.RuleId == "CostThreshold");
            var view = new HealthCheckView { DataContext = health };
            var window = new Window { Content = view, Width = 1280, Height = 700, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.SetResourceReference(Window.BackgroundProperty, "Brush.Surface");
            window.SetResourceReference(Window.ForegroundProperty, "Brush.Text");
            window.Show();
            await EditorActionsTests.Idle();
            Assert.Equal(5, view.List.Items.Count);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)view.FindName("DetailPanel")).Visibility);
            Assert.Single(DbaHelpViewTests.Descendants<InfoButton>(view));

            var results = new List<int>();
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme, save: false);
                await EditorActionsTests.Idle();
                results.Add(ResultGridViewTests.Capture(window, Path.Combine(directory, $"wpf-health-{(theme == AppTheme.Dark ? "dark" : "light")}.png")));
            }

            ThemeManager.Apply(AppTheme.Dark, save: false);
            window.Close();
            return results.Min();
        });

        Assert.True(colours > 10, $"rendered only {colours} distinct colours.");
    }
}
