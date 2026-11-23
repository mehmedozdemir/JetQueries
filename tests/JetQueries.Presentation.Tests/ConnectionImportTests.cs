using System.Globalization;
using JetQueries.Core.Connections;
using JetQueries.Provider.Oracle;
using JetQueries.Provider.SqlServer;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>Connections imported from DBeaver and SSMS: what is read, how providers map it, what is saved.</summary>
public sealed class ConnectionImportTests
{
    public ConnectionImportTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    private const string DBeaver = """
        {
          "folders": { "Müşteri A": {}, "Müşteri A/Canlı": { "parent": "Müşteri A" } },
          "connections": {
            "sqlserver-1": { "provider": "sqlserver", "driver": "microsoft", "name": "Kartlar canlı", "folder": "Müşteri A/Canlı",
              "configuration": { "host": "10.0.0.5", "port": "1433", "database": "Cards", "type": "prod", "auth-model": "sqlserver_native", "user": "app_reader" } },
            "oracle-1": { "provider": "oracle", "driver": "oracle_thin", "name": "Ödeme test",
              "configuration": { "host": "ora1", "port": "1521", "database": "ORCL", "type": "test", "provider-properties": { "@dbeaver-sid-service@": "SID" } } },
            "pg-1": { "provider": "postgresql", "driver": "postgres-jdbc", "name": "Raporlar",
              "configuration": { "host": "pg", "port": "5432", "database": "reports", "type": "dev" } },
            "mysql-1": { "provider": "mysql", "name": "Not supported", "configuration": { "host": "m" } }
          }
        }
        """;

    private const string Ssms = """
        <?xml version="1.0"?>
        <model xmlns="http://schemas.serviceml.org/smlif/2007/02">
          <definitions><document><data>
            <RegisteredServers:RegisteredServer xmlns:RegisteredServers="http://schemas.microsoft.com/sqlserver/RegisteredServers/2007/08" xmlns:sml="http://schemas.serviceml.org/sml/2007/02">
              <RegisteredServers:Parent><sfc:Reference xmlns:sfc="http://schemas.microsoft.com/sqlserver/sfc/serialization/2007/08"><sml:Uri>/RegisteredServersStore/ServerGroup/DatabaseEngineServerGroup/ServerGroup/Müşteri B/ServerGroup/Test</sml:Uri></sfc:Reference></RegisteredServers:Parent>
              <RegisteredServers:Name type="string">B test</RegisteredServers:Name>
              <RegisteredServers:ServerName type="string">sql-b\INST1</RegisteredServers:ServerName>
              <RegisteredServers:ServerType type="ServerType">DatabaseEngine</RegisteredServers:ServerType>
              <RegisteredServers:ConnectionStringWithEncryptedPassword type="string">data source=sql-b\INST1;initial catalog=Ledger;user id=ledger_ro;password=AQAAANCMnd8BFdER;encrypt=False;trust server certificate=True</RegisteredServers:ConnectionStringWithEncryptedPassword>
            </RegisteredServers:RegisteredServer>
          </data></document></definitions>
          <definitions><document><data>
            <RegisteredServers:RegisteredServer xmlns:RegisteredServers="http://schemas.microsoft.com/sqlserver/RegisteredServers/2007/08">
              <RegisteredServers:Name type="string">Yerel</RegisteredServers:Name>
              <RegisteredServers:ServerName type="string">.</RegisteredServers:ServerName>
              <RegisteredServers:ServerType type="ServerType">DatabaseEngine</RegisteredServers:ServerType>
              <RegisteredServers:ConnectionStringWithEncryptedPassword type="string">data source=.;integrated security=True</RegisteredServers:ConnectionStringWithEncryptedPassword>
            </RegisteredServers:RegisteredServer>
            <RegisteredServers:RegisteredServer xmlns:RegisteredServers="http://schemas.microsoft.com/sqlserver/RegisteredServers/2007/08">
              <RegisteredServers:Name type="string">Analiz</RegisteredServers:Name>
              <RegisteredServers:ServerType type="ServerType">AnalysisServices</RegisteredServers:ServerType>
            </RegisteredServers:RegisteredServer>
          </data></document></definitions>
        </model>
        """;

    [Fact]
    public void DBeaver_and_SSMS_connections_are_read_without_passwords()
    {
        var dbeaver = ConnectionImporter.ReadDBeaver(DBeaver);
        Assert.Equal(["Kartlar canlı", "Ödeme test", "Raporlar"], dbeaver.Select(static c => c.Name));
        var cards = dbeaver[0];
        Assert.Equal(("sqlserver", "Müşteri A/Canlı", "app_reader", ConnectionEnvironment.Production), (cards.ProviderId, cards.Folder, cards.UserName, cards.Environment));
        Assert.Equal("10.0.0.5:1433 / Cards", cards.Target);
        Assert.Equal(("ORCL", null), (dbeaver[1].Endpoint.Sid, dbeaver[1].Endpoint.ServiceName));

        var ssms = ConnectionImporter.ReadSsms(Ssms);
        Assert.Equal(["B test", "Yerel"], ssms.Select(static c => c.Name));
        Assert.Equal(("Müşteri B/Test", "ledger_ro", "Ledger"), (ssms[0].Folder, ssms[0].UserName, ssms[0].Endpoint.Database));
        Assert.Equal((false, true), (ssms[0].Endpoint.Encrypt, ssms[0].Endpoint.TrustServerCertificate));
        Assert.True(ssms[1].Endpoint.IntegratedSecurity);
        Assert.Null(ssms[1].UserName);
        Assert.DoesNotContain(ssms, static c => c.ToString().Contains("AQAAANCM", StringComparison.Ordinal));
    }

    [Fact]
    public void Providers_turn_an_imported_endpoint_into_their_own_settings()
    {
        var sqlServer = new SqlServerProvider();
        Assert.Equal(
            new Dictionary<string, string> { ["server"] = "10.0.0.5,1433", ["database"] = "Cards", ["authentication"] = "sql" },
            sqlServer.ToSettings(ConnectionImporter.ReadDBeaver(DBeaver)[0].Endpoint));
        Assert.Equal(
            new Dictionary<string, string> { ["server"] = "sql-b\\INST1", ["database"] = "Ledger", ["authentication"] = "sql", ["encrypt"] = "false", ["trustServerCertificate"] = "true" },
            sqlServer.ToSettings(ConnectionImporter.ReadSsms(Ssms)[0].Endpoint));

        var oracle = new OracleProvider();
        Assert.Equal(new Dictionary<string, string> { ["host"] = "ora1", ["port"] = "1521", ["sid"] = "ORCL" }, oracle.ToSettings(ConnectionImporter.ReadDBeaver(DBeaver)[1].Endpoint));
    }

    [Fact]
    public async Task Chosen_connections_are_saved_once_with_folder_and_environment()
    {
        await using var env = new TestEnvironment();
        var directory = Directory.CreateTempSubdirectory("jq-import").FullName;
        var json = Path.Combine(directory, "data-sources.json");
        var xml = Path.Combine(directory, "RegSrvr.xml");
        await File.WriteAllTextAsync(json, DBeaver, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(xml, Ssms, TestContext.Current.CancellationToken);

        IDatabaseProvider[] providers = [new SqlServerProvider(), new OracleProvider()];
        var import = new ImportConnectionsViewModel(providers, env.Store, env.Jobs);
        await import.AddFileAsync(json, "DBeaver");
        await import.AddFileAsync(xml, "SSMS");

        // PostgreSQL is not loaded here: listed, not importable.
        Assert.Equal(5, import.Items.Count);
        var postgres = import.Items.Single(static i => i.Candidate.ProviderId == "postgresql");
        Assert.False(postgres.CanImport);
        Assert.Equal("no provider", postgres.Note);
        Assert.Equal(4, import.SelectedCount);

        import.Items.Single(static i => i.Candidate.Name == "Yerel").IsSelected = false;
        await import.ImportCommand.ExecuteAsync(null);

        var saved = await env.Store.GetAllAsync(TestContext.Current.CancellationToken);
        var cards = saved.Single(static d => d.Name == "Kartlar canlı");
        Assert.Equal(("Müşteri A/Canlı", ConnectionEnvironment.Production, true, "app_reader", false), (cards.Folder, cards.Environment, cards.IsReadOnly, cards.UserName, cards.SavePassword));
        Assert.DoesNotContain(saved, static d => d.Name == "Yerel");
        Assert.Equal(3, saved.Count);

        // Read again: what is saved is marked and left out.
        var again = new ImportConnectionsViewModel(providers, env.Store, env.Jobs, static () => []);
        await again.DiscoverAsync();
        await again.AddFileAsync(json, "DBeaver");
        Assert.True(again.Items.Single(static i => i.Candidate.Name == "Kartlar canlı").Exists);
        Assert.Equal("already saved", again.Items.Single(static i => i.Candidate.Name == "Kartlar canlı").Note);
    }
}
