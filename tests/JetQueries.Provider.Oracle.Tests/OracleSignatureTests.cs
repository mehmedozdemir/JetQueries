using System.Globalization;
using JetQueries.Provider.Oracle.Language;
using JetQueries.Sdk;

namespace JetQueries.Provider.Oracle.Tests;

/// <summary>Built-in function signatures, signature help and parameter values for Oracle.</summary>
public sealed class OracleSignatureTests
{
    private static readonly ISqlLanguageService Service = new OracleProvider().LanguageService!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public OracleSignatureTests()
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
        var catalog = OracleFunctions.Catalog;
        var names = catalog.Names.ToList();
        Assert.InRange(names.Count, 70, 150);
        Assert.All(names, static n => Assert.Equal(n.ToUpperInvariant(), n));
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
    [InlineData("SELECT TO_CHAR(|", 0)]
    [InlineData("SELECT TO_CHAR(hire_date, |", 1)]
    [InlineData("SELECT to_char(SUBSTR(x, 1, 2), 'YYYY', |", 2)]
    public async Task Help_follows_the_argument_the_caret_is_on(string marked, int parameter)
    {
        var help = await HelpAsync(marked);
        Assert.NotNull(help);
        var signature = help.Signatures[help.ActiveSignature];
        Assert.Equal("TO_CHAR", signature.Name);
        Assert.Equal(["value", "format", "nls"], signature.Parameters.Select(static p => p.Name));
        Assert.Equal(parameter, help.ActiveParameter);
        Assert.Equal("VARCHAR2", signature.ReturnType);
        Assert.Equal("Tarih ya da sayıyı biçim maskesiyle metne çevirir", signature.Documentation);
        Assert.Equal("SELECT ".Length, help.CallStart);
    }

    [Theory]
    [InlineData("SELECT 1|")]
    [InlineData("SELECT x FROM t WHERE id IN (|")]
    [InlineData("INSERT INTO t (|")]
    [InlineData("SELECT NVL(a, b) FROM t; SELECT |")]
    public async Task No_help_outside_a_call(string marked)
    {
        Assert.Null(await HelpAsync(marked));
    }

    [Fact]
    public async Task Overloads_and_repeating_parameters()
    {
        var trunc = (await HelpAsync("SELECT TRUNC(SYSDATE, |"))!;
        Assert.Equal(2, trunc.Signatures.Count);
        Assert.True(trunc.Signatures[0].Parameters[1].IsOptional);

        var decode = (await HelpAsync("SELECT DECODE(status, 'A', 1, 'B', 2, |"))!;
        Assert.True(decode.Signatures[0].IsVariadic);
        Assert.Equal(2, decode.ActiveParameter);
    }

    [Fact]
    public async Task A_parameter_with_values_offers_them()
    {
        // Inside a string: the quoted masks replace the string.
        var masks = await CompleteAsync("SELECT TO_CHAR(SYSDATE, 'DD|");
        Assert.All(masks, static i => Assert.Equal(CompletionItemKind.Value, i.Kind));
        var turkish = Assert.Single(masks, static i => i.Label == "'DD.MM.YYYY'");
        Assert.Equal("Türkçe tarih", turkish.Detail);
        Assert.Equal(new TextSpan("SELECT TO_CHAR(SYSDATE, ".Length, 3), masks[0].ReplaceSpan);

        var units = await CompleteAsync("SELECT TRUNC(SYSDATE, |");
        Assert.Contains(units, static i => i.Label == "'MONTH'");

        var nls = await CompleteAsync("SELECT TO_CHAR(SYSDATE, 'MONTH', |");
        Assert.Contains(nls, static i => i.Label == "'NLS_DATE_LANGUAGE = TURKISH'");

        var context = await CompleteAsync("SELECT SYS_CONTEXT('USERENV', 'C|");
        Assert.Contains(context, static i => i.Label == "'CURRENT_SCHEMA'");
        Assert.Equal(["'CLIENT_IDENTIFIER'", "'CLIENT_INFO'", "'CON_NAME'", "'CURRENT_SCHEMA'", "'CURRENT_USER'"], context.Select(static i => i.Label).Order(StringComparer.Ordinal));

        var match = await CompleteAsync("SELECT 1 FROM t WHERE REGEXP_LIKE(name, '^a', |");
        Assert.Contains(match, static i => i.Label == "'i'");

        var field = await CompleteAsync("SELECT EXTRACT(|");
        Assert.Contains(field, static i => i.Label == "YEAR");
    }

    [Fact]
    public async Task A_parameter_without_values_gets_the_usual_proposals()
    {
        var nvl = Assert.Single(await CompleteAsync("SELECT NV|"), static i => i.Label == "NVL");
        Assert.Equal("NVL(expr1, expr2)", nvl.Detail);
        Assert.Equal("NVL()", nvl.InsertText);
        Assert.True(nvl.OpensCall);
        Assert.StartsWith("expr1 NULL ise expr2", nvl.Documentation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_package_member_or_routine_is_asked_from_the_database()
    {
        var metadata = new Routines();
        var (text, offset) = At("BEGIN card_ops.open_card(1, |");
        var help = await ((ISignatureHelpProvider)Service).GetSignatureHelpAsync(new SqlDocument(text, metadata) { DefaultSchema = "APP" }, offset, Token);
        Assert.NotNull(help);
        Assert.Equal(1, help.ActiveParameter);
        Assert.Equal(new DatabaseObject(new DatabaseObjectName(null, "CARD_OPS", "OPEN_CARD"), DatabaseObjectKinds.Function), metadata.Asked);
        Assert.Equal("OUT", help.Signatures[0].Parameters[1].Direction);

        // An unqualified call to a user function: the default schema.
        (text, offset) = At("SELECT balance_of(|");
        await ((ISignatureHelpProvider)Service).GetSignatureHelpAsync(new SqlDocument(text, metadata) { DefaultSchema = "APP" }, offset, Token);
        Assert.Equal(new DatabaseObjectName(null, "APP", "BALANCE_OF"), metadata.Asked!.Name);
    }

    private sealed class Routines : IMetadataProvider, IRoutineMetadataProvider
    {
        public DatabaseObject? Asked { get; private set; }

        public Task<IReadOnlyList<SqlSignature>> GetSignaturesAsync(DatabaseObject routine, CancellationToken cancellationToken)
        {
            Asked = routine;
            return Task.FromResult<IReadOnlyList<SqlSignature>>(
                [new SqlSignature(routine.Name.Name, [new SqlParameterInfo("P_ID", "NUMBER"), new SqlParameterInfo("P_OUT", "VARCHAR2") { Direction = "OUT" }])]);
        }

        public async IAsyncEnumerable<DatabaseObject> GetObjectsAsync(DatabaseObject? parent, DatabaseObjectKinds kinds, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DatabaseObjectName tableOrView, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ColumnInfo>>([]);

        public Task<string?> GetDefinitionAsync(DatabaseObject target, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
