using System.Globalization;
using System.IO.Compression;
using System.Text;
using JetQueries.Presentation.ViewModels;

namespace JetQueries.Presentation.Tests;

/// <summary>Writing results to files.</summary>
public sealed class ResultExportTests
{
    private static readonly string[] Headers = ["Id", "Ad", "Tutar", "Tarih"];

    private static readonly object?[][] Rows =
    [
        [1L, "İstanbul; \"merkez\"", 1250.5m, new DateTime(2026, 1, 15)],
        [2L, null, -3m, new DateTime(2026, 2, 3, 14, 30, 5)],
    ];

    public ResultExportTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
    }

    private static async IAsyncEnumerable<object?[]> Async(IEnumerable<object?[]> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static async Task<(byte[] Bytes, long Count)> WriteAsync(ResultExportOptions options)
    {
        using var stream = new MemoryStream();
        var count = await ResultExporter.WriteAsync(stream, options, Headers, Async(Rows), null, TestContext.Current.CancellationToken);
        return (stream.ToArray(), count);
    }

    [Fact]
    public async Task Csv_has_a_bom_the_chosen_separator_and_quotes_where_needed()
    {
        var (bytes, count) = await WriteAsync(new ResultExportOptions { Format = ResultExportFormat.Csv, Delimiter = ";", NullText = "NULL" });

        Assert.Equal(2, count);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal(
            "Id;Ad;Tutar;Tarih\r\n1;\"İstanbul; \"\"merkez\"\"\";1250.5;2026-01-15\r\n2;NULL;-3;2026-02-03 14:30:05.000\r\n",
            Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public async Task Json_sql_markdown_and_html_are_written_row_by_row()
    {
        var json = Encoding.UTF8.GetString((await WriteAsync(new ResultExportOptions { Format = ResultExportFormat.Json })).Bytes);
        Assert.StartsWith("[\n  {\"Id\":1,\"Ad\":\"İstanbul; \\\"merkez\\\"\",\"Tutar\":1250.5", json, StringComparison.Ordinal);
        Assert.EndsWith("}\n]\n", json, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.Json.JsonDocument.Parse(json).RootElement.GetArrayLength());

        var sql = Encoding.UTF8.GetString((await WriteAsync(new ResultExportOptions { Format = ResultExportFormat.SqlInsert, TableName = "dbo.Cards" })).Bytes);
        Assert.StartsWith("INSERT INTO dbo.Cards (Id, Ad, Tutar, Tarih) VALUES (1, 'İstanbul; \"merkez\"', 1250.5, '2026-01-15');", sql, StringComparison.Ordinal);
        Assert.Contains("VALUES (2, NULL, -3,", sql, StringComparison.Ordinal);

        var markdown = Encoding.UTF8.GetString((await WriteAsync(new ResultExportOptions { Format = ResultExportFormat.Markdown })).Bytes);
        Assert.StartsWith("| Id | Ad | Tutar | Tarih |\r\n| --- | --- | --- | --- |\r\n| 1 |", markdown, StringComparison.Ordinal);

        var html = Encoding.UTF8.GetString((await WriteAsync(new ResultExportOptions { Format = ResultExportFormat.Html })).Bytes);
        Assert.Contains("<th>Ad</th>", html, StringComparison.Ordinal);
        Assert.Contains("<td>İstanbul; &quot;merkez&quot;</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td class=\"null\">NULL</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Excel_is_a_workbook_with_numbers_dates_and_text()
    {
        var (bytes, count) = await WriteAsync(new ResultExportOptions { Format = ResultExportFormat.Excel });
        Assert.Equal(2, count);

        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheet = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        var document = System.Xml.Linq.XDocument.Parse(sheet);
        System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = document.Descendants(ns + "row").ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("Ad", rows[0].Descendants(ns + "t").ElementAt(1).Value);
        Assert.Equal("1250.5", rows[1].Elements(ns + "c").ElementAt(2).Element(ns + "v")!.Value);
        Assert.Equal("2", rows[1].Elements(ns + "c").ElementAt(3).Attribute("s")!.Value); // the date style
        Assert.Equal(new DateTime(2026, 1, 15).ToOADate().ToString("R", CultureInfo.InvariantCulture), rows[1].Elements(ns + "c").ElementAt(3).Element(ns + "v")!.Value);
        Assert.Equal("İstanbul; \"merkez\"", rows[1].Descendants(ns + "t").Single().Value);
    }

    [Fact]
    public async Task Excel_takes_a_title_a_table_totals_and_widths()
    {
        var excel = new ExcelExportOptions
        {
            SheetName = "Kartlar: [2026]", Title = "Aylık rapor", AsTable = true, TableStyle = "TableStyleMedium9", Totals = true, TotalsLabel = "Toplam", Borders = true,
        };
        var (bytes, count) = await WriteAsync(new ResultExportOptions { Format = ResultExportFormat.Excel, Excel = excel });
        Assert.Equal(2, count);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        string Part(string name)
        {
            using var reader = new StreamReader(zip.GetEntry(name)!.Open());
            return reader.ReadToEnd();
        }

        System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sheet = System.Xml.Linq.XDocument.Parse(Part("xl/worksheets/sheet1.xml"));
        var rows = sheet.Descendants(ns + "row").ToList();
        Assert.Equal(["1", "3", "4", "5", "6"], rows.Select(static r => r.Attribute("r")!.Value));
        Assert.Equal("Aylık rapor", rows[0].Descendants(ns + "t").Single().Value);
        Assert.Equal("A4", sheet.Descendants(ns + "pane").Single().Attribute("topLeftCell")!.Value);
        Assert.Equal(["SUBTOTAL(109,A4:A5)", "SUBTOTAL(109,C4:C5)"], rows[4].Descendants(ns + "f").Select(static f => f.Value));
        Assert.Equal(4, sheet.Descendants(ns + "col").Count());
        var table = System.Xml.Linq.XDocument.Parse(Part("xl/tables/table1.xml"));
        Assert.Equal("A3:D5", table.Root!.Attribute("ref")!.Value);
        Assert.Equal("TableStyleMedium9", table.Descendants(ns + "tableStyleInfo").Single().Attribute("name")!.Value);
        Assert.Contains("name=\"Kartlar 2026\"", Part("xl/workbook.xml"), StringComparison.Ordinal);
        Assert.Contains("table+xml", Part("[Content_Types].xml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dd.MM.yyyy", @"dd\.mm\.yyyy")]
    [InlineData("M/d/yyyy", @"m\/d\/yyyy")]
    [InlineData("HH:mm:ss", @"hh\:mm\:ss")]
    [InlineData("h:mm:ss tt", @"h\:mm\:ss\ AM/PM")]
    public void Date_patterns_become_excel_formats(string pattern, string expected) => Assert.Equal(expected, ResultExporter.ExcelDate(pattern));

    [Fact]
    public void The_dialog_follows_the_format_with_the_file_name()
    {
        var export = new ExportResultViewModel(1250, canReadAll: true, "Sorgu 1", @"C:\Exports");
        Assert.Equal(@"C:\Exports\Sorgu 1.xlsx", export.Path);
        Assert.True(export.IsExcel);
        Assert.Equal("Sorgu 1", export.Options.Excel.SheetName);
        export.SelectedFormat = export.Formats.Single(static f => f.Value == ResultExportFormat.Csv);
        Assert.Equal(@"C:\Exports\Sorgu 1.csv", export.Path);
        Assert.True(export.IsCsv);
        Assert.False(export.IsExcel);
        export.Path = @"C:\Exports\kartlar.txt";
        export.SelectedFormat = export.Formats.Single(static f => f.Value == ResultExportFormat.Json);
        Assert.Equal(@"C:\Exports\kartlar.txt", export.Path);
        Assert.Contains("1.250", export.ShownRowsText, StringComparison.Ordinal);
        Assert.Equal(ResultExportFormat.Json, export.Options.Format);
    }

    [Fact]
    public void The_last_choices_come_back_and_reach_the_options()
    {
        var first = new ExportResultViewModel(10, canReadAll: false, "Rapor", @"C:\Exports")
        {
            AsTable = true,
            Totals = true,
            Borders = true,
            FreezeHeader = false,
            ExcelTitle = "  Kasım  ",
        };
        first.TableStyle = first.TableStyles[2];
        first.DateFormat = first.DateFormats.Single(static d => d.Value == ExcelDateFormat.Iso);
        var kept = first.Preferences;
        Assert.False(first.CanColorHeader);

        var next = new ExportResultViewModel(10, canReadAll: false, "Rapor 2", @"C:\Exports");
        next.Apply(kept);
        var excel = next.Options.Excel;
        Assert.True(excel.AsTable && excel.Totals && excel.Borders && !excel.FreezeHeader);
        Assert.Equal("TableStyleMedium7", excel.TableStyle);
        Assert.Equal(ExcelDateFormat.Iso, excel.DateFormat);
        Assert.Null(excel.Title);
        Assert.Equal("Toplam", excel.TotalsLabel);
        Assert.Equal("Kasım", first.Options.Excel.Title);

        next.Apply(kept with { Format = ResultExportFormat.Csv });
        Assert.Equal(@"C:\Exports\Rapor 2.csv", next.Path);
    }
}
