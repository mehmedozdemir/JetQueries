using System.Globalization;
using System.IO;
using JetQueries.Presentation.ViewModels;
using JetQueries.Wpf.Views;

namespace JetQueries.Wpf.Tests;

/// <summary>The Export dialog of a result, rendered.</summary>
[Collection(WpfUi.Name)]
public sealed class ExportResultWindowTests
{
    [Fact]
    public async Task The_export_dialog_shows_the_format_options_rows_and_file()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var export = new ExportResultViewModel(1250, canReadAll: true, "Kart hareketleri", @"C:\Users\kullanici\Documents");
            var window = new ExportResultWindow(export) { Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            await EditorActionsTests.Idle();
            var results = new Dictionary<string, int>
            {
                ["csv"] = ResultGridViewTests.Capture(window, Path.Combine(directory, "wpf-export-csv.png")),
            };
            export.SelectedFormat = export.Formats[3];
            await EditorActionsTests.Idle();
            results["sql"] = ResultGridViewTests.Capture(window, Path.Combine(directory, "wpf-export-sql.png"));
            window.Close();
            return results;
        });

        Assert.All(colours, static pair => Assert.True(pair.Value > 12, $"{pair.Key} rendered only {pair.Value} distinct colours."));
    }
}
