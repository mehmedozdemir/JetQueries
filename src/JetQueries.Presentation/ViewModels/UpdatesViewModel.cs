using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Jobs;
using JetQueries.Core.Settings;
using JetQueries.Presentation.Resources;

namespace JetQueries.Presentation.ViewModels;

/// <summary>Where updates come from and when they are looked for (the <c>updates</c> settings section).</summary>
public sealed record UpdateSettings
{
    /// <summary>The settings section (file) name.</summary>
    public const string Section = "updates";

    /// <summary>The release feed: an http(s) URL or a local folder holding releases.&lt;channel&gt;.json; empty for none.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>stable, beta or nightly.</summary>
    public string Channel { get; init; } = "stable";

    /// <summary>Look for an update in the background at start-up.</summary>
    public bool CheckOnStartup { get; init; } = true;
}

/// <summary>A newer release than the running one.</summary>
/// <param name="Version">Its version.</param>
/// <param name="Handle">What the update service needs to download and apply it.</param>
public sealed record AvailableUpdate(string Version, object Handle);

/// <summary>Finds, downloads and applies updates of the installed application (Velopack in the WPF host).</summary>
public interface IUpdateService
{
    /// <summary>Whether the application was installed (Setup.exe): only then can it update itself.</summary>
    bool IsInstalled { get; }

    /// <summary>The running version.</summary>
    string CurrentVersion { get; }

    /// <summary>A newer release in the feed, or null when the running one is the latest.</summary>
    Task<AvailableUpdate?> CheckAsync(UpdateSettings settings, CancellationToken cancellationToken);

    /// <summary>Downloads the update, reporting 0–100.</summary>
    Task DownloadAsync(UpdateSettings settings, AvailableUpdate update, IProgress<int> progress, CancellationToken cancellationToken);

    /// <summary>Arranges for the downloaded update to be applied when the application exits, and the new version started.</summary>
    void ApplyOnExit(UpdateSettings settings, AvailableUpdate update);
}

/// <summary>Where the update flow stands.</summary>
public enum UpdateState
{
    /// <summary>Nothing asked yet.</summary>
    Idle,

    /// <summary>Looking at the feed.</summary>
    Checking,

    /// <summary>The running version is the latest.</summary>
    UpToDate,

    /// <summary>A newer version can be downloaded.</summary>
    Available,

    /// <summary>Downloading.</summary>
    Downloading,

    /// <summary>Downloaded: a restart applies it.</summary>
    Ready,

    /// <summary>Something went wrong (shown in <see cref="UpdatesViewModel.Status"/>).</summary>
    Failed,
}

/// <summary>
/// The application's updates: the settings (channel, feed, start-up check), looking for a newer release in the
/// background, downloading it and restarting into it. The status bar shows it when there is something to do.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private readonly ISettingsStore? _store;
    private readonly IJobQueue _jobs;
    private AvailableUpdate? _found;

    public UpdatesViewModel(IUpdateService updates, IJobQueue jobs, ISettingsStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(jobs);
        _updates = updates;
        _jobs = jobs;
        _store = store;
        Status = Strings.UpdateIdle;
    }

    /// <summary>The release channels.</summary>
    public IReadOnlyList<string> Channels { get; } = ["stable", "beta", "nightly"];

    public string CurrentVersion => _updates.CurrentVersion;

    [ObservableProperty]
    public partial string Source { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Channel { get; set; } = "stable";

    [ObservableProperty]
    public partial bool CheckOnStartup { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsInStatusBar), nameof(CanDownload), nameof(CanRestart))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(DownloadCommand), nameof(RestartCommand))]
    public partial UpdateState State { get; set; }

    /// <summary>What the flow is doing, in the user's language.</summary>
    [ObservableProperty]
    public partial string Status { get; set; }

    /// <summary>Download progress, 0–100.</summary>
    [ObservableProperty]
    public partial int Progress { get; set; }

    /// <summary>The version found, when there is one.</summary>
    [ObservableProperty]
    public partial string? AvailableVersion { get; set; }

    /// <summary>Whether the status bar shows the update (found, downloading or ready).</summary>
    public bool ShowsInStatusBar => State is UpdateState.Available or UpdateState.Downloading or UpdateState.Ready;

    public bool CanDownload => State == UpdateState.Available;

    public bool CanRestart => State == UpdateState.Ready;

    /// <summary>The settings as shown.</summary>
    public UpdateSettings Settings => new() { Source = Source.Trim(), Channel = Channel, CheckOnStartup = CheckOnStartup };

    /// <summary>Reads the saved settings.</summary>
    public async Task LoadAsync()
    {
        var saved = _store is null
            ? new UpdateSettings()
            : await _jobs.Enqueue("Read update settings", context => _store.LoadAsync(UpdateSettings.Section, new UpdateSettings(), context.CancellationToken), JobPriority.Low).Completion;
        Source = saved.Source;
        Channel = Channels.Contains(saved.Channel) ? saved.Channel : "stable";
        CheckOnStartup = saved.CheckOnStartup;
    }

    /// <summary>Saves the settings as shown.</summary>
    public Task SaveAsync()
    {
        var settings = Settings;
        return _store is null
            ? Task.CompletedTask
            : _jobs.Enqueue("Save update settings", context => _store.SaveAsync(UpdateSettings.Section, settings, context.CancellationToken), JobPriority.High).Completion;
    }

    /// <summary>At start-up: looks for an update quietly when the settings ask for it (nothing is said when there is none).</summary>
    public async Task StartupCheckAsync()
    {
        await LoadAsync();
        if (CheckOnStartup && Source.Trim().Length > 0 && _updates.IsInstalled)
        {
            await CheckAsync(quiet: true);
        }
    }

    private bool CanCheck() => State is not (UpdateState.Checking or UpdateState.Downloading);

    /// <summary>Looks for a newer release now.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => CheckAsync(quiet: false);

    private async Task CheckAsync(bool quiet)
    {
        if (!_updates.IsInstalled)
        {
            State = UpdateState.Failed;
            Status = Strings.UpdateNotInstalled;
            return;
        }

        var settings = Settings;
        if (settings.Source.Length == 0)
        {
            State = UpdateState.Failed;
            Status = Strings.UpdateNoSource;
            return;
        }

        State = UpdateState.Checking;
        Status = Strings.UpdateChecking;
        try
        {
            _found = await _jobs.Enqueue("Check for updates", context => _updates.CheckAsync(settings, context.CancellationToken), JobPriority.Low).Completion;
            AvailableVersion = _found?.Version;
            State = _found is null ? (quiet ? UpdateState.Idle : UpdateState.UpToDate) : UpdateState.Available;
            Status = _found is null ? Format(Strings.UpdateUpToDate, _updates.CurrentVersion) : Format(Strings.UpdateAvailable, _found.Version);
        }
        catch (OperationCanceledException)
        {
            State = UpdateState.Idle;
            Status = Strings.UpdateIdle;
        }
#pragma warning disable CA1031 // A feed that cannot be read is reported; nothing else is disturbed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            State = quiet ? UpdateState.Idle : UpdateState.Failed;
            Status = Format(Strings.UpdateFailed, ex.Message);
        }
    }

    /// <summary>Downloads the update found.</summary>
    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (_found is not { } update)
        {
            return;
        }

        var settings = Settings;
        State = UpdateState.Downloading;
        Progress = 0;
        Status = Format(Strings.UpdateDownloading, update.Version);
        var progress = new Progress<int>(p => Progress = p);
        try
        {
            await _jobs.Enqueue("Download update", context => _updates.DownloadAsync(settings, update, progress, context.CancellationToken), JobPriority.Low).Completion;
            State = UpdateState.Ready;
            Status = Format(Strings.UpdateReady, update.Version);
        }
        catch (OperationCanceledException)
        {
            State = UpdateState.Available;
        }
#pragma warning disable CA1031 // Reported; the update stays available to try again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            State = UpdateState.Available;
            Status = Format(Strings.UpdateFailed, ex.Message);
        }
    }

    /// <summary>Raised when the application should close (normally: tabs are saved) to restart into the update.</summary>
    public event EventHandler? RestartRequested;

    /// <summary>Applies the downloaded update when the application closes, then starts the new version.</summary>
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private void Restart()
    {
        if (_found is { } update)
        {
            _updates.ApplyOnExit(Settings, update);
            RestartRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string Format(string format, object? argument) =>
        string.Format(CultureInfo.CurrentCulture, System.Text.CompositeFormat.Parse(format), argument);
}
