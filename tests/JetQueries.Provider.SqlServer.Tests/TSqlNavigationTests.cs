using System.Diagnostics;
using JetQueries.Sdk;

namespace JetQueries.Provider.SqlServer.Tests;

public sealed class TSqlDiagnosticsTests
{
    private static readonly ISqlLanguageService Service = new SqlServerProvider().LanguageService!;

    [Theory]
    [InlineData("")]
    [InlineData("SELECT c.Id, c.[Card No] FROM dbo.Cards AS c WHERE c.Balance > 0 ORDER BY c.Id;")]
    [InlineData("CREATE TABLE t (id int PRIMARY KEY);\nGO\nCREATE OR ALTER PROCEDURE p AS SELECT 1;\nGO 3\nEXEC p;")]
    [InlineData("SELECT * FROM table_that_does_not_exist WHERE no_such_column = 1")]
    [InlineData("DECLARE @x int = 1; IF @x = 1 BEGIN PRINT N'Müşteri'; END")]
    [InlineData("SELECT \"quoted name\" FROM [t] -- comment\n/* block */")]
    public async Task Valid_syntax_has_no_diagnostics(string sql) =>
        Assert.Empty(await Service.GetDiagnosticsAsync(new SqlDocument(sql), TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_syntax_error_is_reported_on_the_token_the_parser_stopped_at()
    {
        const string Sql = "SELECT Id,\nFROM dbo.Cards\nWHERE Balance > 0";

        var diagnostic = Assert.Single(await Service.GetDiagnosticsAsync(new SqlDocument(Sql), TestContext.Current.CancellationToken));

        Assert.Equal(SqlDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("FROM", Sql.Substring(diagnostic.Span.Start, diagnostic.Span.Length));
        Assert.Contains("FROM", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT * FROM")]
    [InlineData("SELECT 1 +")]
    [InlineData("SELECT 1;\nGO\nSELECT 2 FROM;\nGO 2\nSELECT 3")]
    [InlineData("SELECT (1 + 2 FROM t")]
    [InlineData("UPDATE t SET WHERE id = 1")]
    public async Task Broken_text_gives_diagnostics_inside_the_text(string sql)
    {
        var diagnostics = await Service.GetDiagnosticsAsync(new SqlDocument(sql), TestContext.Current.CancellationToken);

        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d =>
        {
            Assert.InRange(d.Span.Start, 0, sql.Length);
            Assert.InRange(d.Span.End, d.Span.Start, sql.Length);
            Assert.False(string.IsNullOrWhiteSpace(d.Message));
        });
    }

    [Fact]
    public async Task An_error_in_a_later_batch_is_placed_in_the_whole_text()
    {
        const string Sql = "SELECT 1;\nGO 2\nSELECT 2;\nGO\nSELECT 3 FROM WHERE x = 1";

        var diagnostic = Assert.Single(await Service.GetDiagnosticsAsync(new SqlDocument(Sql), TestContext.Current.CancellationToken));

        // The parser points at a token of the third batch; its position is given in the whole text.
        Assert.True(diagnostic.Span.Start > Sql.IndexOf("SELECT 3", StringComparison.Ordinal));
        Assert.True(Sql.Substring(diagnostic.Span.Start, diagnostic.Span.Length) is "FROM" or "WHERE");
    }

    [Fact]
    public async Task Checking_a_large_script_is_quick_enough_to_run_while_typing()
    {
        var sql = string.Concat(Enumerable.Repeat("SELECT c.Id, c.Balance FROM dbo.Cards c JOIN dbo.Transactions t ON t.CardId = c.Id WHERE c.Status = 'A';\n", 2_000));
        await Service.GetDiagnosticsAsync(new SqlDocument(sql), TestContext.Current.CancellationToken);

        var watch = Stopwatch.StartNew();
        var diagnostics = await Service.GetDiagnosticsAsync(new SqlDocument(sql + "SELECT FROM"), TestContext.Current.CancellationToken);
        watch.Stop();

        Assert.NotEmpty(diagnostics);

        // Runs in the background half a second after the last key press; it must still feel immediate.
        Assert.True(watch.ElapsedMilliseconds < 1_000, $"parsing 2,000 lines took {watch.ElapsedMilliseconds} ms");
    }
}

/// <summary>Go to definition; the caret is where the <c>|</c> is.</summary>
public sealed class TSqlDefinitionTests
{
    private static readonly ISqlLanguageService Service = new SqlServerProvider().LanguageService!;

    [Theory]
    [InlineData("SELECT * FROM Car|ds", "Cards", DatabaseObjectKinds.Table, null)]
    [InlineData("SELECT * FROM dbo.|Cards c", "Cards", DatabaseObjectKinds.Table, null)]
    [InlineData("SELECT * FROM [dbo].[CardSumm|ary]", "CardSummary", DatabaseObjectKinds.View, null)]
    [InlineData("SELECT c|.Id FROM Cards c", "Cards", DatabaseObjectKinds.Table, null)]
    [InlineData("SELECT c.Bal|ance FROM Cards c", "Cards", DatabaseObjectKinds.Table, "Balance")]
    [InlineData("SELECT t.Amount FROM Cards c JOIN Transactions t ON t.Card|Id = c.Id", "Transactions", DatabaseObjectKinds.Table, "CardId")]
    [InlineData("SELECT Cards.I|d FROM Cards", "Cards", DatabaseObjectKinds.Table, "Id")]
    [InlineData("SELECT Amo|unt FROM Cards c JOIN Transactions t ON t.CardId = c.Id", "Transactions", DatabaseObjectKinds.Table, "Amount")]
    [InlineData("EXEC dbo.Purge|OldLogs", "PurgeOldLogs", DatabaseObjectKinds.Procedure, null)]
    [InlineData("EXEC PurgeOldLogs|", "PurgeOldLogs", DatabaseObjectKinds.Procedure, null)]
    [InlineData("SELECT * FROM Archive.dbo.Old|Cards", "OldCards", DatabaseObjectKinds.Table, null)]
    [InlineData("select * from CARDS| where 1 = 1", "Cards", DatabaseObjectKinds.Table, null)]
    public async Task A_name_leads_to_its_object(string sql, string expectedName, DatabaseObjectKinds expectedKind, string? expectedMember)
    {
        var definition = await FindAsync(sql);

        Assert.NotNull(definition);
        Assert.Equal(expectedName, definition.Target!.Name.Name);
        Assert.Equal(expectedKind, definition.Target.Kind);
        Assert.Equal(expectedMember, definition.MemberName);
    }

    [Fact]
    public async Task The_object_carries_its_database_and_schema()
    {
        var definition = await FindAsync("SELECT * FROM Archive.dbo.Old|Cards");

        Assert.Equal(new DatabaseObjectName("Archive", "dbo", "OldCards"), definition!.Target!.Name);
    }

    [Theory]
    [InlineData("SELECT * FROM NoSuch|Table")]
    [InlineData("SELECT x.Id| FROM NoSuchTable x")]
    [InlineData("SEL|ECT * FROM Cards")]
    [InlineData("SELECT * FROM Cards WHERE Status = 'Car|ds'")]
    [InlineData("SELECT * FROM Cards -- Car|ds")]
    [InlineData("SELECT * FROM db|o.Cards")]
    [InlineData("SELECT 1 |")]
    public async Task Where_there_is_nothing_to_go_to_the_answer_is_null(string sql) =>
        Assert.Null(await FindAsync(sql));

    [Fact]
    public async Task Without_metadata_there_is_no_definition() =>
        Assert.Null(await Service.GetDefinitionAsync(new SqlDocument("SELECT * FROM Cards"), 16, TestContext.Current.CancellationToken));

    private static Task<SqlDefinition?> FindAsync(string sqlWithCaret)
    {
        var offset = sqlWithCaret.IndexOf('|', StringComparison.Ordinal);
        return Service.GetDefinitionAsync(new SqlDocument(sqlWithCaret.Remove(offset, 1), new FakeCatalog()), offset, TestContext.Current.CancellationToken);
    }
}
