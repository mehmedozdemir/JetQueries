using System.Globalization;
using JetQueries.Sdk;

namespace JetQueries.Provider.SqlServer.Tests;

/// <summary>Built-in function signatures, signature help and parameter values for T-SQL.</summary>
public sealed class TSqlSignatureTests
{
    private static readonly ISqlLanguageService Service = new SqlServerProvider().LanguageService!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public TSqlSignatureTests()
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

    [Theory]
    [InlineData("SELECT DATEADD(|", 0)]
    [InlineData("SELECT DATEADD(day, |", 1)]
    [InlineData("SELECT DATEADD(day, (1 + 2) * 3, |", 2)]
    [InlineData("SELECT DATEADD(day, ABS(-1), GETDATE(|", 2)]
    public async Task Help_follows_the_argument_the_caret_is_on(string marked, int parameter)
    {
        var help = await HelpAsync(marked);
        Assert.NotNull(help);
        var signature = help.Signatures[help.ActiveSignature];

        // Inside GETDATE( the innermost call is GETDATE, which has no parameters.
        if (marked.EndsWith("GETDATE(|", StringComparison.Ordinal))
        {
            Assert.Equal("GETDATE", signature.Name);
            return;
        }

        Assert.Equal("DATEADD", signature.Name);
        Assert.Equal(["datepart", "number", "date"], signature.Parameters.Select(static p => p.Name));
        Assert.Equal(parameter, help.ActiveParameter);
        Assert.Equal("datetime", signature.ReturnType);
        Assert.Equal("Tarihe verilen birimden belirtilen kadar ekler", signature.Documentation);
        Assert.Equal("SELECT ".Length, help.CallStart);
    }

    [Theory]
    [InlineData("SELECT 1|")]
    [InlineData("SELECT x FROM t WHERE id IN (|")]
    [InlineData("INSERT INTO t (|")]
    [InlineData("SELECT DATEADD(day, 1, x); SELECT |")]
    public async Task No_help_outside_a_call(string marked)
    {
        Assert.Null(await HelpAsync(marked));
    }

    [Fact]
    public async Task A_parameter_with_values_offers_them()
    {
        var items = await CompleteAsync("SELECT DATEADD(|");
        Assert.All(items, static i => Assert.Equal(CompletionItemKind.Value, i.Kind));
        var month = Assert.Single(items, static i => i.Label == "month");
        Assert.Equal("Ay", month.Detail);

        // Typed part filters; the replaced span is the word.
        var typed = await CompleteAsync("SELECT DATEDIFF(mi|");
        Assert.Equal(["microsecond", "millisecond", "minute"], typed.Select(static i => i.Label).Order());
        Assert.Equal(new TextSpan("SELECT DATEDIFF(".Length, 2), typed[0].ReplaceSpan);

        // CONVERT's third argument: the date styles.
        var styles = await CompleteAsync("SELECT CONVERT(varchar(10), GETDATE(), |");
        Assert.Contains(styles, static i => i.Label == "104" && i.Detail!.Contains("Türkçe", StringComparison.Ordinal));

        // Inside a string: the quoted formats replace the string.
        var formats = await CompleteAsync("SELECT FORMAT(x, 'dd|");
        Assert.Contains(formats, static i => i.Label == "'dd.MM.yyyy'");
        Assert.Equal(new TextSpan("SELECT FORMAT(x, ".Length, 3), formats[0].ReplaceSpan);
    }

    [Fact]
    public async Task A_parameter_without_values_gets_the_usual_proposals()
    {
        var items = await CompleteAsync("SELECT DATEADD(day, GE|");
        var getdate = Assert.Single(items, static i => i.Label == "GETDATE");
        Assert.Equal("GETDATE()", getdate.InsertText);
        Assert.False(getdate.OpensCall);

        var dateAdd = Assert.Single(await CompleteAsync("SELECT DATEA|"), static i => i.Label == "DATEADD");
        Assert.Equal("DATEADD(datepart, number, date)", dateAdd.Detail);
        Assert.Equal("DATEADD()", dateAdd.InsertText);
        Assert.True(dateAdd.OpensCall);
        Assert.StartsWith("Tarihe verilen birimden", dateAdd.Documentation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Optional_and_repeating_parameters_are_marked()
    {
        var round = (await HelpAsync("SELECT ROUND(x, 2, |"))!;
        Assert.True(round.Signatures[0].Parameters[2].IsOptional);
        Assert.Equal(["0", "1"], round.Signatures[0].Parameters[2].Values.Select(static v => v.Text));

        var concat = (await HelpAsync("SELECT CONCAT(a, b, c, d, |"))!;
        Assert.True(concat.Signatures[0].IsVariadic);
        Assert.Equal(1, concat.ActiveParameter);
    }

    [Fact]
    public async Task Exec_asks_the_database_for_a_procedure()
    {
        var metadata = new Routines();
        var (text, offset) = At("EXEC dbo.KartBul 'x', |");
        var help = await ((ISignatureHelpProvider)Service).GetSignatureHelpAsync(new SqlDocument(text, metadata) { DefaultDatabase = "Db" }, offset, Token);
        Assert.NotNull(help);
        Assert.Equal(1, help.ActiveParameter);
        Assert.Equal(new DatabaseObject(new DatabaseObjectName("Db", "dbo", "KartBul"), DatabaseObjectKinds.Procedure), metadata.Asked);

        // A user function call.
        (text, offset) = At("SELECT dbo.Fn(|");
        help = await ((ISignatureHelpProvider)Service).GetSignatureHelpAsync(new SqlDocument(text, metadata), offset, Token);
        Assert.Equal("@no", help!.Signatures[0].Parameters[0].Name);
        Assert.Equal(DatabaseObjectKinds.Function, metadata.Asked!.Kind);
    }

    [Theory]
    [InlineData("SELECT DATENAME(|", "DATENAME", "Tarihin bir parçasını metin olarak verir (ay adı…)")]
    [InlineData("SELECT LEN(|", "LEN", "Karakter sayısı (sondaki boşluklar sayılmaz)")]
    [InlineData("SELECT CHOOSE(|", "CHOOSE", "Verilen sıradaki değer (1'den başlar)")]
    public async Task A_description_with_brackets_stays_out_of_the_parameters(string marked, string name, string documentation)
    {
        var signature = (await HelpAsync(marked))!.Signatures[0];
        Assert.Equal(name, signature.Name);
        Assert.Equal(documentation, signature.Documentation);
        Assert.All(signature.Parameters, static p => Assert.DoesNotContain(")", p.DataType ?? string.Empty, StringComparison.Ordinal));
    }

    private sealed class Routines : IMetadataProvider, IRoutineMetadataProvider
    {
        public DatabaseObject? Asked { get; private set; }

        public Task<IReadOnlyList<SqlSignature>> GetSignaturesAsync(DatabaseObject routine, CancellationToken cancellationToken)
        {
            Asked = routine;
            return Task.FromResult<IReadOnlyList<SqlSignature>>([new SqlSignature(routine.Name.Name, [new SqlParameterInfo("@no", "varchar(19)"), new SqlParameterInfo("@adet", "int") { Direction = "OUT" }])]);
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

    [Fact]
    public async Task A_function_called_without_brackets_is_inserted_without_them()
    {
        var items = await CompleteAsync("SELECT CURRENT_TIM|");

        var item = Assert.Single(items, static i => i.Label == "CURRENT_TIMESTAMP");
        Assert.Equal(CompletionItemKind.Function, item.Kind);
        Assert.Equal("CURRENT_TIMESTAMP", item.InsertText);
        Assert.False(item.OpensCall);
        Assert.Equal("CURRENT_TIMESTAMP", item.Detail);
        Assert.NotNull(item.Documentation);
        Assert.Null(await HelpAsync("SELECT CURRENT_TIMESTAMP(|"));
    }
}
