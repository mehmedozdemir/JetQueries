using System.Globalization;
using System.IO;
using System.Text;
using JetQueries.Presentation.ViewModels;
using JetQueries.Sdk;
using JetQueries.Wpf.Views;

namespace JetQueries.Wpf.Tests;

/// <summary>The Import data dialog, rendered with a file read and its columns mapped.</summary>
[Collection(WpfUi.Name)]
public sealed class ImportDataWindowTests
{
    [Fact]
    public async Task The_dialog_shows_the_file_its_first_rows_and_the_mapping()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(AppContext.BaseDirectory, "import-render.csv");
        await File.WriteAllTextAsync(file, "Id;CardNo;Balance\r\n1;5200 0001;1250,50\r\n2;5200 0002;0\r\n3;Müşteri İşlemleri;12\r\n", Encoding.UTF8, TestContext.Current.CancellationToken);
        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            await using var env = new TestEnvironment();
            var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar"));
            var session = env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
            var import = new ImportDataViewModel(session, new DatabaseObject(new DatabaseObjectName("Cards", "dbo", "Cards"), DatabaseObjectKinds.Table), env.Metadata, env.Jobs);
            await import.LoadAsync();
            import.FilePath = file;
            await import.PreviewAsync();
            var window = new ImportDataWindow(import) { Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            await EditorActionsTests.Idle();
            Assert.Equal(["Id", "CardNo", "Balance"], window.PreviewColumns);
            var result = ResultGridViewTests.Capture(window, Path.Combine(directory, "wpf-import-data.png"));
            window.Close();
            return result;
        });

        Assert.True(colours > 12, $"rendered only {colours} distinct colours.");
    }
}
