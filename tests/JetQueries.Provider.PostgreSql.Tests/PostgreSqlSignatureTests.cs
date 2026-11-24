using System.Globalization;
using JetQueries.Provider.PostgreSql.Language;
using JetQueries.Sdk;

namespace JetQueries.Provider.PostgreSql.Tests;

/// <summary>Built-in function signatures, signature help and parameter values for PostgreSQL.</summary>
public sealed class PostgreSqlSignatureTests
{
    private static readonly ISqlLanguageService Service = new PostgreSqlProvider().LanguageService!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public PostgreSqlSignatureTests()
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
        var catalog = PostgreSqlFunctions.Catalog;
        var names = catalog.Names.ToList();
        Assert.InRange(names.Count, 70, 200);
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
    [InlineData("SELECT date_trunc(|", 0)]
    [InlineData("SELECT date_trunc('month', |", 1)]
    [InlineData("SELECT DATE_TRUNC('month', now() + (interval '1 day') * 2, |", 2)]
    public async Task Help_follows_the_argument_the_caret_is_on(string marked, int parameter)
    {
        var help = await HelpAsync(marked);
        Assert.NotNull(help);
        var signature = help.Signatures[help.ActiveSignature];
        Assert.Equal("date_trunc", signature.Name);
        Assert.Equal(["field", "source", "time_zone"], signature.Parameters.Select(static p => p.Name));
        Assert.Equal(parameter, help.ActiveParameter);
        Assert.Equal("timestamp", signature.ReturnType);
        Assert.Equal("Verilen birime indirger", signature.Documentation);
        Assert.Equal("SELECT ".Length, help.CallStart);
    }

    [Theory]
    [InlineData("SELECT 1|")]
    [InlineData("SELECT x FROM t WHERE id IN (|")]
    [InlineData("INSERT INTO t (|")]
    [InlineData("SELECT coalesce(a, b) FROM t; SELECT |")]
    public async Task No_help_outside_a_call(string marked)
    {
        Assert.Null(await HelpAsync(marked));
    }

    [Fact]
    public async Task Overloads_and_repeating_parameters()
    {
        var toTimestamp = (await HelpAsync("SELECT to_timestamp(|"))!;
        Assert.Equal(2, toTimestamp.Signatures.Count);

        var concat = (await HelpAsync("SELECT concat_ws(', ', a, b, c, |"))!;
        Assert.True(concat.Signatures[0].IsVariadic);
        Assert.Equal(1, concat.ActiveParameter);
    }

    [Fact]
    public async Task A_parameter_with_values_offers_them()
    {
        // Inside a string: the quoted formats replace the string.
        var formats = await CompleteAsync("SELECT to_char(now(), 'DD|");
        Assert.All(formats, static i => Assert.Equal(CompletionItemKind.Value, i.Kind));
        var turkish = Assert.Single(formats, static i => i.Label == "'DD.MM.YYYY'");
        Assert.Equal("Türkçe tarih", turkish.Detail);
        Assert.Equal(new TextSpan("SELECT to_char(now(), ".Length, 3), formats[0].ReplaceSpan);

        var fields = await CompleteAsync("SELECT date_part('d|");
        Assert.Equal(["'day'", "'decade'", "'dow'", "'doy'"], fields.Select(static i => i.Label).Order(StringComparer.Ordinal));

        var extract = await CompleteAsync("SELECT extract(|");
        Assert.Contains(extract, static i => i.Label == "epoch");

        var flags = await CompleteAsync("SELECT regexp_replace(name, 'a', 'b', |");
        Assert.Contains(flags, static i => i.Label == "'g'");

        var setting = await CompleteAsync("SELECT current_setting('sea|");
        Assert.Equal("'search_path'", Assert.Single(setting).Label);
    }

    [Fact]
    public async Task A_parameter_without_values_gets_the_usual_proposals()
    {
        var age = Assert.Single(await CompleteAsync("SELECT ag|"), static i => i.Label == "age");
        Assert.Equal("age(timestamp, [since])", age.Detail);
        Assert.Equal("age()", age.InsertText);
        Assert.True(age.OpensCall);

        var now = Assert.Single(await CompleteAsync("SELECT date_trunc('day', no|"), static i => i.Label == "now");
        Assert.False(now.OpensCall);
    }

    [Fact]
    public async Task A_user_function_is_asked_from_the_database()
    {
        var metadata = new Routines();
        var (text, offset) = At("SELECT kart.balance_of(|");
        var help = await ((ISignatureHelpProvider)Service).GetSignatureHelpAsync(new SqlDocument(text, metadata), offset, Token);
        Assert.NotNull(help);
        Assert.Equal(new DatabaseObject(new DatabaseObjectName(null, "kart", "balance_of"), DatabaseObjectKinds.Function), metadata.Asked);

        // CALL reads like any other call.
        (text, offset) = At("CALL Purge_Old(30, |");
        help = await ((ISignatureHelpProvider)Service).GetSignatureHelpAsync(new SqlDocument(text, metadata), offset, Token);
        Assert.Equal(1, help!.ActiveParameter);
        Assert.Equal("purge_old", metadata.Asked!.Name.Name);
    }

    private sealed class Routines : IMetadataProvider, IRoutineMetadataProvider
    {
        public DatabaseObject? Asked { get; private set; }

        public Task<IReadOnlyList<SqlSignature>> GetSignaturesAsync(DatabaseObject routine, CancellationToken cancellationToken)
        {
            Asked = routine;
            return Task.FromResult<IReadOnlyList<SqlSignature>>([new SqlSignature(routine.Name.Name, [new SqlParameterInfo("p_days", "integer"), new SqlParameterInfo("p_all", "boolean", IsOptional: true)])]);
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
