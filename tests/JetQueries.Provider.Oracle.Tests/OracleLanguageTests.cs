using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using JetQueries.Sdk;

namespace JetQueries.Provider.Oracle.Tests;

public sealed class PlSqlTokenizerTests
{
    private static readonly ISqlLanguageService Service = new OracleProvider().LanguageService!;

    [Fact]
    public void Tokens_get_their_kinds()
    {
        const string Line = "SELECT NVL(c.balance, 0), 'it''s', \"Mixed Case\", :p_id, &env FROM cards c WHERE c.note = q'[a'b]' -- note";

        var tokens = Tokens(Line, 0, out var state);

        Assert.Equal(0, state);
        Assert.Contains(("SELECT", SqlTokenKind.Keyword), tokens);
        Assert.Contains(("NVL", SqlTokenKind.Function), tokens);
        Assert.Contains(("'it''s'", SqlTokenKind.StringLiteral), tokens);
        Assert.Contains(("\"Mixed Case\"", SqlTokenKind.Identifier), tokens);
        Assert.Contains((":p_id", SqlTokenKind.Parameter), tokens);
        Assert.Contains(("&env", SqlTokenKind.Parameter), tokens);
        Assert.Contains(("q'[a'b]'", SqlTokenKind.StringLiteral), tokens);
        Assert.Equal(("-- note", SqlTokenKind.Comment), tokens[^1]);
    }

    [Fact]
    public void PlSql_words_types_and_ranges()
    {
        var tokens = Tokens("l_total PLS_INTEGER := 0; FOR i IN 1..10 LOOP l_total := l_total + i; END LOOP;", 0, out _);

        Assert.Contains(("PLS_INTEGER", SqlTokenKind.DataType), tokens);
        Assert.Contains(("FOR", SqlTokenKind.Keyword), tokens);
        Assert.Contains(("LOOP", SqlTokenKind.Keyword), tokens);
        Assert.Contains(("1", SqlTokenKind.NumberLiteral), tokens);
        Assert.Contains(("10", SqlTokenKind.NumberLiteral), tokens);
        Assert.Contains(("l_total", SqlTokenKind.Identifier), tokens);
    }

    [Fact]
    public void Comments_strings_and_q_literals_span_lines()
    {
        Tokens("x := 1; /* start", 0, out var state);
        var inComment = Tokens("still comment */ y := 2;", state, out state);
        Assert.Equal(("still comment */", SqlTokenKind.Comment), inComment[0]);
        Assert.Equal(0, state);

        Tokens("v := 'first line", 0, out state);
        var inString = Tokens("second' || x;", state, out state);
        Assert.Equal(("second'", SqlTokenKind.StringLiteral), inString[0]);
        Assert.Equal(0, state);

        Tokens("v := q'{select 'a'", 0, out state);
        var inQuoted = Tokens("from dual}' || x;", state, out state);
        Assert.Equal(("from dual}'", SqlTokenKind.StringLiteral), inQuoted[0]);
        Assert.Equal(0, state);
    }

    [Fact]
    public void Keywords_do_not_depend_on_the_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var tokens = Tokens("insert into t select initcap(x) from dual minus select 1 from dual", 0, out _);

            Assert.Equal(SqlTokenKind.Keyword, tokens[0].Kind);
            Assert.Contains(("initcap", SqlTokenKind.Function), tokens);
            Assert.Contains(("minus", SqlTokenKind.Keyword), tokens);
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

/// <summary>Completion and navigation; the caret is where the <c>|</c> is.</summary>
public sealed class OracleCompletionTests
{
    private static readonly ISqlLanguageService Service = new OracleProvider().LanguageService!;

    [Theory]
    [InlineData("SELECT * FROM |")]
    [InlineData("select * from cards c join |")]
    [InlineData("INSERT INTO |")]
    [InlineData("UPDATE |")]
    [InlineData("DELETE FROM |")]
    public async Task Tables_and_views_of_the_current_schema_are_offered(string sql)
    {
        var items = await CompleteAsync(sql);

        // Tables and views together, by name.
        Assert.Equal(["CARDS", "CARD_SUMMARY", "Müşteri İşlemleri", "TRANSACTIONS"], items.Select(static i => i.Label));
        Assert.Equal("\"Müşteri İşlemleri\"", items[2].InsertText);
        Assert.Equal("CARDS", items[0].InsertText);
        Assert.Equal(CompletionItemKind.View, items[1].Kind);
    }

    [Theory]
    [InlineData("SELECT c.| FROM cards c")]
    [InlineData("select * from Cards c where c.|")]
    [InlineData("SELECT cards.| FROM cards")]
    [InlineData("UPDATE cards c SET c.| = 1")]
    public async Task After_an_alias_its_columns_whatever_the_case_of_the_table_name(string sql)
    {
        var items = await CompleteAsync(sql);

        Assert.Equal(["ID", "CARD_NO", "BALANCE"], items.Select(static i => i.Label));
        Assert.Equal("NUMBER(18) · PK", items[0].Detail);
    }

    [Fact]
    public async Task A_quoted_table_keeps_its_case()
    {
        var items = await CompleteAsync("SELECT m.| FROM \"Müşteri İşlemleri\" m");

        Assert.Equal(["ID", "Açıklama"], items.Select(static i => i.Label));
        Assert.Equal("\"Açıklama\"", items[1].InsertText);
    }

    [Fact]
    public async Task After_a_package_its_routines_and_after_a_schema_its_objects()
    {
        var members = await CompleteAsync("BEGIN card_ops.|; END;");
        Assert.Equal(["BALANCE_OF", "CLOSE_CARD", "OPEN_CARD"], members.Select(static i => i.Label));
        Assert.Equal(CompletionItemKind.Function, members.Single(static m => m.Label == "BALANCE_OF").Kind);
        Assert.Equal(CompletionItemKind.Procedure, members.Single(static m => m.Label == "OPEN_CARD").Kind);

        var qualified = await CompleteAsync("x := app.card_ops.|");
        Assert.Equal(3, qualified.Count);

        var inSchema = await CompleteAsync("SELECT * FROM report.|");
        Assert.Equal(["DAILY_TOTALS"], inSchema.Select(static i => i.Label));
    }

    [Fact]
    public async Task Inside_a_routine_its_parameters_and_variables_come_first()
    {
        const string Body = """
            CREATE OR REPLACE PACKAGE BODY card_ops AS
              g_limit CONSTANT NUMBER := 1000;

              PROCEDURE log_it(p_text IN VARCHAR2) IS
              BEGIN
                NULL;
              END log_it;

              FUNCTION balance_of(p_card_id IN NUMBER, p_as_of DATE DEFAULT SYSDATE) RETURN NUMBER IS
                l_balance cards.balance%TYPE;
                l_count   PLS_INTEGER := 0;
              BEGIN
                SELECT c.balance INTO l_balance FROM cards c WHERE c.id = |
                RETURN l_balance;
              END balance_of;
            END card_ops;
            """;

        var items = await CompleteAsync(Body);

        var variables = items.Where(static i => i.Kind == CompletionItemKind.Variable).ToList();
        // Its own locals and parameters, the package's constant, and the package's routines (itself included).
        Assert.Equal(
            ["balance_of", "g_limit", "l_balance", "l_count", "log_it", "p_as_of", "p_card_id"],
            variables.Select(static v => v.Label).Order(StringComparer.Ordinal));
        Assert.Equal("IN NUMBER", variables.Single(static v => v.Label == "p_card_id").Detail);
        Assert.Equal("PLS_INTEGER", variables.Single(static v => v.Label == "l_count").Detail);
        Assert.Equal("CONSTANT NUMBER", variables.Single(static v => v.Label == "g_limit").Detail);
        Assert.Equal("procedure", variables.Single(static v => v.Label == "log_it").Detail);

        // Variables come before the columns of the table in the statement, which come before the alias.
        Assert.Equal(CompletionItemKind.Variable, items[0].Kind);
        Assert.Contains(items, static i => i is { Label: "BALANCE", Kind: CompletionItemKind.Column });
        Assert.True(items.FindIndex(static i => i.Kind == CompletionItemKind.Column) > items.FindLastIndex(static i => i.Kind == CompletionItemKind.Variable));

        // Another routine's locals are not visible.
        Assert.DoesNotContain(items, static i => i.Label == "p_text");
    }

    [Fact]
    public async Task Select_into_expects_variables_not_tables()
    {
        var items = await CompleteAsync("DECLARE\n  l_total NUMBER;\nBEGIN\n  SELECT COUNT(*) INTO | FROM cards;\nEND;");

        Assert.Contains(items, static i => i is { Label: "l_total", Kind: CompletionItemKind.Variable });
        Assert.DoesNotContain(items, static i => i.Kind is CompletionItemKind.Table or CompletionItemKind.View);
    }

    [Fact]
    public async Task Typing_brings_in_matching_functions_and_keywords()
    {
        var items = await CompleteAsync("SELECT c.id FROM cards c WHERE nv|");

        Assert.Equal(["NVL", "NVL2"], items.Select(static i => i.Label));

        var keywords = await CompleteAsync("BEGIN\n  EXC|");
        Assert.Contains(keywords, static i => i is { Label: "EXCEPTION", Kind: CompletionItemKind.Keyword });
    }

    [Theory]
    [InlineData("SELECT 'text with FROM |")]
    [InlineData("SELECT 1 FROM dual -- FROM |")]
    [InlineData("v := q'[SELECT c.| FROM cards c]';")]
    [InlineData("|")]
    public async Task Nothing_is_offered_in_strings_or_comments(string sql) =>
        Assert.Empty(await CompleteAsync(sql));

    [Theory]
    [InlineData("SELECT * FROM car|ds", "CARDS", DatabaseObjectKinds.Table, null)]
    [InlineData("SELECT c.bal|ance FROM cards c", "CARDS", DatabaseObjectKinds.Table, "BALANCE")]
    [InlineData("BEGIN card_ops.open_c|ard(1); END;", "CARD_OPS", DatabaseObjectKinds.Package, "OPEN_CARD")]
    [InlineData("BEGIN card|_ops.open_card(1); END;", "CARD_OPS", DatabaseObjectKinds.Package, null)]
    [InlineData("x := app.card_ops.balance|_of(1);", "CARD_OPS", DatabaseObjectKinds.Package, "BALANCE_OF")]
    [InlineData("SELECT * FROM report.daily_tot|als", "DAILY_TOTALS", DatabaseObjectKinds.Table, null)]
    [InlineData("SELECT * FROM \"Müşteri İşlem|leri\"", "Müşteri İşlemleri", DatabaseObjectKinds.Table, null)]
    [InlineData("BEGIN purge_old|_logs; END;", "PURGE_OLD_LOGS", DatabaseObjectKinds.Procedure, null)]
    public async Task A_name_leads_to_its_object(string sql, string expectedName, DatabaseObjectKinds expectedKind, string? expectedMember)
    {
        var offset = sql.IndexOf('|', StringComparison.Ordinal);
        var definition = await Service.GetDefinitionAsync(new SqlDocument(sql.Remove(offset, 1), new OracleCatalog()), offset, TestContext.Current.CancellationToken);

        Assert.NotNull(definition);
        Assert.Equal(expectedName, definition.Target!.Name.Name);
        Assert.Equal(expectedKind, definition.Target.Kind);
        Assert.Equal(expectedMember, definition.MemberName);
    }

    [Fact]
    public async Task Unbalanced_structure_is_reported_and_valid_code_is_not()
    {
        const string Valid = "CREATE OR REPLACE PROCEDURE p IS\nBEGIN\n  IF 1 = 1 THEN\n    NULL;\n  END IF;\nEND p;\n/\nSELECT 1 FROM dual;";
        Assert.Empty(await Service.GetDiagnosticsAsync(new SqlDocument(Valid), TestContext.Current.CancellationToken));

        const string Broken = "CREATE OR REPLACE PROCEDURE p IS\nBEGIN\n  IF 1 = 1 THEN\n    NULL;\nEND p;\n/";
        var diagnostic = Assert.Single(await Service.GetDiagnosticsAsync(new SqlDocument(Broken), TestContext.Current.CancellationToken));
        Assert.Equal("END IF expected", diagnostic.Message);
        Assert.Equal("END", Broken.Substring(diagnostic.Span.Start, diagnostic.Span.Length));

        // A wrong expression is not this service's business: Oracle reports it on compile.
        Assert.Empty(await Service.GetDiagnosticsAsync(new SqlDocument("BEGIN\n  x := := 1 +;\nEND;"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Completion_and_checking_fit_their_budgets_on_a_large_package()
    {
        var routine = string.Concat(Enumerable.Range(0, 400).Select(static i =>
            $"  PROCEDURE step_{i}(p_id IN NUMBER) IS\n    l_n NUMBER := 0;\n  BEGIN\n    UPDATE cards c SET c.balance = c.balance + l_n WHERE c.id = p_id;\n    IF l_n > 0 THEN\n      NULL;\n    END IF;\n  END step_{i};\n\n"));
        var text = "CREATE OR REPLACE PACKAGE BODY big AS\n" + routine + "  PROCEDURE last_one(p_id IN NUMBER) IS\n  BEGIN\n    SELECT c. FROM cards c;\n  END last_one;\nEND big;\n";
        var offset = text.LastIndexOf("SELECT c.", StringComparison.Ordinal) + "SELECT c.".Length;
        var document = new SqlDocument(text, new OracleCatalog());
        await Service.GetCompletionsAsync(document, offset, TestContext.Current.CancellationToken);

        var watch = Stopwatch.StartNew();
        var columns = await Service.GetCompletionsAsync(document, offset, TestContext.Current.CancellationToken);
        var completionMs = watch.ElapsedMilliseconds;
        watch.Restart();
        var diagnostics = await Service.GetDiagnosticsAsync(document, TestContext.Current.CancellationToken);
        var checkMs = watch.ElapsedMilliseconds;

        Assert.Equal(3, columns.Count);
        Assert.Empty(diagnostics);

        // About 3,600 lines. Budget: completion under 100 ms with warm metadata.
        Assert.True(completionMs < 100, $"completion took {completionMs} ms");
        Assert.True(checkMs < 200, $"structure check took {checkMs} ms");
    }

    private static async Task<List<CompletionItem>> CompleteAsync(string sqlWithCaret)
    {
        var offset = sqlWithCaret.IndexOf('|', StringComparison.Ordinal);
        var sql = sqlWithCaret.Remove(offset, 1).ReplaceLineEndings("\n");
        offset = sqlWithCaret.ReplaceLineEndings("\n").IndexOf('|', StringComparison.Ordinal);
        return [.. await Service.GetCompletionsAsync(new SqlDocument(sql, new OracleCatalog()), offset, TestContext.Current.CancellationToken)];
    }
}

/// <summary>
/// Current schema APP: CARDS, "Müşteri İşlemleri", TRANSACTIONS, view CARD_SUMMARY, package CARD_OPS, procedure
/// PURGE_OLD_LOGS; schema REPORT: DAILY_TOTALS. Names as Oracle's dictionary stores them.
/// </summary>
internal sealed class OracleCatalog : IMetadataProvider
{
    public async IAsyncEnumerable<DatabaseObject> GetObjectsAsync(
        DatabaseObject? parent, DatabaseObjectKinds kinds, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        if (parent is { Kind: DatabaseObjectKinds.Package, Name.Name: "CARD_OPS" })
        {
            foreach (var (name, kind) in new[]
                     {
                         ("OPEN_CARD", DatabaseObjectKinds.Procedure), ("CLOSE_CARD", DatabaseObjectKinds.Procedure), ("BALANCE_OF", DatabaseObjectKinds.Function),
                     })
            {
                if (kinds.HasFlag(kind))
                {
                    yield return new(new(null, "APP", name), kind);
                }
            }

            yield break;
        }

        // The root with object kinds, or schema APP, is the current schema.
        var schema = parent is null ? "APP" : parent.Kind == DatabaseObjectKinds.Schema ? parent.Name.Name : null;
        (string Name, DatabaseObjectKinds Kind)[] objects = schema switch
        {
            "APP" =>
            [
                ("CARDS", DatabaseObjectKinds.Table), ("Müşteri İşlemleri", DatabaseObjectKinds.Table), ("TRANSACTIONS", DatabaseObjectKinds.Table),
                ("CARD_SUMMARY", DatabaseObjectKinds.View), ("CARD_OPS", DatabaseObjectKinds.Package), ("PURGE_OLD_LOGS", DatabaseObjectKinds.Procedure),
            ],
            "REPORT" => [("DAILY_TOTALS", DatabaseObjectKinds.Table)],
            _ => [],
        };
        foreach (var (name, kind) in objects)
        {
            if (kinds.HasFlag(kind))
            {
                yield return new(new(null, schema, name), kind);
            }
        }
    }

    public Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DatabaseObjectName tableOrView, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ColumnInfo>>(tableOrView.Name switch
        {
            "CARDS" when tableOrView.Schema is null or "APP" =>
                [new("ID", "NUMBER(18)", false, 1, true), new("CARD_NO", "VARCHAR2(19)", false, 2), new("BALANCE", "NUMBER(12,2)", true, 3)],
            "Müşteri İşlemleri" => [new("ID", "NUMBER", false, 1, true), new("Açıklama", "VARCHAR2(200 CHAR)", true, 2)],
            _ => [],
        });

    public Task<string?> GetDefinitionAsync(DatabaseObject target, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
