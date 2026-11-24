using JetQueries.Sdk;
using Oracle.ManagedDataAccess.Client;

namespace JetQueries.Provider.Oracle.Tests;

/// <summary>TNS aliases: reading tnsnames.ora, the connection string, the suggestions and the form's fields.</summary>
public sealed class OracleTnsTests
{
    private const string Sample = """
        # Production
        PRODDB =
          (DESCRIPTION =
            (ADDRESS = (PROTOCOL = TCP)(HOST = db-prod.example)(PORT = 1521))
            (CONNECT_DATA = (SERVER = DEDICATED) (SERVICE_NAME = PROD.example))
          )

        TEST1, TEST1_ALT = (DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=10.0.0.5)(PORT=1522)))(CONNECT_DATA=(SID=TST)))
        IFILE = C:\oracle\other.ora
        cloud_high = (description= (retry_count=20)(address=(protocol=tcps)(port=1522)(host=adb.example.com))(connect_data=(service_name=x_high.adb.example.com))(security=(ssl_server_dn_match=yes)))
        # PRODDB = (DESCRIPTION = (ADDRESS = (HOST = ignored)))
        """;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_aliases_of_a_tnsnames_file_are_read_with_where_they_point()
    {
        var entries = TnsNames.Parse(Sample);

        Assert.Equal(["PRODDB", "TEST1", "TEST1_ALT", "cloud_high"], entries.Select(static e => e.Alias));
        Assert.Equal(new TnsEntry("PRODDB", "db-prod.example", "1521", "PROD.example"), entries[0]);
        Assert.Equal("TST", entries[1].Service);
        Assert.Equal("TEST1_ALT — 10.0.0.5:1522/TST", entries[2].Describe());
        Assert.Equal("adb.example.com", entries[3].Host);
    }

    [Fact]
    public void A_tns_connection_string_names_the_alias_and_its_folders()
    {
        var profile = Profile(new() { ["connectionType"] = "tns", ["tnsAlias"] = "PRODDB", ["tnsAdmin"] = @"C:\oracle\network admin", ["walletLocation"] = @"C:\wallet" });

        var builder = new OracleConnectionStringBuilder(OracleConnectionSettings.BuildConnectionString(profile, new ConnectionCredential("hr", "pw")));

        Assert.Equal("PRODDB", builder.DataSource);
        Assert.Equal(@"C:\oracle\network admin", builder.TnsAdmin);
        Assert.Equal(@"C:\wallet", builder.WalletLocation);
        Assert.Equal("hr", builder.UserID);
    }

    [Theory]
    [InlineData("tnsAlias", "PROD(DB)")]
    [InlineData("tnsAdmin", @"C:\x;Password=y")]
    [InlineData("walletLocation", "C:\\x\"")]
    public void A_value_that_would_change_the_connection_string_is_refused(string key, string value)
    {
        var settings = new Dictionary<string, string> { ["connectionType"] = "tns", ["tnsAlias"] = "PRODDB", [key] = value };
        Assert.Throws<ArgumentException>(() => OracleConnectionSettings.BuildConnectionString(Profile(settings), new ConnectionCredential("hr", "pw")));
    }

    [Fact]
    public async Task The_aliases_are_suggested_from_the_chosen_folder()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "tns-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "tnsnames.ora"), Sample, Token);
        var provider = new OracleProvider();

        var suggestions = await provider.GetSuggestionsAsync("tnsAlias", new Dictionary<string, string> { ["tnsAdmin"] = folder }, Token);

        Assert.Equal("PRODDB", suggestions[0].Value);
        Assert.Equal("PRODDB — db-prod.example:1521/PROD.example", suggestions[0].DisplayName);
        Assert.Empty(await provider.GetSuggestionsAsync("tnsAlias", new Dictionary<string, string> { ["tnsAdmin"] = Path.Combine(folder, "none") }, Token));
        Assert.Empty(await provider.GetSuggestionsAsync("host", new Dictionary<string, string> { ["tnsAdmin"] = folder }, Token));
    }

    [Fact]
    public void The_form_shows_the_tns_fields_only_for_a_tns_connection()
    {
        var descriptors = new OracleProvider().ConnectionSettings.ToDictionary(static d => d.Key);
        var basic = new Dictionary<string, string> { ["connectionType"] = "basic" };
        var tns = new Dictionary<string, string> { ["connectionType"] = "tns" };

        Assert.True(descriptors["host"].VisibleWhen!.IsMet(basic));
        Assert.True(descriptors["host"].VisibleWhen!.IsMet(new Dictionary<string, string>(), "basic"));
        Assert.False(descriptors["host"].VisibleWhen!.IsMet(tns));
        Assert.True(descriptors["tnsAlias"].VisibleWhen!.IsMet(tns));
        Assert.True(descriptors["tnsAlias"].HasSuggestions);
        Assert.Equal(ConnectionSettingKind.FolderPath, descriptors["tnsAdmin"].Kind);
    }

    private static ConnectionProfile Profile(Dictionary<string, string> settings) => new(Guid.NewGuid(), "t", OracleProvider.ProviderId, settings);
}
