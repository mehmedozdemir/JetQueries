using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Jobs;
using JetQueries.Core.Settings;
using JetQueries.Core.Sync;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.Services;

namespace JetQueries.Presentation.ViewModels;

/// <summary>The sync settings: on or off, and the shared folder.</summary>
public sealed record SyncSettings
{
    /// <summary>The settings section (file) name.</summary>
    public const string Section = "sync";

    public bool Enabled { get; init; }

    /// <summary>The shared folder (OneDrive, Dropbox, a network share); empty for none.</summary>
    public string Folder { get; init; } = string.Empty;
}

/// <summary>
/// Sync of connection definitions and saved queries through a shared folder (Settings, Sync): at start-up, every
/// few minutes while the application runs, and on "Sync now". Runs in the background; when it brings changes the
/// explorer and the library are read again (<see cref="Applied"/>).
/// </summary>
public sealed partial class SyncViewModel : ObservableObject, IDisposable
{
    /// <summary>How often a running application syncs.</summary>
    public static TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(2);

    private readonly ISyncService _sync;
    private readonly IJobQueue _jobs;
    private readonly IUiDispatcher _ui;
    private readonly ISettingsStore? _store;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private bool _scheduled;

    public SyncViewModel(ISyncService sync, IJobQueue jobs, IUiDispatcher ui, ISettingsStore? store = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(ui);
        _sync = sync;
        _jobs = jobs;
        _ui = ui;
        _store = store;
        _time = time ?? TimeProvider.System;
        Folder = string.Empty;
        Status = Strings.SyncNever;
    }

    /// <summary>A sync changed connections or saved queries here.</summary>
    public event EventHandler<SyncResult>? Applied;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    public partial string Folder { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    public partial bool IsSyncing { get; set; }

    /// <summary>What the last sync did, or why it failed.</summary>
    [ObservableProperty]
    public partial string Status { get; set; }

    /// <summary>The last sync failed.</summary>
    [ObservableProperty]
    public partial bool Failed { get; set; }

    /// <summary>The settings as shown.</summary>
    public SyncSettings Settings => new() { Enabled = Enabled, Folder = Folder.Trim() };

    /// <summary>Reads the saved settings.</summary>
    public async Task LoadAsync()
    {
        var saved = _store is null
            ? new SyncSettings()
            : await _jobs.Enqueue("Read sync settings", context => _store.LoadAsync(SyncSettings.Section, new SyncSettings(), context.CancellationToken), JobPriority.Low).Completion;
        Enabled = saved.Enabled;
        Folder = saved.Folder;
    }

    /// <summary>Saves the settings as shown.</summary>
    public Task SaveAsync()
    {
        var settings = Settings;
        return _store is null
            ? Task.CompletedTask
            : _jobs.Enqueue("Save sync settings", context => _store.SaveAsync(SyncSettings.Section, settings, context.CancellationToken), JobPriority.High).Completion;
    }

    /// <summary>At start-up: reads the settings, syncs when it is on, then every <see cref="Interval"/>.</summary>
    public async Task StartAsync()
    {
        await LoadAsync();
        await SyncIfOnAsync();
        if (!_scheduled)
        {
            _scheduled = true;
            _ = RepeatAsync(_stop.Token);
        }
    }

    /// <summary>Syncs when sync is on and no sync is running (quietly: a failure only shows in the status).</summary>
    public Task SyncIfOnAsync() => Enabled && Folder.Trim().Length > 0 && !IsSyncing ? SyncAsync() : Task.CompletedTask;

    private bool CanSyncNow() => !IsSyncing && Folder.Trim().Length > 0;

    /// <summary>Syncs now (Settings, Sync now).</summary>
    [RelayCommand(CanExecute = nameof(CanSyncNow))]
    private Task SyncNowAsync() => SyncAsync();

    private async Task SyncAsync()
    {
        var folder = Folder.Trim();
        IsSyncing = true;
        Status = Strings.SyncRunning;
        try
        {
            var result = await _jobs.Enqueue("Sync", context => _sync.SyncAsync(folder, context.CancellationToken), JobPriority.Low).Completion;
            Failed = false;
            var at = _time.GetLocalNow().ToString("t", CultureInfo.CurrentCulture);
            Status = result is { Received: 0, Sent: 0, Removed: 0 }
                ? string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.SyncUpToDate), at)
                : string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.SyncDone), at, result.Received, result.Sent, result.Removed);
            if (result.ConnectionsChanged || result.QueriesChanged)
            {
                Applied?.Invoke(this, result);
            }
        }
        catch (OperationCanceledException)
        {
            Status = Strings.SyncCanceled;
        }
#pragma warning disable CA1031 // The folder is offline, the file is locked or unreadable: said, and tried again later.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Failed = true;
            Status = ex is DirectoryNotFoundException
                ? string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.SyncNoFolder), folder)
                : string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.SyncFailed), ex.Message);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private async Task RepeatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _ui.Post(() => _ = SyncIfOnAsync());
        }
    }

    /// <summary>Stops the repeated sync.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
