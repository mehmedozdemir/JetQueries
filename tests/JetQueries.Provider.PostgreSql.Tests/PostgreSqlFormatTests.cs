using JetQueries.Sdk;

namespace JetQueries.Provider.PostgreSql.Tests;

public sealed class PostgreSqlFormatTests
{
    private static readonly ISqlFormatter Formatter = (ISqlFormatter)new PostgreSqlProvider().LanguageService!;

    [Fact]
    public void Queries_are_laid_out_and_function_bodies_are_kept_as_written()
    {
        const string Block = "do $$\nbegin\n  perform  1;\nend $$;";
        var sql = "select c.id, c.tags::text, $1 as p from kart.cards c left join kart.x x using (id) where c.\"Balance\" > 0 limit 10 offset 5;\n" + Block;

        var formatted = Formatter.Format(sql, SqlFormatOptions.Default);

        Assert.Equal(
            "SELECT\n    c.id,\n    c.tags::text,\n    $1 AS p\nFROM\n    kart.cards c\nLEFT JOIN kart.x x USING (id)\nWHERE\n    c.\"Balance\" > 0\nLIMIT 10\nOFFSET 5;\n\n" + Block,
            formatted);
        Assert.Equal(formatted, Formatter.Format(formatted, SqlFormatOptions.Default));
    }
}
