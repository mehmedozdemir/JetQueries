using JetQueries.Sdk;

namespace JetQueries.Provider.Oracle.Tests;

public sealed class OracleConnectionStringTests
{
    private static ParsedConnectionString Parse(string text) => new OracleProvider().ParseConnectionString(text);

    [Theory]
    [InlineData("User Id=hr;Password=pw;Data Source=db01:1522/ORCLPDB1", "1522")]
    [InlineData("User Id=hr;Password=pw;Data Source=//db01:1522/ORCLPDB1", "1522")]
    [InlineData("User Id=hr;Password=pw;Data Source=db01/ORCLPDB1", null)]
    [InlineData("User Id=hr;Password=pw;Data Source=db01:1522/ORCLPDB1:dedicated", "1522")]
    [InlineData("User Id=hr;Password=pw;Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=db01)(PORT=1522))(CONNECT_DATA=(SERVICE_NAME=ORCLPDB1)))", "1522")]
    [InlineData("jdbc:oracle:thin:hr/pw@//db01:1522/ORCLPDB1", "1522")]
    [InlineData("hr/pw@db01:1522/ORCLPDB1", "1522")]
    public void Service_names_are_read(string text, string? port)
    {
        var parsed = Parse(text);
        Assert.Equal("db01", parsed.Settings["host"]);
        Assert.Equal(port, parsed.Settings.GetValueOrDefault("port"));
        Assert.Equal("ORCLPDB1", parsed.Settings["serviceName"]);
        Assert.False(parsed.Settings.ContainsKey("sid"));
        Assert.Equal("hr", parsed.UserName);
        Assert.Equal("pw", parsed.Password);
    }

    [Theory]
    [InlineData("jdbc:oracle:thin:@db01:1521:XE")]
    [InlineData("Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=db01)(PORT=1521))(CONNECT_DATA=(SID=XE)))")]
    public void A_sid_is_read(string text)
    {
        var parsed = Parse(text);
        Assert.Equal("XE", parsed.Settings["sid"]);
        Assert.False(parsed.Settings.ContainsKey("serviceName"));
        Assert.Null(parsed.UserName);
    }

    [Fact]
    public void Role_and_timeout_are_taken_and_pool_settings_skipped()
    {
        var parsed = Parse("User Id=sys;Password=pw;Data Source=db:1521/FREE;DBA Privilege=SYSDBA;Connection Timeout=40;Pooling=false;Wallet_Location=x");
        Assert.Equal("sysdba", parsed.Settings["role"]);
        Assert.Equal("40", parsed.Settings["connectTimeoutSeconds"]);
        Assert.Equal(["wallet_location"], parsed.Ignored.Select(static k => k.ToLowerInvariant()));
    }

    [Fact]
    public void A_password_with_an_at_sign_keeps_it()
    {
        Assert.Equal("p@w", Parse("hr/p@w@db:1521/FREE").Password);
    }

    [Theory]
    [InlineData("User Id=hr;Password=pw;Data Source=PRODDB")]
    [InlineData("PRODDB")]
    [InlineData("User Id=hr;Password=pw")]
    public void Tns_aliases_and_missing_addresses_are_refused(string text)
    {
        var error = Assert.Throws<FormatException>(() => Parse(text));
        Assert.DoesNotContain("pw", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_settings_give_the_endpoint()
    {
        Assert.Equal(new NetworkEndpoint("db01", 1522), new OracleProvider().GetEndpoint(Parse("db01:1522/X").Settings));
    }
}
