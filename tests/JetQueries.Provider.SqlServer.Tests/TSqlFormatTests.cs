using JetQueries.Sdk;

namespace JetQueries.Provider.SqlServer.Tests;

public sealed class TSqlFormatTests
{
    private static readonly ISqlFormatter Formatter = (ISqlFormatter)new SqlServerProvider().LanguageService!;

    private static string Format(string sql) => Formatter.Format(sql, SqlFormatOptions.Default);

    [Fact]
    public void A_query_gets_one_clause_per_line_and_its_content_indented()
    {
        var sql = "-- Bu yıl verilen kartlar\nselect top (100) c.Id, c.CardNo,c.Balance from dbo.Cards c inner join dbo.Customers u on u.Id = c.CustomerId and u.IsActive = 1 " +
            "where c.IssuedAt >= '2026-01-01' and c.Status = @status or c.Balance between 10 and 20 order by c.Id desc; /* ilk 100 */";

        Assert.Equal(
            """
            -- Bu yıl verilen kartlar
            SELECT TOP (100)
                c.Id,
                c.CardNo,
                c.Balance
            FROM
                dbo.Cards c
            INNER JOIN dbo.Customers u ON u.Id = c.CustomerId
                AND u.IsActive = 1
            WHERE
                c.IssuedAt >= '2026-01-01'
                AND c.Status = @status
                OR c.Balance BETWEEN 10 AND 20
            ORDER BY
                c.Id DESC; /* ilk 100 */
            """.ReplaceLineEndings("\n"),
            Format(sql));
    }

    [Fact]
    public void Subqueries_ctes_and_case_are_indented()
    {
        var sql = "with recent as (select Id, Balance from dbo.Cards where IssuedAt > '2026-01-01') " +
            "select Id, case when Balance > 100 then 'high' when Balance is null then 'none' else 'low' end as Band from recent " +
            "where Id in (select CardId from dbo.Payments group by CardId having count(*) > 2)";

        Assert.Equal(
            """
            WITH
                recent AS (
                    SELECT
                        Id,
                        Balance
                    FROM
                        dbo.Cards
                    WHERE
                        IssuedAt > '2026-01-01'
                )
            SELECT
                Id,
                CASE
                    WHEN Balance > 100 THEN 'high'
                    WHEN Balance IS NULL THEN 'none'
                    ELSE 'low'
                END AS Band
            FROM
                recent
            WHERE
                Id IN (
                    SELECT
                        CardId
                    FROM
                        dbo.Payments
                    GROUP BY
                        CardId
                    HAVING
                        count(*) > 2
                )
            """.ReplaceLineEndings("\n"),
            Format(sql));
    }

    [Fact]
    public void Dml_statements_and_batches_are_laid_out_and_separated()
    {
        var sql = "insert into dbo.Log (Id, Note) values (1, 'a'), (2, 'b')\nupdate dbo.Cards set Balance = 0, Status = 2 where Id = 42;\nGO\n" +
            "delete from dbo.Log where Id = 1";

        Assert.Equal(
            """
            INSERT INTO
                dbo.Log (Id, Note)
            VALUES
                (1, 'a'),
                (2, 'b')
            UPDATE
                dbo.Cards
            SET
                Balance = 0,
                Status = 2
            WHERE
                Id = 42;
            GO

            DELETE FROM
                dbo.Log
            WHERE
                Id = 1
            """.ReplaceLineEndings("\n"),
            Format(sql));
    }

    [Theory]
    [InlineData("CREATE PROCEDURE dbo.P AS\nbegin\n  select  1   from t where a=1;\n    -- keep me\nend")]
    [InlineData("exec   dbo.P   @a = 1")]
    [InlineData("declare @x int = 1;\nif @x = 1 select   'a'")]
    [InlineData("set nocount on;")]
    public void Procedural_code_and_other_statements_stay_as_written(string sql) =>
        Assert.Equal(sql, Format(sql));

    [Theory]
    [InlineData("select 'a  --  b', N'İ   ı', [order  by], \"x\" from t -- end comment\nwhere a = 'it''s' and b like '%;%'")]
    [InlineData("select '\nline two\n  three' as x, /* a\n   block */ y from t")]
    [InlineData("select a from t where b = 1\r\norder by a\r\n")]
    [InlineData("select t.[from], t.[select], x.order_id from t join x on x.id=t.id")]
    [InlineData("SELECT 1 SELECT 2")]
    [InlineData("select a,\n  -- why\n  b from t")]
    public void Strings_comments_and_identifiers_are_kept_and_a_second_pass_changes_nothing(string sql)
    {
        var once = Format(sql);
        Assert.Equal(once, Format(once));
        Assert.Equal(Tokens(sql), Tokens(once));
        if (sql.Contains("\r\n", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("\n", once.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Keywords_can_keep_their_case()
    {
        var formatted = Formatter.Format("select a from t where b = 1", new SqlFormatOptions(UppercaseKeywords: false, IndentSize: 2));
        Assert.Equal("select\n  a\nfrom\n  t\nwhere\n  b = 1", formatted);
    }

    private static List<string> Tokens(string sql)
    {
        var service = new SqlServerProvider().LanguageService!;
        var list = new List<string>();
        var state = 0;
        foreach (var line in sql.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var tokens = service.TokenizeLine(line, state);
            state = tokens.EndState;
            list.AddRange(tokens.Tokens.Select(t => line.Substring(t.Span.Start, t.Span.Length).ToUpperInvariant()));
        }

        return list;
    }
}
