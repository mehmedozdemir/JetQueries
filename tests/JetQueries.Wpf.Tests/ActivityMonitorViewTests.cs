using System.Globalization;
using System.IO;
using System.Windows;
using JetQueries.Core.Connections;
using JetQueries.Presentation.ViewModels;
using JetQueries.Wpf.Views;
using UiKit.Theming;

namespace JetQueries.Wpf.Tests;

/// <summary>The activity monitor tab rendered in both themes: counters, the sessions, the blocking chain, the chosen session.</summary>
[Collection(WpfUi.Name)]
public sealed class ActivityMonitorViewTests
{
    [Fact]
    public async Task The_monitor_shows_sessions_and_the_blocking_chain()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            await using var env = new TestEnvironment();
            env.Provider.Activity = FakeActivity.Snapshot();
            var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar — test", environment: ConnectionEnvironment.Test));
            var session = env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
            await using var monitor = new ActivityMonitorViewModel(session, env.Jobs, env.Dialogs) { AutoRefresh = false };
            var view = new ActivityMonitorView { DataContext = monitor };
            var window = new Window { Content = view, Width = 1280, Height = 640, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.SetResourceReference(Window.BackgroundProperty, "Brush.Surface");
            window.SetResourceReference(Window.ForegroundProperty, "Brush.Text");
            window.Show();
            await EditorActionsTests.Idle();

            // Shown: it reads by itself.
            for (var i = 0; i < 50 && monitor.UpdatedText.Length == 0; i++)
            {
                await EditorActionsTests.Idle();
            }

            Assert.True(monitor.IsShown);
            Assert.Equal(5, view.Grid.Source.RowCount);
            Assert.Equal("52", view.Grid.Source.GetText(0, 0));
            Assert.Equal(Visibility.Visible, ((FrameworkElement)view.FindName("ChainsCard")).Visibility);
            monitor.Selected = monitor.Rows[1];
            await EditorActionsTests.Idle();
            Assert.Equal(Visibility.Visible, ((FrameworkElement)view.FindName("SessionPanel")).Visibility);

            var results = new List<int>();
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme, save: false);
                await EditorActionsTests.Idle();
                results.Add(ResultGridViewTests.Capture(window, Path.Combine(directory, $"wpf-activity-{(theme == AppTheme.Dark ? "dark" : "light")}.png")));
            }

            ThemeManager.Apply(AppTheme.Dark, save: false);
            window.Close();
            Assert.False(monitor.IsShown);
            return results.Min();
        });

        Assert.True(colours > 10, $"rendered only {colours} distinct colours.");
    }
}
