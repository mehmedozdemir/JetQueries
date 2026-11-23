using System.Globalization;

namespace JetQueries.Presentation.Tests;

/// <summary>Copy formats, selection totals and the value panel's preview.</summary>
public sealed class ResultToolsTests
{
    public ResultToolsTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    private static readonly ResultGridColumn[] Columns =
    [
        new("Id", 60, true, typeof(int), "int"),
        new("City", 100, false, typeof(string), "nvarchar"),
        new("Balance", 80, true, typeof(decimal), "decimal"),
    ];

    private static readonly object?[][] Rows =
    [
        [1, "İstanbul, \"Avrupa\"", 1250.5m],
        [2, "O'Hara | x", null],
    ];

    [Fact]
    public void Rows_are_copied_as_csv_json_markdown_and_sql()
    {
        int[] all = [0, 1, 2];
        Assert.Equal("Id,City,Balance\r\n1,\"İstanbul, \"\"Avrupa\"\"\",1250.5\r\n2,O'Hara | x,\r\n", ResultCopy.Format(ResultCopyFormat.Csv, Columns, Rows, all));
        Assert.Equal(
            "[\n  {\n    \"Id\": 1,\n    \"City\": \"İstanbul, \\\"Avrupa\\\"\",\n    \"Balance\": 1250.5\n  },\n  {\n    \"Id\": 2,\n    \"City\": \"O'Hara | x\",\n    \"Balance\": null\n  }\n]",
            ResultCopy.Format(ResultCopyFormat.Json, Columns, Rows, all).ReplaceLineEndings("\n"));
        Assert.Equal(
            "| Id | City | Balance |\n| ---: | --- | ---: |\n| 1 | İstanbul, \"Avrupa\" | 1250.5 |\n| 2 | O'Hara \\| x | NULL |\n",
            ResultCopy.Format(ResultCopyFormat.Markdown, Columns, Rows, all));
        Assert.Equal(
            "INSERT INTO table_name (Id, City, Balance) VALUES (1, 'İstanbul, \"Avrupa\"', 1250.5);\nINSERT INTO table_name (Id, City, Balance) VALUES (2, 'O''Hara | x', NULL);\n",
            ResultCopy.Format(ResultCopyFormat.SqlInsert, Columns, Rows, all));

        // An IN list of one column: distinct, NULLs left out.
        Assert.Equal("(1250.5)", ResultCopy.Format(ResultCopyFormat.SqlInList, Columns, [.. Rows, [3, "x", 1250.5m]], [2]));
        Assert.Equal("('2026-01-15', 0x0A0B)", ResultCopy.Format(
            ResultCopyFormat.SqlInList, [new("D", 1, false, typeof(object))], [[new DateTime(2026, 1, 15)], [new byte[] { 10, 11 }]], [0]));
    }

    [Fact]
    public void Selection_totals_count_values_and_sum_numbers_exactly()
    {
        object?[][] rows = [[0.1m, "a", 1], [0.2m, null, 2], [null, "b", 3.5d]];

        var stats = SelectionStats.Compute(rows, [0, 1, 2], CancellationToken.None);

        Assert.Equal((9L, 7L, 5L), (stats.Cells, stats.Values, stats.Numbers));
        Assert.Equal(6.8m, stats.Sum);
        Assert.Equal((0.1m, 3.5m), (stats.Min, stats.Max));
        Assert.Equal(1.36m, stats.Average);
        Assert.Equal(0, SelectionStats.Compute(rows, [1], CancellationToken.None).Numbers);
    }

    [Fact]
    public void Values_are_previewed_by_what_they_contain()
    {
        Assert.Equal(ValuePreviewKind.Null, ValuePreview.Describe(null).Kind);

        var json = ValuePreview.Describe("{\"şehir\":\"İzmir\",\"n\":[1,2]}");
        Assert.Equal(ValuePreviewKind.Json, json.Kind);
        Assert.Equal("{\n  \"şehir\": \"İzmir\",\n  \"n\": [\n    1,\n    2\n  ]\n}", json.Text.ReplaceLineEndings("\n"));

        var xml = ValuePreview.Describe("<a><b x=\"1\"/></a>");
        Assert.Equal(ValuePreviewKind.Xml, xml.Kind);
        Assert.Equal("<a>\n  <b x=\"1\" />\n</a>", xml.Text.ReplaceLineEndings("\n"));

        Assert.Equal(ValuePreviewKind.Text, ValuePreview.Describe("{ not json").Kind);
        Assert.Equal("10 characters", ValuePreview.Describe("{ not json").Size);

        var png = ValuePreview.Describe(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2 });
        Assert.Equal(ValuePreviewKind.Image, png.Kind);
        Assert.Equal("6 B", png.Size);

        var binary = ValuePreview.Describe("JetQueries!"u8.ToArray());
        Assert.Equal(ValuePreviewKind.Binary, binary.Kind);
        Assert.Equal("00000000  4A 65 74 51 75 65 72 69 65 73 21                 JetQueries!\n", binary.Text);
    }
}
