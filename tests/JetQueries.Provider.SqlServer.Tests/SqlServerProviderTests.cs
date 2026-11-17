using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using JetQueries.Core.Extensions;
using JetQueries.Sdk;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Provider.SqlServer.Tests;

/// <summary>Everything that can be checked without a database.</summary>
public sealed class SqlServerProviderTests
{
    private static readonly ConnectionCredential Credential = new("app_user", "s3cret");

    [Theory]
    [InlineData("Orders", "[Orders]")]
    [InlineData("my table", "[my table]")]
    [InlineData("a]b", "[a]]b]")]
    [InlineData("x]; DROP TABLE t; --", "[x]]; DROP TABLE t; --]")]
    [InlineData("Müşteri", "[Müşteri]")]
    public void Identifiers_are_bracket_quoted(string identifier, string expected) =>
        Assert.Equal(expected, new SqlServerProvider().QuoteIdentifier(identifier));

    [Fact]
    public void Multi_part_names_skip_missing_parts() =>
        Assert.Equal("[db].[dbo].[T]", SqlServerNames.Quote(new DatabaseObjectName("db", "dbo", "T")));

    [Fact]
    public void Connection_string_defaults_are_safe()
    {
        var parsed = Build(new() { ["server"] = "sql01,1433", ["database"] = "Cards" }, Credential);

        Assert.Equal("sql01,1433", parsed.DataSource);
        Assert.Equal("Cards", parsed.InitialCatalog);
        Assert.Equal("app_user", parsed.UserID);
        Assert.Equal("s3cret", parsed.Password);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, parsed.Encrypt);
        Assert.False(parsed.TrustServerCertificate);
        Assert.False(parsed.PersistSecurityInfo);
        Assert.False(parsed.IntegratedSecurity);
        Assert.Equal("JetQueries", parsed.ApplicationName);
        Assert.Equal(ApplicationIntent.ReadWrite, parsed.ApplicationIntent);
    }

    [Fact]
    public void Values_cannot_inject_connection_string_keywords()
    {
        var parsed = Build(
            new() { ["server"] = "sql01;Encrypt=false;TrustServerCertificate=true", ["database"] = "x;Integrated Security=true" },
            new ConnectionCredential("u;Password=other", "p;wd=\"1'"));

        Assert.Equal("sql01;Encrypt=false;TrustServerCertificate=true", parsed.DataSource);
        Assert.Equal("x;Integrated Security=true", parsed.InitialCatalog);
        Assert.Equal("u;Password=other", parsed.UserID);
        Assert.Equal("p;wd=\"1'", parsed.Password);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, parsed.Encrypt);
        Assert.False(parsed.TrustServerCertificate);
        Assert.False(parsed.IntegratedSecurity);
    }

    [Fact]
    public void Windows_authentication_sends_no_credentials()
    {
        var parsed = Build(new() { ["server"] = "sql01", ["authentication"] = "Windows" }, Credential);

        Assert.True(parsed.IntegratedSecurity);
        Assert.Empty(parsed.UserID);
        Assert.Empty(parsed.Password);
    }

    [Fact]
    public void Optional_settings_are_applied()
    {
        var parsed = Build(
            new()
            {
                ["server"] = "sql01",
                ["encrypt"] = "Strict",
                ["trustServerCertificate"] = "true",
                ["readOnlyIntent"] = "True",
                ["connectTimeoutSeconds"] = "5",
            },
            Credential);

        Assert.Equal(SqlConnectionEncryptOption.Strict, parsed.Encrypt);
        Assert.True(parsed.TrustServerCertificate);
        Assert.Equal(ApplicationIntent.ReadOnly, parsed.ApplicationIntent);
        Assert.Equal(5, parsed.ConnectTimeout);
    }

    [Theory]
    [InlineData("", "sql", "true", "15", "u")]
    [InlineData("s", "kerberos", "true", "15", "u")]
    [InlineData("s", "sql", "maybe", "15", "u")]
    [InlineData("s", "sql", "true", "0", "u")]
    [InlineData("s", "sql", "true", "ten", "u")]
    [InlineData("s", "sql", "true", "15", "")]
    public void Invalid_settings_are_rejected_before_connecting(string server, string authentication, string encrypt, string timeout, string user)
    {
        var settings = new Dictionary<string, string>
        {
            ["server"] = server,
            ["authentication"] = authentication,
            ["encrypt"] = encrypt,
            ["connectTimeoutSeconds"] = timeout,
        };

        var error = Assert.Throws<ArgumentException>(() => Build(settings, new ConnectionCredential(user, "p")));
        Assert.DoesNotContain("p;", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nvarchar", "nvarchar", 100, 0, 0, false, "nvarchar(50)")]
    [InlineData("nvarchar", "nvarchar", -1, 0, 0, false, "nvarchar(max)")]
    [InlineData("varchar", "varchar", 20, 0, 0, false, "varchar(20)")]
    [InlineData("varbinary", "varbinary", -1, 0, 0, false, "varbinary(max)")]
    [InlineData("decimal", "decimal", 9, 18, 2, false, "decimal(18,2)")]
    [InlineData("datetime2", "datetime2", 8, 27, 7, false, "datetime2(7)")]
    [InlineData("float", "float", 8, 53, 0, false, "float")]
    [InlineData("float", "float", 4, 24, 0, false, "float(24)")]
    [InlineData("int", "int", 4, 10, 0, false, "int")]
    [InlineData("Phone", "varchar", 20, 0, 0, true, "Phone")]
    [InlineData("BINARY", null, 16, 0, 0, false, "BINARY(16)")]
    public void Column_types_are_formatted_like_sql_server_tools(
        string type, string? baseType, int maxLength, int precision, int scale, bool userDefined, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // Turkish casing (i/İ, ı/I) must not change how type names are recognized.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(expected, SqlServerTypeFormatter.Format(type, baseType, maxLength, precision, scale, userDefined));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Schema_object_query_selects_only_the_requested_kinds()
    {
        var sql = SqlServerMetadataProvider.BuildSchemaObjectsQuery("Sales]Db", DatabaseObjectKinds.Table | DatabaseObjectKinds.View)!;

        Assert.Contains("o.type IN ('U', 'V')", sql, StringComparison.Ordinal);
        Assert.Contains("[Sales]]Db].sys.objects", sql, StringComparison.Ordinal);
        Assert.Contains("s.name = @schema", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("sys.types", sql, StringComparison.Ordinal);

        Assert.Contains("sys.types", SqlServerMetadataProvider.BuildSchemaObjectsQuery(null, DatabaseObjectKinds.Type)!, StringComparison.Ordinal);
        Assert.Null(SqlServerMetadataProvider.BuildSchemaObjectsQuery(null, DatabaseObjectKinds.Package | DatabaseObjectKinds.Schema));
    }

    [Theory]
    [InlineData("U", DatabaseObjectKinds.Table)]
    [InlineData("V", DatabaseObjectKinds.View)]
    [InlineData("P", DatabaseObjectKinds.Procedure)]
    [InlineData("TF", DatabaseObjectKinds.Function)]
    [InlineData("SO", DatabaseObjectKinds.Sequence)]
    [InlineData("SN", DatabaseObjectKinds.Synonym)]
    [InlineData("UT", DatabaseObjectKinds.Type)]
    public void Catalog_type_codes_map_to_kinds(string type, DatabaseObjectKinds expected) =>
        Assert.Equal(expected, SqlServerMetadataProvider.KindOf(type));

    [Fact]
    public async Task The_provider_loads_as_an_extension_with_its_own_sql_client()
    {
        var providerDirectory = typeof(SqlServerProviderTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(static a => a.Key == "ProviderDirectory").Value!;
        var options = new ExtensionHostOptions();
        options.Directories.Add(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(providerDirectory)))!);
        var host = new ExtensionHost(options, NullLogger<ExtensionHost>.Instance);

        var results = await host.LoadAsync(TestContext.Current.CancellationToken);

        var loaded = Assert.Single(results, static r => r.Manifest?.Id == "jetqueries.sqlserver");
        Assert.True(loaded.State == ExtensionState.Loaded, loaded.Error);
        var provider = host.FindDatabaseProvider(SqlServerProvider.ProviderId);
        Assert.NotNull(provider);
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(provider.GetType().Assembly));

        // Nothing listens on port 1 of this machine: the attempt fails inside SqlClient, which proves the
        // driver and its native library were resolved from the extension's directory.
        var profile = new ConnectionProfile(Guid.NewGuid(), "closed port", SqlServerProvider.ProviderId, new Dictionary<string, string>
        {
            ["server"] = "127.0.0.1,1",
            ["connectTimeoutSeconds"] = "2",
        });
        var error = await Assert.ThrowsAnyAsync<Exception>(() => provider.OpenConnectionAsync(profile, Credential, TestContext.Current.CancellationToken));
        Assert.Equal("SqlException", error.GetType().Name);
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(error.GetType().Assembly));
        Assert.DoesNotContain("s3cret", error.ToString(), StringComparison.Ordinal);
    }

    private static SqlConnectionStringBuilder Build(Dictionary<string, string> settings, ConnectionCredential? credential) =>
        new(SqlServerConnectionSettings.BuildConnectionString(
            new ConnectionProfile(Guid.NewGuid(), "test", SqlServerProvider.ProviderId, settings), credential));
}

public sealed class SqlServerEndpointTests
{
    [Theory]
    [InlineData("sql01", "sql01", 1433)]
    [InlineData("sql01,14330", "sql01", 14330)]
    [InlineData("tcp:sql01.corp.local, 1444", "sql01.corp.local", 1444)]
    [InlineData(@"10.0.0.5\PROD,1500", "10.0.0.5", 1500)]
    [InlineData(".", "localhost", 1433)]
    [InlineData("(local)", "localhost", 1433)]
    public void Tcp_endpoint_is_read_from_the_server_setting(string server, string host, int port) =>
        Assert.Equal(new JetQueries.Sdk.NetworkEndpoint(host, port), SqlServerConnectionSettings.ParseEndpoint(server));

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData(@"sql01\PROD")]
    [InlineData(@"np:\\sql01\pipe\sql\query")]
    [InlineData("lpc:sql01")]
    [InlineData("sql01,notaport")]
    [InlineData("sql01,70000")]
    public void Endpoints_that_cannot_be_known_are_left_to_the_driver(string? server) =>
        Assert.Null(SqlServerConnectionSettings.ParseEndpoint(server));

    [Fact]
    public void Windows_authentication_needs_no_credential()
    {
        var provider = new SqlServerProvider();

        Assert.True(provider.RequiresCredential(new Dictionary<string, string>()));
        Assert.False(provider.RequiresCredential(new Dictionary<string, string> { ["authentication"] = " Windows " }));
        Assert.Contains(provider.ConnectionSettings, static s => s is { Key: "server", IsRequired: true });
    }
}
