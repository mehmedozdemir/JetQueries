using System.Globalization;
using JetQueries.Core.Settings;
using JetQueries.Presentation.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Presentation.Tests;

/// <summary>Looking for, downloading and applying an update.</summary>
public sealed class UpdatesTests
{
    public UpdatesTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    private sealed class FakeUpdates : IUpdateService
    {
        public bool IsInstalled { get; set; } = true;

        public string CurrentVersion => "0.1.0";

        public string? Newer { get; set; }

        public Exception? Failure { get; set; }

        public UpdateSettings? AskedWith { get; private set; }

        public AvailableUpdate? Applied { get; private set; }

        public Task<AvailableUpdate?> CheckAsync(UpdateSettings settings, CancellationToken cancellationToken)
        {
            AskedWith = settings;
            return Failure is not null ? Task.FromException<AvailableUpdate?>(Failure) : Task.FromResult(Newer is null ? null : new AvailableUpdate(Newer, "handle"));
        }

        public Task DownloadAsync(UpdateSettings settings, AvailableUpdate update, IProgress<int> progress, CancellationToken cancellationToken)
        {
            progress.Report(50);
            progress.Report(100);
            return Task.CompletedTask;
        }

        public void ApplyOnExit(UpdateSettings settings, AvailableUpdate update) => Applied = update;
    }

    private static JsonSettingsStore Store() => new(new SettingsStoreOptions(), NullLogger<JsonSettingsStore>.Instance);

    [Fact]
    public async Task An_update_is_found_downloaded_and_applied_on_restart()
    {
        await using var env = new TestEnvironment();
        var service = new FakeUpdates { Newer = "0.2.0" };
        var updates = new UpdatesViewModel(service, env.Jobs, Store()) { Source = @"D:\releases", Channel = "beta" };
        Assert.False(updates.ShowsInStatusBar);

        await updates.CheckCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.Available, updates.State);
        Assert.Equal("Version 0.2.0 is available.", updates.Status);
        Assert.Equal("beta", service.AskedWith!.Channel);
        Assert.Equal(@"D:\releases", service.AskedWith.Source);
        Assert.True(updates.ShowsInStatusBar);
        Assert.True(updates.CanDownload);

        await updates.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.Ready, updates.State);
        Assert.True(updates.CanRestart);

        var restarted = false;
        updates.RestartRequested += (_, _) => restarted = true;
        updates.RestartCommand.Execute(null);
        Assert.True(restarted);
        Assert.Equal("0.2.0", service.Applied!.Version);
    }

    [Fact]
    public async Task Without_a_newer_version_a_source_or_an_installation_it_says_so()
    {
        await using var env = new TestEnvironment();
        var service = new FakeUpdates();
        var updates = new UpdatesViewModel(service, env.Jobs);

        await updates.CheckCommand.ExecuteAsync(null);
        Assert.Equal("No update source is set (Settings > Updates).", updates.Status);

        updates.Source = "https://example.invalid/releases";
        await updates.CheckCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.UpToDate, updates.State);
        Assert.Equal("0.1.0 is the latest version.", updates.Status);

        service.Failure = new InvalidOperationException("feed unreachable");
        await updates.CheckCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.Failed, updates.State);
        Assert.Contains("feed unreachable", updates.Status, StringComparison.Ordinal);

        service.IsInstalled = false;
        await updates.CheckCommand.ExecuteAsync(null);
        Assert.StartsWith("Updates work in the installed application", updates.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_settings_are_saved_and_the_startup_check_is_quiet()
    {
        await using var env = new TestEnvironment();
        var store = Store();
        var service = new FakeUpdates();
        var updates = new UpdatesViewModel(service, env.Jobs, store) { Source = "https://example.invalid", Channel = "nightly", CheckOnStartup = true };
        await updates.SaveAsync();

        var again = new UpdatesViewModel(service, env.Jobs, store);
        await again.StartupCheckAsync();
        Assert.Equal("nightly", again.Channel);
        Assert.Equal("https://example.invalid", again.Source);

        // Nothing newer: the status bar stays quiet.
        Assert.Equal(UpdateState.Idle, again.State);
        Assert.False(again.ShowsInStatusBar);
    }
}
