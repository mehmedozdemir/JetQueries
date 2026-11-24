using System.Globalization;
using System.IO;
using System.Windows;
using JetQueries.Wpf.Views;
using UiKit.Theming;

namespace JetQueries.Wpf.Tests;

/// <summary>The Import connections window, rendered in both themes.</summary>
[Collection(WpfUi.Name)]
public sealed class ImportWindowTests
{
    private const string DBeaver = """
        { "connections": {
            "a": { "provider": "sqlserver", "name": "Kartlar canlı", "folder": "Müşteri A/Canlı",
                   "configuration": { "host": "10.0.0.5", "port": "1433", "database": "Cards", "type": "prod", "user": "app_reader" } },
            "b": { "provider": "sqlserver", "name": "Kartlar test", "folder": "Müşteri A/Test",
                   "configuration": { "host": "10.0.0.6", "port": "1433", "database": "Cards", "type": "test", "user": "app" } },
            "c": { "provider": "postgresql", "name": "Raporlar", "configuration": { "host": "pg", "port": "5432", "database": "reports" } } } }
        """;

    [Fact]
    public async Task The_import_window_lists_what_it_found()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(Directory.CreateTempSubdirectory("jq-import").FullName, "data-sources.json");
        await File.WriteAllTextAsync(file, DBeaver, TestContext.Current.CancellationToken);

        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            await using var env = new TestEnvironment();
            var viewModel = new ImportConnectionsViewModel([new JetQueries.Provider.SqlServer.SqlServerProvider()], env.Store, env.Jobs);
            // Never this computer's own DBeaver / SSMS settings in a test.
            var window = new ImportConnectionsWindow(viewModel, discover: false) { Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.Show();

            // Step one: the sources, from a stand-in discovery (never this computer's own tools).
            var sources = new ImportConnectionsViewModel([new JetQueries.Provider.SqlServer.SqlServerProvider()], env.Store, env.Jobs, () => [("DBeaver", file)]);
            var first = new ImportConnectionsWindow(sources, discover: false) { Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            first.Show();
            await sources.DiscoverAsync();
            await EditorActionsTests.Idle();
            ResultGridViewTests.Capture(first, Path.Combine(directory, "wpf-import-sources.png"));
            first.Close();

            await viewModel.AddFileAsync(file, "DBeaver");
            Assert.Equal(2, viewModel.SelectedCount);
            var results = new Dictionary<string, int>();
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme, save: false);
                await EditorActionsTests.Idle();
                results[theme.ToString()] = ResultGridViewTests.Capture(window, Path.Combine(directory, $"wpf-import-{(theme == AppTheme.Dark ? "dark" : "light")}.png"));
            }

            ThemeManager.Apply(AppTheme.Dark, save: false);
            window.Close();
            return results;
        });

        Assert.All(colours, static pair => Assert.True(pair.Value > 12, $"{pair.Key} rendered only {pair.Value} distinct colours."));
    }
}
