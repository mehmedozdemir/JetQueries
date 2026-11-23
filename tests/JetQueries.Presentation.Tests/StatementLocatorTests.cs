using JetQueries.Provider.Oracle;
using JetQueries.Provider.SqlServer;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>Ctrl+Enter runs the statement at the caret: where statements begin and end, per dialect.</summary>
public sealed class StatementLocatorTests
{
    private static readonly ISqlLanguageService TSql = new SqlServerProvider().LanguageService!;
    private static readonly ISqlLanguageService PlSql = new OracleProvider().LanguageService!;

    // The caret is |; the statement expected is between « and ».
    [Theory]
    [InlineData("«SELECT 1;»|\nSELECT 2;")]
    [InlineData("SELECT 1;\n«SELECT |2;»")]
    [InlineData("«SELECT a\nFROM t\nWHERE b = 'x;y' -- ; here\n  AND c = 1;|»\nSELECT 2")]
    [InlineData("«SELECT a FROM t»\n|\nSELECT b FROM u")]
    [InlineData("SELECT a FROM t\n\n«SELECT b FROM u|»")]
    [InlineData("SELECT 1\nGO\n«UPDATE t SET a = 1\nWHERE id = 2|»\nGO")]
    [InlineData("«BEGIN TRAN;»|\nUPDATE t SET a = 1;")]
    [InlineData("«IF @x = 1\nBEGIN\n  UPDATE t SET a = 1;\n  DELETE FROM u;|\nEND;»\nSELECT 2;")]
    [InlineData("«CREATE PROCEDURE dbo.p AS\nBEGIN\n  SELECT 1;\n\n  SELECT 2;|\nEND»\nGO\nSELECT 3")]
    [InlineData("«DECLARE @x int = 1;\nSELECT @x;|»\nGO")]
    [InlineData("SELECT 1;\n«SELECT [x;y], \"q;r\" FROM [BEGIN];|»")]
    [InlineData("«SELECT CASE WHEN a = 1 THEN 'x;' ELSE 'y' END AS k FROM t;|»")]
    public void The_statement_at_the_caret_is_found_in_tsql(string marked) => Check(TSql, marked);

    [Theory]
    [InlineData("SELECT * FROM dual;\n«SELECT |1 FROM dual;»")]
    [InlineData("SELECT * FROM dual;\n«BEGIN\n  UPDATE t SET a = 1;\n  IF x THEN NULL; END IF;\n  LOOP EXIT; END LOOP;|\nEND;»\n/\nSELECT 2 FROM dual;")]
    [InlineData("«CREATE OR REPLACE PACKAGE p AS\n  PROCEDURE x;|\n  FUNCTION y RETURN NUMBER;\nEND p;»\n/\nSELECT 1 FROM dual;")]
    [InlineData("SELECT 1 FROM dual;\n«SELECT q'[a;b]' FROM t;|»")]
    public void The_statement_at_the_caret_is_found_in_plsql(string marked) => Check(PlSql, marked);

    [Fact]
    public async Task Ctrl_enter_runs_the_statement_at_the_caret_or_the_selection()
    {
        await using var env = new TestEnvironment();
        env.Provider.LanguageService = TSql;
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var query = env.Query();
        query.Text = "SELECT 1;\nUPDATE dbo.Cards SET Balance = 0\nWHERE Id = 2;\n\nSELECT 3";

        await query.RunStatementCommand.ExecuteAsync("SELECT 1;\nUPDATE".Length);
        Assert.Equal("UPDATE dbo.Cards SET Balance = 0\nWHERE Id = 2;", env.Provider.Executed[^1].Sql);

        await query.RunStatementCommand.ExecuteAsync(query.Text.Length);
        Assert.Equal("SELECT 3", env.Provider.Executed[^1].Sql);

        // A selection wins over the caret.
        query.SelectedText = "SELECT 1";
        await query.RunStatementCommand.ExecuteAsync(query.Text.Length);
        Assert.Equal("SELECT 1", env.Provider.Executed[^1].Sql);

        // Nothing to run: it says so and sends nothing.
        query.SelectedText = string.Empty;
        query.Text = "SELECT 1\n\n\n";
        var count = env.Provider.Executed.Count;
        query.Text = "   \n-- only a comment\n";
        await query.RunStatementCommand.ExecuteAsync(0);
        Assert.Equal("No statement at the cursor.", query.Status);
        Assert.Equal(count, env.Provider.Executed.Count);
    }

    [Fact]
    public void A_text_of_blank_lines_has_no_statement() =>
        Assert.Null(StatementLocator.Find("  \n\n  \n", 3, TSql));

    private static void Check(ISqlLanguageService language, string marked)
    {
        var start = marked.IndexOf('«', StringComparison.Ordinal);
        var withoutStart = marked.Remove(start, 1);
        var end = withoutStart.IndexOf('»', StringComparison.Ordinal);
        var withoutMarks = withoutStart.Remove(end, 1);
        var caret = withoutMarks.IndexOf('|', StringComparison.Ordinal);
        var text = withoutMarks.Remove(caret, 1);
        var expected = withoutStart[start..end].Replace("|", string.Empty, StringComparison.Ordinal);

        var span = StatementLocator.Find(text, caret, language);

        Assert.NotNull(span);
        Assert.Equal(expected, text.Substring(span.Value.Start, span.Value.Length));
    }
}
