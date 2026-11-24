using System.Globalization;
using System.Text;
using JetQueries.Core.Connections;
using JetQueries.Core.Metadata;
using JetQueries.Presentation.ViewModels;
using JetQueries.Provider.Sqlite;
using JetQueries.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Presentation.Tests;

/// <summary>Importing a CSV file into a table.</summary>
public sealed class DataImportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public DataImportTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
    }

    [Fact]
    public void Csv_fields_may_hold_separators_quotes_and_line_breaks()
    {
        const string Text = "id;ad;not\r\n1;\"İstanbul; merkez\";\"iki\nsatır\"\r\n2;\"\"\"tırnak\"\"\";\n\n3;x;y";

        Assert.Equal(';', CsvReader.DetectDelimiter("id;ad;\"a,b\";not"));
        Assert.Equal('\t', CsvReader.DetectDelimiter("id\tad\tnot"));
        var records = CsvReader.Read(new StringReader(Text), ';').ToList();

        Assert.Equal(4, records.Count);
        Assert.Equal(["1", "İstanbul; merkez", "iki\nsatır"], records[1]);
        Assert.Equal(["2", "\"tırnak\"", ""], records[2]);
        Assert.Equal(["3", "x", "y"], records[3]);
    }

    [Theory]
    [InlineData("int", "42", 42L)]
    [InlineData("NUMBER(12,2)", "1250.5", 1250.5)]
    [InlineData("decimal(12,2)", "1.250,50", 1250.5)]
    [InlineData("bit", "evet", true)]
    [InlineData("boolean", "0", false)]
    [InlineData("bit", "hayır", false)]
    [InlineData("bit", "HAYIR", false)]
    [InlineData("nvarchar(50)", "İzmir", "İzmir")]
    public void Values_take_the_type_of_their_column(string type, string text, object expected)
    {
        var (value, error) = ImportValues.Convert(text, ImportValues.KindOf(type), emptyIsNull: true);
        Assert.Null(error);
        Assert.Equal(expected is double d ? (decimal)d : expected, value);
    }

    [Fact]
    public void Dates_empties_and_bad_values()
    {
        Assert.Equal(new DateTime(2026, 1, 15), ImportValues.Convert("2026-01-15", ImportValues.KindOf("date"), true).Value);
        Assert.Equal(new DateTime(2026, 1, 15, 14, 30, 0), ImportValues.Convert("15.01.2026 14:30", ImportValues.KindOf("datetime2(7)"), true).Value);
        Assert.Null(ImportValues.Convert(string.Empty, ImportValues.KindOf("int"), emptyIsNull: false).Value);
        Assert.Equal(string.Empty, ImportValues.Convert(string.Empty, ImportValues.KindOf("text"), emptyIsNull: false).Value);
        Assert.Equal("integer", ImportValues.Convert("on iki", ImportValues.KindOf("bigint"), true).Error);
    }

    [Fact]
    public async Task A_csv_file_is_imported_into_a_table_through_the_dialog()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var database = Path.Combine(folder, "cards.db");
        await using var env = new TestEnvironment();
        await using var manager = new ConnectionManager(new FakeExtensions(new SqliteProvider()), new SessionCredentialStore(), env.Probe);
        var definition = new ConnectionDefinition
        {
            Id = Guid.NewGuid(),
            Name = "cards",
            ProviderId = SqliteProvider.ProviderId,
            Settings = new Dictionary<string, string> { ["file"] = database, ["mode"] = "create" },
        };
        var session = (await manager.OpenAsync(definition, null, Token)).Session!;
        await foreach (var _ in session.ExecuteAsync(new QueryRequest("CREATE TABLE cards (id INTEGER PRIMARY KEY, card_no TEXT NOT NULL, balance NUMERIC, issued_at TEXT, active BOOLEAN)"), Token))
        {
        }

        // 1 250 rows in Windows-1254 with a header in another order and a column the table does not have.
        var csv = new StringBuilder("Card No;Issued At;Balance;Extra;id;active\r\n");
        for (var i = 1; i <= 1250; i++)
        {
            csv.Append(CultureInfo.InvariantCulture, $"Müşteri {i};2026-01-{1 + (i % 28):00};{i},50;x;{i};{(i % 2 == 0 ? "evet" : "hayır")}\r\n");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var file = Path.Combine(folder, "cards.csv");
        await File.WriteAllTextAsync(file, csv.ToString(), Encoding.GetEncoding("windows-1254"), Token);

        var cache = new SqliteMetadataCache(new MetadataCacheOptions(), TimeProvider.System, NullLogger<SqliteMetadataCache>.Instance);
        var import = new ImportDataViewModel(session, new DatabaseObject(new DatabaseObjectName(null, "main", "cards"), DatabaseObjectKinds.Table), new MetadataService(cache, TimeProvider.System), env.Jobs);
        Assert.True(ImportDataViewModel.CanImport(session, import.Table));
        await import.LoadAsync();
        import.SelectedEncoding = import.Encodings[1];
        import.FilePath = file;
        await import.PreviewAsync();

        // The separator was told from the header; the columns were matched by name, ignoring case and spaces.
        Assert.Equal(';', import.DetectedDelimiter);
        Assert.Equal(["Card No", "Issued At", "Balance", "Extra", "id", "active"], import.PreviewHeaders);
        Assert.Equal("Müşteri 1", import.PreviewRows[0][0]);
        Assert.Equal(["id", "Card No", "Balance", "Issued At", "active"], import.Mappings.Select(static m => m.Source!.Text));

        await import.ImportCommand.ExecuteAsync(null);
        Assert.False(import.Failed, import.Status);
        Assert.Equal(1250, import.Imported);

        var rows = new List<object?[]>();
        await foreach (var e in session.ExecuteAsync(new QueryRequest("SELECT count(*), sum(balance), max(card_no), sum(active) FROM cards"), Token))
        {
            if (e is RowBatch batch)
            {
                rows.AddRange(batch.Rows);
            }
        }

        Assert.Equal(1250L, Convert.ToInt64(rows[0][0], CultureInfo.InvariantCulture));
        Assert.Equal(1250 * 1251 / 2 + 625m, Convert.ToDecimal(rows[0][1], CultureInfo.InvariantCulture));
        Assert.Equal(625L, Convert.ToInt64(rows[0][3], CultureInfo.InvariantCulture));

        // A bad value: nothing is kept (one transaction) and the line and column are named.
        await File.WriteAllTextAsync(file, "id;card_no;balance\r\n5000;ok;1\r\n5001;bad;on iki\r\n", Encoding.UTF8, Token);
        import.SelectedEncoding = import.Encodings[0];
        await import.PreviewAsync();
        await import.ImportCommand.ExecuteAsync(null);
        Assert.True(import.Failed);
        Assert.Contains("Satır 3", import.Status, StringComparison.Ordinal);
        Assert.Contains("balance", import.Status, StringComparison.Ordinal);
        await foreach (var e in session.ExecuteAsync(new QueryRequest("SELECT count(*) FROM cards WHERE id >= 5000"), Token))
        {
            if (e is RowBatch batch)
            {
                Assert.Equal(0L, Convert.ToInt64(batch.Rows[0][0], CultureInfo.InvariantCulture));
            }
        }

        await manager.CloseAsync(definition.Id);
        cache.Dispose();
    }
}
