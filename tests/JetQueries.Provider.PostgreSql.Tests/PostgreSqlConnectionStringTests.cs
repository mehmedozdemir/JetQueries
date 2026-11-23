using JetQueries.Sdk;

namespace JetQueries.Provider.PostgreSql.Tests;

public sealed class PostgreSqlConnectionStringTests
{
    private static ParsedConnectionString Parse(string text) => new PostgreSqlProvider().ParseConnectionString(text);

    [Theory]
    [InlineData("Host=pg1;Port=5433;Database=shop;Username=app;Password=pw;SSL Mode=Require;Timeout=20;Pooling=false")]
    [InlineData("host=pg1 port=5433 dbname=shop user=app password='pw' sslmode=require connect_timeout=20")]
    [InlineData("postgresql://app:pw@pg1:5433/shop?sslmode=require&connect_timeout=20")]
    [InlineData("jdbc:postgresql://pg1:5433/shop?user=app&password=pw&ssl=true&connectTimeout=20")]
    public void Every_form_gives_the_same_settings(string text)
    {
        var parsed = Parse(text);
        Assert.Equal("pg1", parsed.Settings["host"]);
        Assert.Equal("5433", parsed.Settings["port"]);
        Assert.Equal("shop", parsed.Settings["database"]);
        Assert.Equal("require", parsed.Settings["sslMode"]);
        Assert.Equal("20", parsed.Settings["connectTimeoutSeconds"]);
        Assert.Equal("app", parsed.UserName);
        Assert.Equal("pw", parsed.Password);
        Assert.Empty(parsed.Ignored);
    }

    [Fact]
    public void Url_parts_are_decoded()
    {
        var parsed = Parse("postgres://my%40user:p%3Aw%2Fd@[::1]:6000/my%20db");
        Assert.Equal("my@user", parsed.UserName);
        Assert.Equal("p:w/d", parsed.Password);
        Assert.Equal("[::1]", parsed.Settings["host"]);
        Assert.Equal("6000", parsed.Settings["port"]);
        Assert.Equal("my db", parsed.Settings["database"]);
    }

    [Fact]
    public void Several_hosts_are_kept()
    {
        Assert.Equal("a,b", Parse("postgresql://a:5432,b:5432/db").Settings["host"]);
    }

    [Fact]
    public void A_libpq_value_may_be_quoted_with_escapes()
    {
        Assert.Equal("it's", Parse("host=h password='it\\'s'").Password);
    }

    [Fact]
    public void Unknown_keywords_are_listed()
    {
        Assert.Equal(["target session attributes"], Parse("Host=h;Target Session Attributes=primary;Application Name=x").Ignored.Select(static k => k.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Database=x")]
    [InlineData("postgresql:///db")]
    [InlineData("host=h password='open")]
    public void Not_a_connection_string_is_refused(string text)
    {
        Assert.Throws<FormatException>(() => Parse(text));
    }

    [Fact]
    public void Host_and_port_alone_are_read()
    {
        var parsed = Parse("pg.example:5444");
        Assert.Equal("pg.example", parsed.Settings["host"]);
        Assert.Equal("5444", parsed.Settings["port"]);
    }
}
