using System.Globalization;
using System.IO;
using System.Windows;
using JetQueries.Core.Library;
using JetQueries.Presentation.ViewModels;
using JetQueries.Wpf.Views;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Wpf.Tests;

/// <summary>The parameters dialog, the Save to library dialog and the saved queries window, rendered.</summary>
[Collection(WpfUi.Name)]
public sealed class SavedQueriesWindowTests
{
    [Fact]
    public async Task The_parameter_save_and_library_windows_render()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(AppContext.BaseDirectory, "saved-queries-render", Guid.NewGuid().ToString("N"), "saved-queries.json");
        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            var results = new List<int>();

            var parameters = new QueryParametersViewModel([new QueryParameterItem("table", isText: true) { Value = "dbo.Cards" }, new QueryParameterItem("city", isText: false) { Value = "İstanbul" }, new QueryParameterItem("since", isText: false) { Kind = QueryParameterKind.Date, Value = "01.01.2026" }]);
            var parametersWindow = new QueryParametersWindow(parameters) { Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            parametersWindow.Show();
            await EditorActionsTests.Idle();
            results.Add(ResultGridViewTests.Capture(parametersWindow, Path.Combine(directory, "wpf-query-parameters.png")));
            parametersWindow.Close();

            var edit = new SavedQueryEditViewModel("Aylık kart raporu", "Raporlar", "Şehre göre kartlar", ["Raporlar", "Kontroller"], "Kartlar", keepConnection: true);
            var saveWindow = new SaveQueryWindow(edit) { Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            saveWindow.Show();
            await EditorActionsTests.Idle();
            results.Add(ResultGridViewTests.Capture(saveWindow, Path.Combine(directory, "wpf-save-query.png")));
            saveWindow.Close();

            await using var env = new TestEnvironment();
            var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar"));
            await env.Store.SaveAsync(definition, CancellationToken.None);
            using var store = new JsonSavedQueryStore(new SavedQueryStoreOptions { FilePath = path }, NullLogger<JsonSavedQueryStore>.Instance);
            var now = DateTimeOffset.UtcNow;
            await store.SaveAsync(new SavedQuery { Id = Guid.NewGuid(), Name = "Aylık kart raporu", Folder = "Raporlar", Sql = "SELECT * FROM Cards WHERE City = :city", ConnectionId = definition.Id, CreatedAt = now, UpdatedAt = now }, CancellationToken.None);
            await store.SaveAsync(new SavedQuery { Id = Guid.NewGuid(), Name = "Bakiye kontrolü", Folder = "Kontroller", Sql = "SELECT SUM(Balance) FROM Cards", CreatedAt = now, UpdatedAt = now }, CancellationToken.None);
            var library = new SavedQueriesViewModel(store, env.Store, env.Jobs, env.Dialogs, env.Documents());
            await library.LoadAsync();
            var libraryWindow = new SavedQueriesWindow(library) { Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            libraryWindow.Show();
            await EditorActionsTests.Idle();
            Assert.Equal("SELECT SUM(Balance) FROM Cards", libraryWindow.PreviewText);
            results.Add(ResultGridViewTests.Capture(libraryWindow, Path.Combine(directory, "wpf-saved-queries.png")));
            libraryWindow.Close();
            return results.Min();
        });

        Assert.True(colours > 8, $"rendered only {colours} distinct colours.");
    }
}
