using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using JetQueries.Core.Connections;
using JetQueries.Presentation.ViewModels;
using JetQueries.Wpf.Services;
using JetQueries.Wpf.ViewModels;
using JetQueries.Wpf.Views;
using UiKit.Theming;

namespace JetQueries.Wpf.Tests;

/// <summary>Tabs in their connection's environment colour, joined to the query's frame; a pinned tab shows its pin.</summary>
[Collection(WpfUi.Name)]
public sealed class DocumentTabsViewTests
{
    [Fact]
    public async Task Tabs_take_the_environment_colour_and_a_pinned_tab_shows_its_pin()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            await using var env = new TestEnvironment();
            var production = await env.OpenAsync(TestEnvironment.Definition("Kartlar — üretim", environment: ConnectionEnvironment.Production));
            var test = await env.OpenAsync(TestEnvironment.Definition("Kartlar — test", environment: ConnectionEnvironment.Test));
            var ui = new WpfUiDispatcher();
            var documents = new DocumentsViewModel(env.Manager, env.Metadata, env.History, env.Jobs, env.Dialogs, ui);
            var first = documents.Selected!;
            var onTest = documents.Open("Rapor", "SELECT 1", test.Id);
            var onProduction = documents.Open("Düzeltme", "UPDATE Cards SET Balance = 0 WHERE Id = 1", production.Id);
            documents.Pin(onTest, true);
            documents.SelectedTab = onProduction;
            var shell = new MainWindowViewModel(new EditorActionsTests.NoTheme(), ui, env.Connections(), documents, new HistoryViewModel(env.History, env.Jobs, env.Dialogs, ui, documents));
            var window = new MainWindow(shell) { Width = 1100, Height = 560, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            for (var i = 0; i < 50 && onProduction.Environment is null; i++)
            {
                await EditorActionsTests.Idle();
            }

            await EditorActionsTests.Idle();
            Assert.Equal([onTest, first, onProduction], documents.Tabs);
            var pins = EditorActionsTests.All<Button>(window).Where(static b => b.Name == "PinButton" && b.IsVisible).ToList();
            Assert.Same(onTest, Assert.Single(pins).DataContext);

            var result = 0;
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeManager.Apply(theme, save: false);
                await EditorActionsTests.Idle();
                result = Math.Max(result, ResultGridViewTests.Capture(window, Path.Combine(directory, $"wpf-tabs-environment-{theme.ToString().ToLowerInvariant()}.png")));
            }

            ThemeManager.Apply(AppTheme.Light, save: false);
            window.Close();
            return result;
        });

        Assert.True(colours > 20, $"rendered only {colours} distinct colours.");
    }
}
