using System.Globalization;
using System.IO;
using System.Windows;
using JetQueries.Presentation.ViewModels;
using JetQueries.Wpf.Views;
using UiKit.Theming;

namespace JetQueries.Wpf.Tests;

/// <summary>The database detail tab rendered in both themes.</summary>
[Collection(WpfUi.Name)]
public sealed class DatabaseDetailViewTests
{
    [Fact]
    public async Task Tiles_tables_files_and_backups_render()
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
            await using var detail = new DatabaseDetailViewModel(session, env.Jobs, "Cards");
            detail.Show(FakeDatabaseDetail.Of("Cards"), FakeDatabaseDetail.Now);
            var view = new DatabaseDetailView { DataContext = detail };
            var window = new Window { Content = view, Width = 1280, Height = 1100, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.SetResourceReference(Window.BackgroundProperty, "Brush.Surface");
            window.SetResourceReference(Window.ForegroundProperty, "Brush.Text");
            window.Show();
            await EditorActionsTests.Idle();
            Assert.Equal((3, 2, 2), (view.Grids.Tables.Source.RowCount, view.Grids.Files.Source.RowCount, view.Grids.Backups.Source.RowCount));
            Assert.Equal(11, DbaHelpViewTests.Descendants<InfoButton>(view).Count());

            var results = new List<int>();
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme, save: false);
                await EditorActionsTests.Idle();
                results.Add(ResultGridViewTests.Capture(window, Path.Combine(directory, $"wpf-database-detail-{(theme == AppTheme.Dark ? "dark" : "light")}.png")));
            }

            ThemeManager.Apply(AppTheme.Dark, save: false);
            window.Close();
            return results.Min();
        });

        Assert.True(colours > 10, $"rendered only {colours} distinct colours.");
    }
}
