using System.Globalization;
using JetQueries.Provider.Sqlite.Language;
using JetQueries.Sdk;

namespace JetQueries.Provider.Sqlite.Tests;

/// <summary>Built-in function signatures, signature help and parameter values for SQLite.</summary>
public sealed class SqliteSignatureTests
{
    private static readonly ISqlLanguageService Service = new SqliteProvider().LanguageService!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public SqliteSignatureTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
    }

    private static (string Text, int Offset) At(string marked) => (marked.Replace("|", string.Empty, StringComparison.Ordinal), marked.IndexOf('|', StringComparison.Ordinal));

    private static Task<SignatureHelp?> HelpAsync(string marked)
    {
        var (text, offset) = At(marked);
        return ((ISignatureHelpProvider)Service).GetSignatureHelpAsync(new SqlDocument(text), offset, Token);
    }

    private static Task<IReadOnlyList<CompletionItem>> CompleteAsync(string marked)
    {
        var (text, offset) = At(marked);
        return Service.GetCompletionsAsync(new SqlDocument(text), offset, Token);
    }

    [Fact]
    public void The_catalog_parses_and_reads_well()
    {
        var catalog = SqliteFunctions.Catalog;
        var names = catalog.Names.ToList();
        Assert.InRange(names.Count, 40, 150);
        Assert.All(names, static n => Assert.Equal(n.ToLowerInvariant(), n));
        foreach (var function in names.SelectMany(catalog.Find))
        {
            Assert.False(string.IsNullOrWhiteSpace(function.Documentation.English), function.Name);
            Assert.False(string.IsNullOrWhiteSpace(function.Documentation.Turkish), function.Name);

            // A description that ends with ')' would be read as part of the parameter list.
            Assert.All(function.Parameters, p => Assert.DoesNotContain("--", p.Type ?? string.Empty, StringComparison.Ordinal));
            Assert.All(function.Parameters, p => Assert.DoesNotContain(")", p.Type ?? string.Empty, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("SELECT strftime(|", 0)]
    [InlineData("SELECT strftime('%Y', |", 1)]
    [InlineData("SELECT STRFTIME('%Y', created, 'start of month', |", 2)]
    public async Task Help_follows_the_argument_the_caret_is_on(string marked, int parameter)
    {
        var help = await HelpAsync(marked);
        Assert.NotNull(help);
        var signature = help.Signatures[help.ActiveSignature];
        Assert.Equal("strftime", signature.Name);
        Assert.Equal(["format", "time_value", "modifier"], signature.Parameters.Select(static p => p.Name));
        Assert.True(signature.IsVariadic);
        Assert.Equal(parameter, help.ActiveParameter);
        Assert.Equal("text", signature.ReturnType);
        Assert.Equal("Tarihi % kodlarıyla biçimler", signature.Documentation);
    }

    [Theory]
    [InlineData("SELECT 1|")]
    [InlineData("SELECT x FROM t WHERE id IN (|")]
    [InlineData("INSERT INTO t (|")]
    public async Task No_help_outside_a_call(string marked)
    {
        Assert.Null(await HelpAsync(marked));
    }

    [Fact]
    public async Task A_parameter_with_values_offers_them()
    {
        // Inside a string: the quoted formats replace the string.
        var formats = await CompleteAsync("SELECT strftime('%d|");
        Assert.All(formats, static i => Assert.Equal(CompletionItemKind.Value, i.Kind));
        var turkish = Assert.Single(formats, static i => i.Label == "'%d.%m.%Y'");
        Assert.Equal("Türkçe tarih", turkish.Detail);
        Assert.Equal(new TextSpan("SELECT strftime(".Length, 3), formats[0].ReplaceSpan);

        // Every modifier position offers the modifiers.
        var modifiers = await CompleteAsync("SELECT date('now', 'start of month', '+1 month', 'st|");
        Assert.Equal(["'start of day'", "'start of month'", "'start of year'"], modifiers.Select(static i => i.Label).Order(StringComparer.Ordinal));

        var paths = await CompleteAsync("SELECT json_extract(data, |");
        Assert.Contains(paths, static i => i.Label == "'$.name'");
    }

    [Fact]
    public async Task A_parameter_without_values_gets_the_usual_proposals()
    {
        var ifnull = Assert.Single(await CompleteAsync("SELECT ifn|"), static i => i.Label == "ifnull");
        Assert.Equal("ifnull(value, replacement)", ifnull.Detail);
        Assert.Equal("ifnull()", ifnull.InsertText);
        Assert.True(ifnull.OpensCall);
    }

    [Fact]
    public async Task A_database_has_no_routines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jq-routines-{Guid.NewGuid():N}.db");
        try
        {
            await using var connection = await new SqliteProvider().OpenConnectionAsync(SqliteProviderTests.Profile(new() { ["file"] = path, ["mode"] = "create" }), null, Token);
            var routines = Assert.IsAssignableFrom<IRoutineMetadataProvider>(connection.Metadata);
            Assert.Empty(await routines.GetSignaturesAsync(new DatabaseObject(new DatabaseObjectName(null, null, "f"), DatabaseObjectKinds.Function), Token));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
