using JetQueries.Sdk;

namespace JetQueries.Provider.Oracle.Tests;

public sealed class OracleFormatTests
{
    private static readonly ISqlFormatter Formatter = (ISqlFormatter)new OracleProvider().LanguageService!;

    [Fact]
    public void Queries_are_laid_out_and_plsql_is_kept_as_written()
    {
        const string Package = "create or replace package body p as\n  procedure x is begin   null; end;\nend p;\n/";
        var sql = "select level n, mod(level, 3) m from dual where :x = 1 connect by level <= 9 order by n fetch first 3 rows only;\n" + Package;

        var formatted = Formatter.Format(sql, SqlFormatOptions.Default);

        Assert.Equal(
            "SELECT\n    level n,\n    mod(level, 3) m\nFROM\n    dual\nWHERE\n    :x = 1\nCONNECT BY level <= 9\nORDER BY\n    n\nFETCH first 3 rows only;\n\n" + Package,
            formatted);
        Assert.Equal(formatted, Formatter.Format(formatted, SqlFormatOptions.Default));
    }
}
