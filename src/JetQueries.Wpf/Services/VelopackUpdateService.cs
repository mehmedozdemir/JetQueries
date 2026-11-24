using System.IO;
using System.Reflection;
using JetQueries.Presentation.ViewModels;
using Velopack;

namespace JetQueries.Wpf.Services;

/// <summary>
/// Updates through Velopack: the feed is an http(s) URL or a local folder with the releases that
/// <c>scripts/pack.ps1</c> writes (<c>releases.&lt;channel&gt;.json</c> and the packages). Only an installed
/// application (Setup.exe) can update itself.
/// </summary>
internal sealed class VelopackUpdateService : IUpdateService
{
    // The manager of the running installation, for IsInstalled and the version; any source will do for that.
    private readonly UpdateManager _local = new(Path.GetTempPath());

    public bool IsInstalled => _local.IsInstalled;

    public string CurrentVersion =>
        _local.CurrentVersion?.ToString()
        ?? Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.0.0";

    public async Task<AvailableUpdate?> CheckAsync(UpdateSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        var info = await Manager(settings).CheckForUpdatesAsync().ConfigureAwait(false);
        return info is null ? null : new AvailableUpdate(info.TargetFullRelease.Version.ToString(), info);
    }

    public async Task DownloadAsync(UpdateSettings settings, AvailableUpdate update, IProgress<int> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(update);
        await Manager(settings).DownloadUpdatesAsync((UpdateInfo)update.Handle, progress.Report, cancelToken: cancellationToken).ConfigureAwait(false);
    }

    public void ApplyOnExit(UpdateSettings settings, AvailableUpdate update)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(update);

        // Velopack's updater waits for this process to exit (the normal close saves the open tabs), applies the
        // update and starts the new version.
        Manager(settings).WaitExitThenApplyUpdates(((UpdateInfo)update.Handle).TargetFullRelease, silent: false, restart: true);
    }

    private static UpdateManager Manager(UpdateSettings settings) =>
        new(settings.Source, new UpdateOptions { ExplicitChannel = settings.Channel });
}
