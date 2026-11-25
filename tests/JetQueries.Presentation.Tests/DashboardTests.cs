using System.Globalization;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>The server dashboard: tiles, rates from two samples, chart windows, waits and backup warnings.</summary>
public sealed class DashboardTests
{
    public DashboardTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
    }

    [Fact]
    public async Task Two_samples_make_rates_and_the_overview_fills_the_header_and_databases()
    {
        await using var env = new TestEnvironment();
        var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar"));
        var session = env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
        await using var dashboard = new DashboardViewModel(session, env.Jobs) { AutoRefresh = false };

        await dashboard.RefreshAsync();
        Assert.Null(dashboard.Error);
        Assert.Equal("SQL Server 2022 · Developer Edition (64-bit) · 16.0.4135.4 RTM", dashboard.ServerText);
        Assert.StartsWith("SQL01 · 8 CPU · 32.0 GB · up ", dashboard.ServerDetail, StringComparison.Ordinal);
        Assert.Equal("36 %", dashboard.CpuText);
        Assert.Equal("server 30 % · other 6 %", dashboard.CpuDetail);
        Assert.Equal("11.7 GB", dashboard.MemoryText);
        Assert.Equal("target 13.7 GB · machine free 3.1 GB", dashboard.MemoryDetail);
        Assert.Equal("7", dashboard.RequestsText);
        Assert.Equal("142 connections", dashboard.RequestsDetail);
        Assert.Equal("4,200 s", dashboard.PleText);
        Assert.Equal(31, dashboard.CpuSeries[0].Points.Count);    // 30 minutes of history and the latest
        Assert.Empty(dashboard.BatchSeries[0].Points);              // a rate needs two samples
        Assert.Equal("Top waits since the server started", dashboard.WaitsTitle);
        Assert.Equal("PAGEIOLATCH_SH", dashboard.Waits[0].Label);
        Assert.Equal(5, dashboard.Databases.Count);

        await dashboard.RefreshAsync();
        var batches = Assert.Single(dashboard.BatchSeries[0].Points);
        Assert.Equal(1_120, batches.Value);                         // (5 600 batches) / 5 s
        Assert.Equal("1,120", dashboard.BatchText);
        Assert.Equal("10 compilations / s", dashboard.BatchDetail);
        Assert.Equal(10, dashboard.IoSeries[0].Points[0].Value, 3);  // 50 MB / 5 s
        Assert.Equal(2, dashboard.IoSeries[1].Points[0].Value, 3);
        Assert.Equal("3.5 ms", dashboard.LatencyText);             // 3 500 ms stall / 1 000 operations
        Assert.Equal("Top waits since the dashboard opened", dashboard.WaitsTitle);
        Assert.Equal(["LCK_M_X", "PAGEIOLATCH_SH", "CXPACKET", "WRITELOG"], dashboard.Waits.Select(static w => w.Label));
        Assert.Equal("6.0 s", dashboard.Waits[0].Text);

        await dashboard.RefreshAsync();
        await dashboard.RefreshAsync();
        Assert.Equal(2, dashboard.BlockedCount);
        Assert.True(dashboard.HasBlocking);
        Assert.Equal(3, dashboard.BatchSeries[0].Points.Count);
    }

    [Fact]
    public void Backups_missing_or_old_are_warned_about_tempdb_and_offline_databases_never()
    {
        var now = FakeDashboard.Start;
        var rows = FakeDashboard.Overview().Databases.Select(d => DashboardViewModel.DatabaseRowOf(d, now)).ToList();

        Assert.Null(rows[0].Cells[7]);                                               // Cards: full and log recent
        Assert.Equal("FULL recovery without a log backup in 24 h", rows[1].Cells[7]); // Sales: no log backup
        Assert.Equal("no full backup", rows[2].Cells[7]);                            // Archive
        Assert.Equal("full backup 10 days old", rows[3].Cells[7]);                   // master
        Assert.Null(rows[4].Cells[7]);                                               // tempdb
        Assert.Equal("50.0 GB", rows[1].Cells[3]);
        Assert.Equal("—", rows[2].Cells[5]);

        var unknown = DashboardViewModel.DatabaseRowOf(new DatabaseSummary("Locked") { State = "ONLINE", RecoveryModel = "FULL" }, now);
        Assert.Null(unknown.Cells[7]);
        Assert.Equal("?", unknown.Cells[5]);
        Assert.Null(DashboardViewModel.DatabaseRowOf(new DatabaseSummary("Old") { State = "OFFLINE", BackupsKnown = true }, now).Cells[7]);
    }
}
