using System.Globalization;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>The database detail screen: tiles and their statuses, tables, files, backups, settings, opening it.</summary>
public sealed class DatabaseDetailTests
{
    public DatabaseDetailTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
    }

    [Fact]
    public async Task Tiles_say_what_needs_attention()
    {
        await using var env = new TestEnvironment();
        var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar"));
        var session = env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
        await using var detail = new DatabaseDetailViewModel(session, env.Jobs, "Cards");
        detail.Show(FakeDatabaseDetail.Of("Cards"), FakeDatabaseDetail.Now);

        Assert.Equal("ONLINE · FULL · compatibility 160 · Turkish_CI_AS", detail.Summary);
        Assert.StartsWith("owner sa · created ", detail.SummaryDetail, StringComparison.Ordinal);
        Assert.Equal(("2.0 GB", "1.5 GB used · 75 %", HealthSeverity.Ok), (detail.Data.Value, detail.Data.Detail, detail.Data.Status));
        Assert.Equal(("4.0 GB", "92 % full · held by LOG_BACKUP", HealthSeverity.Critical), (detail.Log.Value, detail.Log.Detail, detail.Log.Status));
        Assert.Equal(("450", HealthSeverity.Warning), (detail.Vlf.Value, detail.Vlf.Status));
        Assert.Equal(("10 days ago", HealthSeverity.Critical), (detail.FullBackup.Value, detail.FullBackup.Status));
        Assert.Equal("Critical", detail.FullBackup.StatusText);
        Assert.Equal(("never", HealthSeverity.Critical), (detail.LogBackup.Value, detail.LogBackup.Status));
        Assert.Equal(("never", HealthSeverity.Warning), (detail.CheckDb.Value, detail.CheckDb.Status));
        Assert.Equal(("37", "1,234,567 rows"), (detail.Tables.Value, detail.Tables.Detail));

        Assert.Equal(["dbo.Transactions", "dbo.Cards", "audit.Log"], detail.LargestTables.Select(static b => b.Label));
        Assert.Equal("900 MB · 1,000,000 rows", detail.LargestTables[0].Text);
        Assert.Equal(["dbo.Transactions", "1,000,000", "900 MB", "600 MB", "280 MB"], detail.TableRows[0]);
        Assert.Equal("12 MB", detail.TableRows[2][2]);
        Assert.Equal("0.4 MB", detail.TableRows[2][4]);
        Assert.Equal(["Cards_log", "LOG", null, "4.0 GB", "3.7 GB", "8 %", "10 %", "2048 MB", @"L:\Log\Cards_log.ldf"], detail.FileRows[1]);
        Assert.Equal("Differential", detail.BackupRows[0][0]);
        Assert.Equal("03:00", detail.BackupRows[0][2]);
        Assert.Equal("80 MB", detail.BackupRows[0][4]);

        // Settings: translated where known, the provider's label otherwise, empty ones left out.
        Assert.Equal(["Page verification", "Auto shrink", "Query Store", "Something new"], detail.Options.Select(static o => o.Key));
    }

    [Fact]
    public async Task Simple_recovery_needs_no_log_backups_and_unknown_backups_are_not_missing()
    {
        await using var env = new TestEnvironment();
        var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar"));
        var session = env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
        await using var detail = new DatabaseDetailViewModel(session, env.Jobs, "Archive");
        var now = FakeDatabaseDetail.Now;
        detail.Show(FakeDatabaseDetail.Of("Archive") with
        {
            RecoveryModel = "SIMPLE",
            LogUsedPercent = 10,
            LogReuseWait = "NOTHING",
            VirtualLogFiles = 40,
            LastGoodCheckDb = now - TimeSpan.FromDays(2),
            Backups = [new BackupRecord(BackupKind.Full, now - TimeSpan.FromHours(5))],
        }, now);

        Assert.Equal(("—", "SIMPLE: not needed", HealthSeverity.Ok), (detail.LogBackup.Value, detail.LogBackup.Detail, detail.LogBackup.Status));
        Assert.Equal(("5 hours ago", HealthSeverity.Ok), (detail.FullBackup.Value, detail.FullBackup.Status));
        Assert.Equal(HealthSeverity.Ok, detail.Log.Status);
        Assert.Equal(HealthSeverity.Ok, detail.Vlf.Status);
        Assert.Equal(HealthSeverity.Ok, detail.CheckDb.Status);

        detail.Show(FakeDatabaseDetail.Of("Locked") with { BackupsKnown = false, Backups = [] }, now);
        Assert.Equal(("?", HealthSeverity.Ok), (detail.FullBackup.Value, detail.FullBackup.Status));
        Assert.Equal("backup history not available", detail.LogBackup.Detail);
    }

    [Fact]
    public async Task A_database_node_opens_one_tab_per_database_and_reads_it()
    {
        await using var env = new TestEnvironment();
        var definition = TestEnvironment.Definition("Kartlar");
        await env.Store.SaveAsync(definition, TestContext.Current.CancellationToken);
        await env.Credentials.SetPasswordAsync(definition.Id, "x", TestContext.Current.CancellationToken);
        var connections = env.Connections();
        await connections.LoadAsync();
        connections.Selected = connections.Roots.OfType<ConnectionNodeViewModel>().Single();
        await connections.ToggleConnectionCommand.ExecuteAsync(null);
        var node = connections.Roots.OfType<ConnectionNodeViewModel>().Single();
        await TestEnvironment.LoadedAsync(node);
        var database = node.Children.OfType<DatabaseObjectNodeViewModel>().First(static d => d.Target.Kind == DatabaseObjectKinds.Database);
        Assert.True(database.CanDetail);
        Assert.True(database.HasDbaTools);

        var documents = env.Documents();
        connections.DatabaseDetailRequested += (_, detail) => documents.OpenDatabaseDetail(detail);
        connections.OpenDatabaseDetailCommand.Execute(database);
        connections.OpenDatabaseDetailCommand.Execute(database);
        var tab = Assert.Single(documents.Tabs.OfType<DatabaseDetailViewModel>());
        Assert.Equal(database.Target.Name.Name + " · Kartlar", tab.HeaderText);
        for (var i = 0; i < 100 && tab.TableRows.Count == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(3, tab.TableRows.Count);
        await documents.CloseTabCommand.ExecuteAsync(tab);
        Assert.Empty(documents.Tabs.OfType<DatabaseDetailViewModel>());
    }
}
