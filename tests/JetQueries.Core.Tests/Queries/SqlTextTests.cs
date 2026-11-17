using System.Globalization;
using JetQueries.Core.Queries;
using JetQueries.Core.Sql;
using JetQueries.Sdk;

namespace JetQueries.Core.Tests.Queries;

public sealed class BasicSqlLanguageServiceTests
{
    [Fact]
    public void A_line_is_split_into_kinds()
    {
        const string Line = "SELECT c.[Müşteri Adı], N'O''Brien', 42.5 FROM dbo.cards c WHERE id = @id -- note";

        var tokens = Tokenize(Line, out var state);

        Assert.Equal(BasicSqlLanguageService.StateNormal, state);
        Assert.Equal(
            [
                ("SELECT", SqlTokenKind.Keyword), ("c", SqlTokenKind.Identifier), (".", SqlTokenKind.Punctuation),
                ("[Müşteri Adı]", SqlTokenKind.Identifier), (",", SqlTokenKind.Punctuation), ("N'O''Brien'", SqlTokenKind.StringLiteral),
                (",", SqlTokenKind.Punctuation), ("42.5", SqlTokenKind.NumberLiteral), ("FROM", SqlTokenKind.Keyword),
                ("dbo", SqlTokenKind.Identifier), (".", SqlTokenKind.Punctuation), ("cards", SqlTokenKind.Identifier),
                ("c", SqlTokenKind.Identifier), ("WHERE", SqlTokenKind.Keyword), ("id", SqlTokenKind.Identifier),
                ("=", SqlTokenKind.Operator), ("@id", SqlTokenKind.Parameter), ("-- note", SqlTokenKind.Comment),
            ],
            tokens);
    }

    [Fact]
    public void Block_comments_carry_over_lines_through_the_state()
    {
        var first = Tokenize("select 1 /* start", out var state);
        Assert.Equal(BasicSqlLanguageService.StateBlockComment, state);
        Assert.Equal(("/* start", SqlTokenKind.Comment), first[^1]);

        var middle = BasicSqlLanguageService.Instance.TokenizeLine("still a comment select", state);
        Assert.Equal(SqlTokenKind.Comment, Assert.Single(middle.Tokens).Kind);
        Assert.Equal(BasicSqlLanguageService.StateBlockComment, middle.EndState);

        var last = BasicSqlLanguageService.Instance.TokenizeLine("end */ select 2", middle.EndState);
        Assert.Equal([SqlTokenKind.Comment, SqlTokenKind.Keyword, SqlTokenKind.NumberLiteral], last.Tokens.Select(static t => t.Kind));
        Assert.Equal(BasicSqlLanguageService.StateNormal, last.EndState);
    }

    [Fact]
    public void Keywords_are_recognized_under_the_turkish_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var tokens = Tokenize("insert into t (id) values (1); UNION union", out _);

            // "insert".ToUpper() is "İNSERT" in Turkish; the lookup must not depend on that.
            Assert.Equal(SqlTokenKind.Keyword, tokens[0].Kind);
            Assert.Equal(SqlTokenKind.Keyword, tokens[1].Kind);
            Assert.All(tokens.TakeLast(2), static t => Assert.Equal(SqlTokenKind.Keyword, t.Kind));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void An_unfinished_string_ends_with_its_line()
    {
        var tokens = Tokenize("select 'oops", out var state);

        Assert.Equal(("'oops", SqlTokenKind.StringLiteral), tokens[^1]);
        Assert.Equal(BasicSqlLanguageService.StateNormal, state);
    }

    private static List<(string Text, SqlTokenKind Kind)> Tokenize(string line, out int endState)
    {
        var result = BasicSqlLanguageService.Instance.TokenizeLine(line, BasicSqlLanguageService.StateNormal);
        endState = result.EndState;
        return [.. result.Tokens.Select(t => (line.Substring(t.Span.Start, t.Span.Length), t.Kind))];
    }
}

public sealed class StatementGuardTests
{
    [Theory]
    [InlineData("SELECT * FROM cards WHERE id = 1")]
    [InlineData("select 'DROP TABLE x; delete from y' as text -- update z")]
    [InlineData("/* insert into t */ SELECT [update], \"delete\" FROM t")]
    [InlineData("WITH x AS (SELECT 1 AS a) SELECT a FROM x ORDER BY a")]
    [InlineData("")]
    public void Reading_is_not_writing(string sql)
    {
        var risk = StatementGuard.Analyze(sql);

        Assert.False(risk.WritesData);
        Assert.Empty(risk.DestructiveActions);
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("update t set a = 1 where id = 2")]
    [InlineData("DELETE t WHERE id = 2")]
    [InlineData("MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN UPDATE SET a = 1")]
    [InlineData("CREATE TABLE t (id int)")]
    [InlineData("alter table t add c int")]
    [InlineData("EXEC dbo.PurgeOldLogs")]
    [InlineData("execute immediate 'x'")]
    [InlineData("SELECT * INTO backup_t FROM t")]
    [InlineData("GRANT SELECT ON t TO app")]
    [InlineData("select 1;\ninsert into t values (1)")]
    public void Anything_that_can_change_something_counts_as_writing(string sql) =>
        Assert.True(StatementGuard.Analyze(sql).WritesData);

    [Theory]
    [InlineData("DROP TABLE cards", "DROP")]
    [InlineData("truncate table cards", "TRUNCATE")]
    [InlineData("UPDATE cards SET status = 'C'", "UPDATE without WHERE")]
    [InlineData("DELETE FROM cards", "DELETE without WHERE")]
    [InlineData("delete from cards; select 1 where 1 = 1", "DELETE without WHERE")]
    [InlineData("UPDATE cards SET a = (SELECT max(b) FROM x WHERE x.id = 1)", "UPDATE without WHERE")]
    [InlineData("DELETE FROM a\nSELECT * FROM b WHERE 1 = 1", "DELETE without WHERE")]
    public void Destructive_statements_are_named(string sql, string expected) =>
        Assert.Equal([expected], StatementGuard.Analyze(sql).DestructiveActions);

    [Theory]
    [InlineData("UPDATE cards SET status = 'C' WHERE id = 1")]
    [InlineData("DELETE FROM cards\nWHERE id IN (SELECT id FROM old)")]
    [InlineData("UPDATE c SET a = 1 FROM cards c JOIN x ON x.id = c.id WHERE x.flag = 1")]
    [InlineData("SELECT * FROM cards FOR UPDATE")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT fk FOREIGN KEY (a) REFERENCES p (a) ON DELETE CASCADE ON UPDATE CASCADE")]
    [InlineData("MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN UPDATE SET a = 1 WHEN NOT MATCHED BY SOURCE THEN DELETE")]
    [InlineData("INSERT INTO t SELECT * FROM s")]
    public void Statements_with_a_where_or_that_only_mention_update_and_delete_are_not_flagged(string sql) =>
        Assert.Empty(StatementGuard.Analyze(sql).DestructiveActions);

    [Fact]
    public void Several_problems_are_each_reported_once()
    {
        var risk = StatementGuard.Analyze("DROP TABLE a; DROP TABLE b; DELETE FROM c; UPDATE d SET x = 1; TRUNCATE TABLE e");

        Assert.Equal(["DROP", "DELETE without WHERE", "UPDATE without WHERE", "TRUNCATE"], risk.DestructiveActions);
    }
}
