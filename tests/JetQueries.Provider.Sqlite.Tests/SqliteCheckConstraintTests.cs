namespace JetQueries.Provider.Sqlite.Tests;

/// <summary>CHECK constraints read from a CREATE TABLE text.</summary>
public sealed class SqliteCheckConstraintTests
{
    [Fact]
    public void Column_and_table_checks_are_found_with_their_names()
    {
        const string Sql = """
            CREATE TABLE "Limits" (
                id INTEGER PRIMARY KEY, -- a comment with CHECK (x)
                amount NUMERIC(12, 2) NOT NULL CONSTRAINT ck_amount CHECK (amount >= 0),
                "kind" TEXT DEFAULT 'a,b' CHECK ("kind" IN ('a', 'b', 'it''s (odd)')),
                [note] TEXT /* CHECK (no) */,
                CONSTRAINT "ck pair" CHECK (amount < 1000 OR "kind" = 'b'),
                CHECK(length([note]) < 200),
                UNIQUE (amount, "kind")
            )
            """;

        var checks = SqliteCheckConstraints.Parse(Sql);

        Assert.Equal(
            [
                new SqliteCheck("ck_amount", "amount", "amount >= 0"),
                new SqliteCheck(null, "kind", "\"kind\" IN ('a', 'b', 'it''s (odd)')"),
                new SqliteCheck("ck pair", null, "amount < 1000 OR \"kind\" = 'b'"),
                new SqliteCheck(null, null, "length([note]) < 200"),
            ],
            checks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CREATE TABLE t (id INTEGER)")]
    [InlineData("CREATE TABLE t (id INTEGER CHECK")]
    public void A_table_without_checks_or_broken_text_gives_none(string? sql) =>
        Assert.Empty(SqliteCheckConstraints.Parse(sql));
}
