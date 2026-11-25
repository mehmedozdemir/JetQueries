using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using JetQueries.Presentation.ViewModels;
using JetQueries.Wpf.Views;
using UiKit.Theming;

namespace JetQueries.Wpf.Tests;

/// <summary>The "i" of the DBA screens: its card renders, every screen has them, the setting hides them.</summary>
[Collection(WpfUi.Name)]
public sealed class DbaHelpViewTests
{
    [Fact]
    public async Task The_card_explains_and_the_buttons_follow_the_setting()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var results = new List<int>();
            foreach (var (topic, theme) in new[] { (DbaHelp.DashPle, AppTheme.Dark), (DbaHelp.PerfUsage, AppTheme.Light) })
            {
                ThemeManager.Apply(theme, save: false);
                var card = InfoButton.Card(DbaHelp.Get(topic)!, static () => { });
                var host = new Window { Content = new Border { Child = card, Padding = new Thickness(16) }, SizeToContent = SizeToContent.WidthAndHeight, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
                host.SetResourceReference(Window.BackgroundProperty, "Brush.Surface");
                host.Show();
                await EditorActionsTests.Idle();
                results.Add(ResultGridViewTests.Capture(host, Path.Combine(directory, $"wpf-dba-help-{topic}-{(theme == AppTheme.Dark ? "dark" : "light")}.png")));
                host.Close();
            }

            ThemeManager.Apply(AppTheme.Dark, save: false);

            // Every DBA screen carries its explanations, each with a known topic.
            await using var env = new TestEnvironment();
            var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar"));
            var session = env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
            await using var dashboard = new DashboardViewModel(session, env.Jobs) { AutoRefresh = false };
            await using var monitor = new ActivityMonitorViewModel(session, env.Jobs, env.Dialogs) { AutoRefresh = false };
            await using var analysis = new PerformanceViewModel(session, env.Jobs);
            var counts = new List<int>();
            foreach (var view in new FrameworkElement[] { new DashboardView { DataContext = dashboard }, new ActivityMonitorView { DataContext = monitor }, new PerformanceView { DataContext = analysis } })
            {
                var window = new Window { Content = view, Width = 1280, Height = 900, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
                window.Show();
                await EditorActionsTests.Idle();
                var buttons = Descendants<InfoButton>(view).ToList();
                Assert.All(buttons, static b => Assert.NotNull(DbaHelp.Get(b.Topic!)));
                counts.Add(buttons.Count);
                if (view is DashboardView)
                {
                    Assert.All(buttons, static b => Assert.Equal(Visibility.Visible, b.Visibility));
                    new Services.AppearanceService().Apply(new AppearanceSettings { DbaHelp = false });
                    await EditorActionsTests.Idle();
                    Assert.All(buttons, static b => Assert.Equal(Visibility.Collapsed, b.Visibility));
                    new Services.AppearanceService().Apply(new AppearanceSettings());
                    await EditorActionsTests.Idle();
                    Assert.All(buttons, static b => Assert.Equal(Visibility.Visible, b.Visibility));

                    buttons[0].Show();
                    await EditorActionsTests.Idle();
                    Assert.NotNull(buttons[0].ShownCard);
                }

                window.Close();
            }

            Assert.Equal([12, 4, 1], counts);
            return results.Min();
        });

        Assert.True(colours > 8, $"rendered only {colours} distinct colours.");
    }

    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var inner in Descendants<T>(child))
            {
                yield return inner;
            }
        }
    }
}
