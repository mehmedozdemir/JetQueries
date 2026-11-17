using System.Reflection;
using System.Runtime.Loader;
using JetQueries.Core.Extensions;
using JetQueries.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;

namespace JetQueries.Provider.Oracle.Tests;

/// <summary>Everything that can be checked without a database.</summary>
public sealed class OracleProviderTests
{
    private static readonly ConnectionCredential Credential = new("app_user", "s3cret");

    [Theory]
    [InlineData("CARDS", "\"CARDS\"")]
    [InlineData("Müşteri İşlemleri", "\"Müşteri İşlemleri\"")]
    [InlineData("x'; DROP TABLE t; --", "\"x'; DROP TABLE t; --\"")]
    public void Identifiers_are_double_quoted(string identifier, string expected) =>
        Assert.Equal(expected, new OracleProvider().QuoteIdentifier(identifier));

    [Fact]
    public void An_identifier_with_a_double_quote_is_refused() =>
        Assert.Throws<ArgumentException>(() => new OracleProvider().QuoteIdentifier("a\" OR \"1\"=\"1"));

    [Fact]
    public void Connection_string_is_built_from_the_settings()
    {
        var parsed = Build(new() { ["host"] = "ora01.corp.local", ["serviceName"] = "ORCLPDB1" }, Credential);

        Assert.Equal("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=ora01.corp.local)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=ORCLPDB1)))", parsed.DataSource);
        Assert.Equal("app_user", parsed.UserID);
        Assert.Equal("s3cret", parsed.Password);
        Assert.False(parsed.PersistSecurityInfo);
        Assert.Equal(15, parsed.ConnectionTimeout);
        Assert.True(string.IsNullOrEmpty(parsed.DBAPrivilege));

        var sid = Build(new() { ["host"] = "10.0.0.5", ["port"] = "1522", ["sid"] = "XE", ["role"] = "SysDba", ["connectTimeoutSeconds"] = "5" }, Credential);
        Assert.Contains("(HOST=10.0.0.5)(PORT=1522)", sid.DataSource, StringComparison.Ordinal);
        Assert.Contains("(SID=XE)", sid.DataSource, StringComparison.Ordinal);
        Assert.Equal("SYSDBA", sid.DBAPrivilege);
        Assert.Equal(5, sid.ConnectionTimeout);
    }

    [Fact]
    public void A_password_with_special_characters_survives_and_cannot_add_keywords()
    {
        var parsed = Build(new() { ["host"] = "ora01", ["serviceName"] = "S" }, new ConnectionCredential("u", "p;wd=\"1';Data Source=evil"));

        Assert.Equal("p;wd=\"1';Data Source=evil", parsed.Password);
        Assert.Contains("(HOST=ora01)", parsed.DataSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "1521", "S", "", "normal", "15", "u")]
    [InlineData("h)(HOST=evil", "1521", "S", "", "normal", "15", "u")]
    [InlineData("h", "1521", "S)(SERVER=x", "", "normal", "15", "u")]
    [InlineData("h", "70000", "S", "", "normal", "15", "u")]
    [InlineData("h", "1521", "", "", "normal", "15", "u")]
    [InlineData("h", "1521", "S", "X", "normal", "15", "u")]
    [InlineData("h", "1521", "S", "", "root", "15", "u")]
    [InlineData("h", "1521", "S", "", "normal", "0", "u")]
    [InlineData("h", "1521", "S", "", "normal", "15", "")]
    public void Invalid_settings_are_rejected_before_connecting(string host, string port, string service, string sid, string role, string timeout, string user)
    {
        var settings = new Dictionary<string, string>
        {
            ["host"] = host, ["port"] = port, ["serviceName"] = service, ["sid"] = sid, ["role"] = role, ["connectTimeoutSeconds"] = timeout,
        };

        var error = Assert.Throws<ArgumentException>(() => Build(settings, new ConnectionCredential(user, "s3cret")));
        Assert.DoesNotContain("s3cret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_endpoint_is_the_host_and_port()
    {
        var provider = new OracleProvider();

        Assert.Equal(new NetworkEndpoint("ora01", 1521), provider.GetEndpoint(new Dictionary<string, string> { ["host"] = " ora01 " }));
        Assert.Equal(new NetworkEndpoint("ora01", 1600), provider.GetEndpoint(new Dictionary<string, string> { ["host"] = "ora01", ["port"] = "1600" }));
        Assert.Null(provider.GetEndpoint(new Dictionary<string, string>()));
        Assert.True(provider.RequiresCredential(new Dictionary<string, string>()));
        Assert.Contains(provider.ConnectionSettings, static s => s is { Key: "host", IsRequired: true });
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.Packages));
        Assert.False(provider.Capabilities.HasFlag(ProviderCapabilities.MultipleDatabases));
    }

    [Theory]
    [InlineData("VARCHAR2", 200, null, null, 50, "C", "VARCHAR2(50 CHAR)")]
    [InlineData("VARCHAR2", 50, null, null, 50, "B", "VARCHAR2(50)")]
    [InlineData("NVARCHAR2", 100, null, null, 50, "C", "NVARCHAR2(50)")]
    [InlineData("CHAR", 1, null, null, 1, "B", "CHAR(1)")]
    [InlineData("NUMBER", 22, 12, 2, 0, null, "NUMBER(12,2)")]
    [InlineData("NUMBER", 22, 18, 0, 0, null, "NUMBER(18)")]
    [InlineData("NUMBER", 22, null, null, 0, null, "NUMBER")]
    [InlineData("RAW", 16, null, null, 0, null, "RAW(16)")]
    [InlineData("TIMESTAMP(6)", 11, null, 6, 0, null, "TIMESTAMP(6)")]
    [InlineData("DATE", 7, null, null, 0, null, "DATE")]
    [InlineData("CLOB", 4000, null, null, 0, null, "CLOB")]
    [InlineData("FLOAT", 22, 126, null, 0, null, "FLOAT")]
    [InlineData("FLOAT", 22, 53, null, 0, null, "FLOAT(53)")]
    public void Column_types_are_formatted_like_oracle_tools(string type, int length, int? precision, int? scale, int charLength, string? charUsed, string expected) =>
        Assert.Equal(expected, OracleTypeFormatter.Format(type, length, precision, scale, charLength, charUsed));

    [Fact]
    public void A_script_is_cut_the_way_sqlplus_cuts_it()
    {
        const string Script = """
            SET SERVEROUTPUT ON
            PROMPT Creating objects; don't stop
            CREATE TABLE cards (id NUMBER PRIMARY KEY, note VARCHAR2(40));
            INSERT INTO cards VALUES (1, 'a;b');

            CREATE OR REPLACE PROCEDURE purge IS
            BEGIN
              DELETE FROM cards WHERE id < 0;
              COMMIT;
            END purge;
            /
            SHOW ERRORS
            EXEC purge;
            BEGIN
              DBMS_OUTPUT.PUT_LINE('done');
            END;
            /
            SELECT COUNT(*) FROM cards
            """;

        var batches = new OracleProvider().SplitBatches(Script.ReplaceLineEndings("\n"));

        Assert.Equal(
            [
                "CREATE TABLE cards (id NUMBER PRIMARY KEY, note VARCHAR2(40))",
                "INSERT INTO cards VALUES (1, 'a;b')",
                "CREATE OR REPLACE PROCEDURE purge IS\nBEGIN\n  DELETE FROM cards WHERE id < 0;\n  COMMIT;\nEND purge;",
                "BEGIN purge; END;",
                "BEGIN\n  DBMS_OUTPUT.PUT_LINE('done');\nEND;",
                "SELECT COUNT(*) FROM cards",
            ],
            batches.Select(static b => b.Text));
        Assert.Equal([2, 3, 5, 12, 13, 17], batches.Select(static b => b.StartLine));
        Assert.All(batches, static b => Assert.Equal(1, b.RepeatCount));
        Assert.Empty(new OracleProvider().SplitBatches("  \n-- nothing\n"));
    }

    [Theory]
    [InlineData("CREATE OR REPLACE PROCEDURE purge IS BEGIN NULL; END;", "PROCEDURE", null, "PURGE")]
    [InlineData("create function app.calc(x number) return number is begin return x; end;", "FUNCTION", "APP", "CALC")]
    [InlineData("CREATE OR REPLACE PACKAGE BODY card_ops AS END card_ops;", "PACKAGE BODY", null, "CARD_OPS")]
    [InlineData("CREATE OR REPLACE EDITIONABLE PACKAGE \"App\".\"Mixed Case\" AS END;", "PACKAGE", "App", "Mixed Case")]
    [InlineData("CREATE OR REPLACE TRIGGER trg BEFORE INSERT ON t BEGIN NULL; END;", "TRIGGER", null, "TRG")]
    public void A_create_statement_tells_which_stored_unit_it_defines(string sql, string type, string? owner, string name) =>
        Assert.Equal((type, owner, name), OracleQueryExecutor.StoredUnit(sql));

    [Theory]
    [InlineData("CREATE TABLE t (id NUMBER)")]
    [InlineData("SELECT 1 FROM dual")]
    [InlineData("BEGIN NULL; END;")]
    public void Other_statements_define_no_stored_unit(string sql) =>
        Assert.Null(OracleQueryExecutor.StoredUnit(sql));

    [Fact]
    public async Task The_provider_loads_as_an_extension_with_its_own_driver()
    {
        var providerDirectory = typeof(OracleProviderTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(static a => a.Key == "ProviderDirectory").Value!;
        var options = new ExtensionHostOptions();
        options.Directories.Add(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(providerDirectory)))!);
        var host = new ExtensionHost(options, NullLogger<ExtensionHost>.Instance);

        var results = await host.LoadAsync(TestContext.Current.CancellationToken);

        var loaded = Assert.Single(results, static r => r.Manifest?.Id == "jetqueries.oracle");
        Assert.True(loaded.State == ExtensionState.Loaded, loaded.Error);
        var provider = host.FindDatabaseProvider(OracleProvider.ProviderId);
        Assert.NotNull(provider);
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(provider.GetType().Assembly));

        // Nothing listens on port 1 of this machine: the attempt fails inside the driver, which proves it was
        // resolved from the extension's directory.
        var profile = new ConnectionProfile(Guid.NewGuid(), "closed port", OracleProvider.ProviderId, new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = "1",
            ["serviceName"] = "NOPE",
            ["connectTimeoutSeconds"] = "3",
        });
        var error = await Assert.ThrowsAnyAsync<Exception>(() => provider.OpenConnectionAsync(profile, Credential, TestContext.Current.CancellationToken));
        Assert.Equal("OracleException", error.GetType().Name);
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(error.GetType().Assembly));
        Assert.DoesNotContain("s3cret", error.ToString(), StringComparison.Ordinal);
    }

    private static OracleConnectionStringBuilder Build(Dictionary<string, string> settings, ConnectionCredential? credential) =>
        new(OracleConnectionSettings.BuildConnectionString(
            new ConnectionProfile(Guid.NewGuid(), "test", OracleProvider.ProviderId, settings), credential));
}
