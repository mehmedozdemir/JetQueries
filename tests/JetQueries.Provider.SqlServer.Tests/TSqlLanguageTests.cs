using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using JetQueries.Provider.SqlServer.Language;
using JetQueries.Sdk;

namespace JetQueries.Provider.SqlServer.Tests;

public sealed class TSqlLexerTests
{
    private static readonly ISqlLanguageService Service = new SqlServerProvider().LanguageService!;

    [Fact]
    public void Tokens_get_their_kinds()
    {
        const string Line = "SELECT TOP (10) c.[Card No], ISNULL(@x, N'a''b'), CAST(1.5 AS decimal(9,2)) FROM #tmp c -- note";

        var tokens = Tokens(Line, 0, out var state);

        Assert.Equal(0, state);
        Assert.Contains(("SELECT", SqlTokenKind.Keyword), tokens);
        Assert.Contains(("TOP", SqlTokenKind.Keyword), tokens);
        Assert.Contains(("[Card No]", SqlTokenKind.Identifier), tokens);
        Assert.Contains(("ISNULL", SqlTokenKind.Function), tokens);
        Assert.Contains(("@x", SqlTokenKind.Parameter), tokens);
        Assert.Contains(("N'a''b'", SqlTokenKind.StringLiteral), tokens);
        Assert.Contains(("CAST", SqlTokenKind.Function), tokens);
        Assert.Contains(("1.5", SqlTokenKind.NumberLiteral), tokens);
        Assert.Contains(("decimal", SqlTokenKind.DataType), tokens);
        Assert.Contains(("#tmp", SqlTokenKind.Identifier), tokens);
        Assert.Equal(("-- note", SqlTokenKind.Comment), tokens[^1]);
    }

    [Fact]
    public void Block_comments_nest_and_span_lines()
    {
        var first = Tokens("select 1 /* outer /* inner */ still", 0, out var state);
        Assert.Equal(("/* outer /* inner */ still", SqlTokenKind.Comment), first[^1]);
        Assert.NotEqual(0, state);

        var second = Tokens("comment */ select 2", state, out state);
        Assert.Equal([("comment */", SqlTokenKind.Comment), ("select", SqlTokenKind.Keyword), ("2", SqlTokenKind.NumberLiteral)], second);
        Assert.Equal(0, state);
    }

    [Fact]
    public void Strings_span_lines()
    {
        var first = Tokens("select 'line one", 0, out var state);
        Assert.Equal(("'line one", SqlTokenKind.StringLiteral), first[^1]);

        var second = Tokens("line two' , 3", state, out state);
        Assert.Equal(("line two'", SqlTokenKind.StringLiteral), second[0]);
        Assert.Equal(0, state);
    }

    [Fact]
    public void Keywords_do_not_depend_on_the_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var tokens = Tokens("insert into t select isnull(x, 1), cast(y as int) union", 0, out _);

            Assert.Equal(SqlTokenKind.Keyword, tokens[0].Kind);
            Assert.Contains(("isnull", SqlTokenKind.Function), tokens);
            Assert.Contains(("int", SqlTokenKind.DataType), tokens);
            Assert.Equal(("union", SqlTokenKind.Keyword), tokens[^1]);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static List<(string Text, SqlTokenKind Kind)> Tokens(string line, int state, out int endState)
    {
        var result = Service.TokenizeLine(line, state);
        endState = result.EndState;
        return [.. result.Tokens.Select(t => (line.Substring(t.Span.Start, t.Span.Length), t.Kind))];
    }
}

public sealed class TSqlBatchSplitterTests
{
    [Fact]
    public void A_script_is_cut_on_go_lines()
    {
        const string Script = "CREATE TABLE t (id int);\nGO\nINSERT INTO t VALUES (1);\n  go 3 -- three times\n\nSELECT * FROM t;\nGO\n";

        var batches = new SqlServerProvider().SplitBatches(Script);

        Assert.Equal(3, batches.Count);
        Assert.Equal(new ScriptBatch("CREATE TABLE t (id int);\n", 0), batches[0]);
        Assert.Equal(new ScriptBatch("INSERT INTO t VALUES (1);\n", 2, 3), batches[1]);
        Assert.Equal("\nSELECT * FROM t;\n", batches[2].Text);
        Assert.Equal(4, batches[2].StartLine);
    }

    [Theory]
    [InlineData("SELECT 'a\nGO\nb' AS text")]
    [InlineData("/* comment\nGO\nstill comment */ SELECT 1")]
    [InlineData("SELECT 1 AS GO")]
    [InlineData("GOTO done; done: SELECT 1")]
    [InlineData("SELECT go FROM t")]
    public void Go_inside_a_string_a_comment_or_a_statement_is_not_a_separator(string script) =>
        Assert.Equal(script, Assert.Single(new SqlServerProvider().SplitBatches(script)).Text);

    [Fact]
    public void Empty_batches_are_dropped_and_windows_line_ends_work()
    {
        var batches = new SqlServerProvider().SplitBatches("GO\r\n\r\nGO\r\nSELECT 1\r\nGO\r\n   \r\n");

        var only = Assert.Single(batches);
        Assert.Equal("SELECT 1\r\n", only.Text);
        Assert.Equal(3, only.StartLine);
        Assert.Empty(new SqlServerProvider().SplitBatches(string.Empty));
    }
}

/// <summary>Completion; the caret is where the <c>|</c> is.</summary>
public sealed class TSqlCompletionTests
{
    private static readonly ISqlLanguageService Service = new SqlServerProvider().LanguageService!;

    [Theory]
    [InlineData("SELECT * FROM |")]
    [InlineData("SELECT * FROM cards c JOIN |")]
    [InlineData("SELECT * FROM cards c, |")]
    [InlineData("INSERT INTO |")]
    [InlineData("UPDATE |")]
    [InlineData("DELETE FROM |")]
    [InlineData("TRUNCATE TABLE |")]
    public async Task Tables_and_views_are_offered_where_a_table_is_expected(string sql)
    {
        var items = await CompleteAsync(sql);

        Assert.Equal(["Cards", "CardSummary", "Müşteri İşlemleri", "Transactions"], items.Select(static i => i.Label));
        Assert.Contains(items, static i => i is { Label: "Cards", Kind: CompletionItemKind.Table });
        Assert.Contains(items, static i => i is { Label: "CardSummary", Kind: CompletionItemKind.View });
        Assert.Equal("[Müşteri İşlemleri]", items.Single(static i => i.Label == "Müşteri İşlemleri").InsertText);
        Assert.DoesNotContain(items, static i => i.Kind is CompletionItemKind.Keyword or CompletionItemKind.Column);
    }

    [Fact]
    public async Task What_was_typed_filters_the_list_and_is_replaced()
    {
        const string Sql = "SELECT * FROM car|";

        var items = await CompleteAsync(Sql);

        Assert.Equal(["Cards", "CardSummary"], items.Select(static i => i.Label));
        Assert.All(items, i => Assert.Equal(new TextSpan(Sql.IndexOf("car", StringComparison.Ordinal), 3), i.ReplaceSpan));
    }

    [Theory]
    [InlineData("SELECT c.| FROM dbo.Cards c")]
    [InlineData("SELECT * FROM Cards AS c WHERE c.|")]
    [InlineData("SELECT * FROM [dbo].[Cards] c JOIN Transactions t ON t.CardId = c.|")]
    [InlineData("UPDATE c SET c.| FROM Cards c")]
    [InlineData("SELECT Cards.| FROM Cards")]
    [InlineData("SELECT x.Id FROM Transactions x WHERE EXISTS (SELECT 1 FROM Cards c WHERE c.| = x.CardId)")]
    public async Task After_an_alias_or_table_name_its_columns_are_offered_in_table_order(string sql)
    {
        var items = await CompleteAsync(sql);

        Assert.Equal(["Id", "Card No", "Balance", "Status"], items.Select(static i => i.Label));
        Assert.All(items, static i => Assert.Equal(CompletionItemKind.Column, i.Kind));
        Assert.Equal("[Card No]", items[1].InsertText);
        Assert.Equal("bigint · PK", items[0].Detail);
    }

    [Fact]
    public async Task A_subquery_sees_the_outer_tables_too()
    {
        var items = await CompleteAsync("SELECT * FROM Transactions x WHERE EXISTS (SELECT 1 FROM Cards c WHERE c.Id = x.|)");

        Assert.Equal(["TxnId", "CardId", "Amount"], items.Select(static i => i.Label));
    }

    [Theory]
    [InlineData("SELECT | FROM Cards c JOIN Transactions t ON t.CardId = c.Id")]
    [InlineData("SELECT * FROM Cards c JOIN Transactions t ON t.CardId = c.Id WHERE |")]
    [InlineData("SELECT * FROM Cards c JOIN Transactions t ON t.CardId = c.Id ORDER BY c.Id, |")]
    public async Task Where_an_expression_is_expected_the_columns_of_all_tables_in_scope_come_first(string sql)
    {
        var items = await CompleteAsync(sql);

        Assert.Equal(["Amount", "Balance", "Card No", "CardId", "Id", "Status", "TxnId"], items.Where(static i => i.Kind == CompletionItemKind.Column).Select(static i => i.Label));
        Assert.Equal(["c", "t"], items.Where(static i => i.Kind == CompletionItemKind.Alias).Select(static i => i.Label));
        Assert.Equal("decimal(12,2) · t", items.Single(static i => i.Label == "Amount").Detail);
        Assert.True(items.FindIndex(static i => i.Kind == CompletionItemKind.Alias) > items.FindLastIndex(static i => i.Kind == CompletionItemKind.Column));
        Assert.DoesNotContain(items, static i => i.Kind == CompletionItemKind.Keyword);
    }

    [Fact]
    public async Task Typing_brings_in_matching_functions_and_keywords()
    {
        var items = await CompleteAsync("SELECT c.Id FROM Cards c WHERE co|");

        Assert.Contains(items, static i => i is { Label: "COALESCE", Kind: CompletionItemKind.Function });
        Assert.Contains(items, static i => i is { Label: "CONVERT", Kind: CompletionItemKind.Function });
        Assert.Contains(items, static i => i is { Label: "COMMIT", Kind: CompletionItemKind.Keyword });
        Assert.DoesNotContain(items, static i => i.Label == "Balance");

        var start = await CompleteAsync("sel|");
        Assert.Equal("SELECT", Assert.Single(start).Label);
    }

    [Fact]
    public async Task After_a_schema_its_objects_and_after_a_database_its_schemas()
    {
        var inSchema = await CompleteAsync("SELECT * FROM kart.|");
        Assert.Equal(["Limits"], inSchema.Select(static i => i.Label));

        var procedures = await CompleteAsync("EXEC dbo.|");
        Assert.Contains(procedures, static i => i is { Label: "PurgeOldLogs", Kind: CompletionItemKind.Procedure });

        var schemas = await CompleteAsync("SELECT * FROM Archive.|");
        Assert.Equal(["dbo", "kart"], schemas.Select(static i => i.Label));

        var crossDatabase = await CompleteAsync("SELECT * FROM Archive.dbo.|");
        Assert.Equal(["OldCards"], crossDatabase.Select(static i => i.Label));
    }

    [Theory]
    [InlineData("SELECT 'text with FROM |")]
    [InlineData("SELECT 1 -- FROM |")]
    [InlineData("/* SELECT c.| FROM Cards c */")]
    [InlineData("SELECT * FROM Cards c |")]
    [InlineData("|")]
    public async Task Nothing_is_offered_in_strings_comments_or_where_nothing_is_expected(string sql) =>
        Assert.Empty(await CompleteAsync(sql));

    [Fact]
    public async Task Half_typed_and_multi_statement_text_is_handled()
    {
        // The statement being typed has no FROM yet; the one after it must not leak in.
        Assert.Empty(await CompleteAsync("SELECT c.|\nGO\nSELECT * FROM Cards c"));
        Assert.Empty(await CompleteAsync("SELECT c.|; SELECT * FROM Cards c"));

        // Text after the caret belongs to the same statement.
        var items = await CompleteAsync("SELECT 1;\nSELECT t.|\nFROM Transactions t\nWHERE t.Amount > 0");
        Assert.Equal(["TxnId", "CardId", "Amount"], items.Select(static i => i.Label));
    }

    [Fact]
    public async Task Completion_without_metadata_still_offers_keywords_and_never_throws()
    {
        Assert.Empty(await Service.GetCompletionsAsync(new SqlDocument("SELECT * FROM "), 14, TestContext.Current.CancellationToken));
        var keywords = await Service.GetCompletionsAsync(new SqlDocument("SELECT * FR"), 11, TestContext.Current.CancellationToken);
        Assert.Contains(keywords, static i => i.Label == "FROM");

        // Garbage and out-of-range offsets.
        foreach (var text in new[] { "((((", ")))) .. ,, ;", "[unclosed", "'", "/*", ". . .", "SELECT FROM WHERE JOIN ON" })
        {
            for (var offset = -1; offset <= text.Length + 1; offset++)
            {
                await Service.GetCompletionsAsync(new SqlDocument(text, new FakeCatalog()), offset, TestContext.Current.CancellationToken);
            }
        }
    }

    [Fact]
    public async Task Completion_fits_the_intellisense_budget_on_a_large_script_and_schema()
    {
        var catalog = new FakeCatalog { ExtraTables = 5_000 };
        var script = string.Concat(Enumerable.Repeat("SELECT c.Id, c.Balance FROM dbo.Cards c JOIN Transactions t ON t.CardId = c.Id WHERE c.Status = 'A';\n", 2_000));
        var sql = script + "SELECT c. FROM Cards c";
        var offset = script.Length + "SELECT c.".Length;
        var document = new SqlDocument(sql, catalog);
        await Service.GetCompletionsAsync(document, offset, TestContext.Current.CancellationToken);

        var watch = Stopwatch.StartNew();
        var columns = await Service.GetCompletionsAsync(document, offset, TestContext.Current.CancellationToken);
        var tables = await Service.GetCompletionsAsync(new SqlDocument(script + "SELECT * FROM ", catalog), script.Length + 14, TestContext.Current.CancellationToken);
        watch.Stop();

        Assert.Equal(4, columns.Count);
        Assert.Equal(400, tables.Count);

        // Budget: popup in under 100 ms with warm metadata. Two requests on a 2,000-line script must fit easily.
        Assert.True(watch.ElapsedMilliseconds < 100, $"two completions took {watch.ElapsedMilliseconds} ms");
    }

    private static async Task<List<CompletionItem>> CompleteAsync(string sqlWithCaret)
    {
        var offset = sqlWithCaret.IndexOf('|', StringComparison.Ordinal);
        var sql = sqlWithCaret.Remove(offset, 1);
        return [.. await Service.GetCompletionsAsync(new SqlDocument(sql, new FakeCatalog()), offset, TestContext.Current.CancellationToken)];
    }

    /// <summary>dbo: Cards, Müşteri İşlemleri, Transactions, CardSummary (view), PurgeOldLogs; kart: Limits; database Archive.</summary>
    private sealed class FakeCatalog : IMetadataProvider
    {
        public int ExtraTables { get; init; }

        public async IAsyncEnumerable<DatabaseObject> GetObjectsAsync(
            DatabaseObject? parent, DatabaseObjectKinds kinds, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            if (parent is { Kind: DatabaseObjectKinds.Database, Name.Name: "Archive" } && kinds.HasFlag(DatabaseObjectKinds.Schema))
            {
                yield return new(new("Archive", null, "dbo"), DatabaseObjectKinds.Schema);
                yield return new(new("Archive", null, "kart"), DatabaseObjectKinds.Schema);
                yield break;
            }

            if (parent is not { Kind: DatabaseObjectKinds.Schema })
            {
                yield break;
            }

            var (database, schema) = (parent.Name.Database, parent.Name.Name);
            IEnumerable<(string Name, DatabaseObjectKinds Kind)> objects = (database, schema.ToUpperInvariant()) switch
            {
                (null, "DBO") =>
                [
                    ("Cards", DatabaseObjectKinds.Table), ("Müşteri İşlemleri", DatabaseObjectKinds.Table), ("Transactions", DatabaseObjectKinds.Table),
                    ("CardSummary", DatabaseObjectKinds.View), ("PurgeOldLogs", DatabaseObjectKinds.Procedure),
                ],
                (null, "KART") => [("Limits", DatabaseObjectKinds.Table)],
                ("Archive", "DBO") => [("OldCards", DatabaseObjectKinds.Table)],
                _ => [],
            };
            foreach (var (name, kind) in objects)
            {
                if (kinds.HasFlag(kind))
                {
                    yield return new(new(database, schema, name), kind);
                }
            }

            for (var i = 0; i < ExtraTables && kinds.HasFlag(DatabaseObjectKinds.Table) && database is null; i++)
            {
                yield return new(new(null, schema, $"Table{i:D5}"), DatabaseObjectKinds.Table);
            }
        }

        public Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DatabaseObjectName tableOrView, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ColumnInfo>>(tableOrView.Name.ToUpperInvariant() switch
            {
                "CARDS" when tableOrView.Schema?.ToUpperInvariant() == "DBO" =>
                [
                    new("Id", "bigint", false, 1, true), new("Card No", "varchar(19)", false, 2), new("Balance", "decimal(12,2)", true, 3),
                    new("Status", "char(1)", false, 4),
                ],
                "TRANSACTIONS" => [new("TxnId", "bigint", false, 1, true), new("CardId", "bigint", false, 2), new("Amount", "decimal(12,2)", false, 3)],
                _ => [],
            });

        public Task<string?> GetDefinitionAsync(DatabaseObject target, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
