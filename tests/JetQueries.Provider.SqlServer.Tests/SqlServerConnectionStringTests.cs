using JetQueries.Sdk;

namespace JetQueries.Provider.SqlServer.Tests;

public sealed class SqlServerConnectionStringTests
{
    private static ParsedConnectionString Parse(string text) => new SqlServerProvider().ParseConnectionString(text);

    [Fact]
    public void A_SqlClient_string_gives_settings_and_the_credential()
    {
        var parsed = Parse("Server=tcp:db01,1444;Initial Catalog=Sales;User ID=app;Password=p@ss;Encrypt=False;TrustServerCertificate=True;Application Name=X;Connect Timeout=30");

        Assert.Equal("tcp:db01,1444", parsed.Settings["server"]);
        Assert.Equal("Sales", parsed.Settings["database"]);
        Assert.Equal("sql", parsed.Settings["authentication"]);
        Assert.Equal("false", parsed.Settings["encrypt"]);
        Assert.Equal("true", parsed.Settings["trustServerCertificate"]);
        Assert.Equal("30", parsed.Settings["connectTimeoutSeconds"]);
        Assert.Equal("app", parsed.UserName);
        Assert.Equal("p@ss", parsed.Password);
        Assert.Empty(parsed.Ignored);
    }

    [Fact]
    public void A_quoted_password_keeps_its_semicolon()
    {
        var parsed = Parse("Data Source=.\\SQLEXPRESS;Database=x;uid=sa;pwd=\"a;b\"");
        Assert.Equal(".\\SQLEXPRESS", parsed.Settings["server"]);
        Assert.Equal("a;b", parsed.Password);
        Assert.Empty(parsed.Ignored);
    }

    [Theory]
    [InlineData("Integrated Security=SSPI")]
    [InlineData("Trusted_Connection=yes")]
    [InlineData("Authentication=Active Directory Integrated")]
    public void Windows_authentication_drops_any_user(string auth)
    {
        var parsed = Parse("Server=db;" + auth + ";User Id=u;Password=p");
        Assert.Equal("windows", parsed.Settings["authentication"]);
        Assert.Null(parsed.UserName);
        Assert.Null(parsed.Password);
    }

    [Fact]
    public void Unknown_and_unsupported_keywords_are_listed()
    {
        var parsed = Parse("Server=db;Authentication=Active Directory Interactive;Column Encryption Setting=Enabled;Pooling=false");
        Assert.Equal(["authentication", "column encryption setting"], parsed.Ignored.Select(static k => k.ToLowerInvariant()));
    }

    [Fact]
    public void ApplicationIntent_maps_to_read_only_routing()
    {
        Assert.Equal("true", Parse("Server=db;ApplicationIntent=ReadOnly").Settings["readOnlyIntent"]);
    }

    [Fact]
    public void A_bare_server_name_is_the_server()
    {
        Assert.Equal("db01\\INST", Parse("db01\\INST").Settings["server"]);
    }

    [Theory]
    [InlineData("jdbc:sqlserver://db01:1500;databaseName=Sales;user=app;password=pw;encrypt=true;trustServerCertificate=true", "db01,1500")]
    [InlineData("jdbc:sqlserver://db01;databaseName=Sales;user=app;password=pw;encrypt=true;trustServerCertificate=true", "db01")]
    [InlineData("jdbc:sqlserver://db01:1433;databaseName=Sales;user=app;password=pw;encrypt=true;trustServerCertificate=true", "db01")]
    [InlineData("jdbc:sqlserver://db01\\INST:1500;databaseName=Sales;user=app;password=pw;encrypt=true;trustServerCertificate=true", "db01\\INST,1500")]
    [InlineData("jdbc:sqlserver://;serverName=db01;instanceName=INST;databaseName=Sales;user=app;password=pw;encrypt=true;trustServerCertificate=true", "db01\\INST")]
    public void A_jdbc_url_is_read(string url, string server)
    {
        var parsed = Parse(url);
        Assert.Equal(server, parsed.Settings["server"]);
        Assert.Equal("Sales", parsed.Settings["database"]);
        Assert.Equal("true", parsed.Settings["encrypt"]);
        Assert.Equal("true", parsed.Settings["trustServerCertificate"]);
        Assert.Equal("app", parsed.UserName);
        Assert.Equal("pw", parsed.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Database=x;User Id=u")]
    [InlineData("Server='db")]
    public void Not_a_connection_string_is_refused(string text)
    {
        Assert.Throws<FormatException>(() => Parse(text));
    }

    [Fact]
    public void The_message_of_a_refusal_never_repeats_the_password()
    {
        var error = Assert.Throws<FormatException>(() => Parse("Database=x;Password=TopSecret1"));
        Assert.DoesNotContain("TopSecret1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_settings_give_the_endpoint()
    {
        var parsed = Parse("Server=db,1500;Database=Sales;User Id=app;Password=pw;Encrypt=strict");
        Assert.Equal("strict", parsed.Settings["encrypt"]);
        Assert.Equal(new NetworkEndpoint("db", 1500), new SqlServerProvider().GetEndpoint(parsed.Settings));
    }
}
