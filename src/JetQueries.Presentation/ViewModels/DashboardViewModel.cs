using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Connections;
using JetQueries.Core.Jobs;
using JetQueries.Presentation.Resources;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>A point of a time series.</summary>
/// <param name="At">When.</param>
/// <param name="Value">What.</param>
public readonly record struct ChartPoint(DateTimeOffset At, double Value);

/// <summary>A named time series of a chart (its colour is its position: series 1, series 2).</summary>
/// <param name="Name">The legend's text.</param>
/// <param name="Points">Oldest first.</param>
public sealed record ChartSeries(string Name, IReadOnlyList<ChartPoint> Points);

/// <summary>A bar of a bar list: its label, length and the value written beside it.</summary>
/// <param name="Label">What it is.</param>
/// <param name="Value">How long the bar is.</param>
/// <param name="Text">The value as shown.</param>
public sealed record BarItem(string Label, double Value, string Text);

/// <summary>A database's row in the dashboard's table.</summary>
/// <param name="Cells">Name, state, recovery, data, log, last full backup, last log backup, warning.</param>
/// <param name="HasWarning">A backup is missing or old.</param>
public sealed record DatabaseRow(IReadOnlyList<string?> Cells, bool HasWarning);

/// <summary>
/// A server's dashboard: what it is, its load as tiles and charts (CPU, batches, I/O, waits) and its databases.
/// The overview is read once; a sample every few seconds while shown, on a connection of its own; two samples make
/// the rates. Charts keep the last minutes (ADR 0011).
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>How often it can read again, in seconds.</summary>
    public static IReadOnlyList<int> Intervals { get; } = [2, 5, 10, 30];

    /// <summary>The rate charts keep this much.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>The CPU chart (one point a minute) keeps this much.</summary>
    public static readonly TimeSpan CpuWindow = TimeSpan.FromMinutes(60);

    private readonly ConnectionSession _session;
    private readonly IJobQueue _jobs;
    private readonly List<ChartPoint> _cpuServer = [];
    private readonly List<ChartPoint> _cpuOther = [];
    private readonly List<ChartPoint> _batches = [];
    private readonly List<ChartPoint> _reads = [];
    private readonly List<ChartPoint> _writes = [];
    private QueryChannel? _channel;
    private ServerOverview? _overview;
    private ServerSample? _first;
    private ServerSample? _last;
    private bool _disposed;
    private bool _reading;
    private int _loopVersion;

    public DashboardViewModel(ConnectionSession session, IJobQueue jobs)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _jobs = jobs;
    }

    public Guid ConnectionId => _session.Definition.Id;

    public string ConnectionName => _session.Definition.Name;

    public ConnectionEnvironment Environment => _session.Definition.Environment;

    public string HeaderText => Strings.DashTitle + " · " + ConnectionName;

    public string ToolTip => Strings.DashTitle + "\n" + ConnectionName;

    /// <summary>"SQL Server 2022 · Developer Edition · 16.0.4135.4 RTM".</summary>
    [ObservableProperty]
    public partial string ServerText { get; set; } = string.Empty;

    /// <summary>"SRV01 · 8 CPU · 32 GB · açık 3 gün 4 sa".</summary>
    [ObservableProperty]
    public partial string ServerDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CpuText { get; set; } = "—";

    [ObservableProperty]
    public partial string CpuDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MemoryText { get; set; } = "—";

    [ObservableProperty]
    public partial string MemoryDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BatchText { get; set; } = "—";

    [ObservableProperty]
    public partial string BatchDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RequestsText { get; set; } = "—";

    [ObservableProperty]
    public partial string RequestsDetail { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlocking))]
    public partial int BlockedCount { get; set; }

    public bool HasBlocking => BlockedCount > 0;

    [ObservableProperty]
    public partial string PleText { get; set; } = "—";

    [ObservableProperty]
    public partial string LatencyText { get; set; } = "—";

    [ObservableProperty]
    public partial string LatencyDetail { get; set; } = string.Empty;

    /// <summary>CPU of the server and of the rest of the machine, one point a minute.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ChartSeries> CpuSeries { get; set; } = [];

    /// <summary>Batches a second.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ChartSeries> BatchSeries { get; set; } = [];

    /// <summary>MB a second read and written.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ChartSeries> IoSeries { get; set; } = [];

    /// <summary>The waits that took most time, longest first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<BarItem> Waits { get; set; } = [];

    /// <summary>"Since the dashboard opened" or "since the server started".</summary>
    [ObservableProperty]
    public partial string WaitsTitle { get; set; } = Strings.DashWaits;

    /// <summary>The databases, their size and backups.</summary>
    public ObservableCollection<DatabaseRow> Databases { get; } = [];

    /// <summary>The table's headers.</summary>
    public static IReadOnlyList<string> DatabaseColumns { get; } =
    [
        Strings.ActivityColDatabase, Strings.ActivityColState, Strings.DashColRecovery, Strings.DashColData, Strings.DashColLog,
        Strings.DashColFullBackup, Strings.DashColLogBackup, Strings.DashColWarning,
    ];

    /// <summary>Raised when the databases were replaced.</summary>
    public event EventHandler? DatabasesChanged;

    [ObservableProperty]
    public partial bool AutoRefresh { get; set; } = true;

    [ObservableProperty]
    public partial int Interval { get; set; } = 5;

    /// <summary>The view is on screen; reading pauses while it is not.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelLoadCommand))]
    public partial bool IsLoading { get; set; }

    // The read now running, so that it can be stopped.
    private JetQueries.Core.Jobs.JobHandle? _load;

    private Task<T> Track<T>(JetQueries.Core.Jobs.JobHandle<T> job)
    {
        _load = job;
        return job.Completion;
    }

    /// <summary>Stops the read now running (the query on the server is cancelled).</summary>
    [RelayCommand(CanExecute = nameof(IsLoading))]
    private void CancelLoad() => _load?.Cancel();

    [ObservableProperty]
    public partial string UpdatedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; set; }

    partial void OnIsShownChanged(bool value) => Restart(value);

    partial void OnAutoRefreshChanged(bool value) => Restart(value);

    partial void OnIntervalChanged(int value) => Restart(true);

    private void Restart(bool run)
    {
        if (run)
        {
            _ = LoopAsync();
        }
        else
        {
            _loopVersion++;
        }
    }

    private async Task LoopAsync()
    {
        var version = ++_loopVersion;
        if (!IsShown || _disposed)
        {
            return;
        }

        await RefreshAsync();
        while (AutoRefresh && IsShown && !_disposed && version == _loopVersion)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Interval)));
            if (version != _loopVersion || !AutoRefresh || !IsShown || _disposed)
            {
                return;
            }

            await RefreshAsync();
        }
    }

    /// <summary>Reads a sample now (and the overview the first time, or again when asked).</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_reading || _disposed)
        {
            return;
        }

        _reading = true;
        IsLoading = true;
        try
        {
            var needsOverview = _overview is null;
            var (overview, sample) = await Track(_jobs.Enqueue("Read server dashboard", async context =>
            {
                var channel = await ChannelAsync(context.CancellationToken).ConfigureAwait(false);
                var read = needsOverview ? await channel.Dashboard!.GetOverviewAsync(context.CancellationToken).ConfigureAwait(false) : null;
                return (read, await channel.Dashboard!.GetSampleAsync(context.CancellationToken).ConfigureAwait(false));
            }));
            if (_disposed)
            {
                return;
            }

            if (overview is not null)
            {
                ShowOverview(overview);
            }

            Add(sample);
            Error = null;
            UpdatedText = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ActivityUpdated), sample.At.ToLocalTime().ToString("T", CultureInfo.CurrentCulture));
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            _channel = null;
            Error = Strings.ActivityDisconnected;
            AutoRefresh = false;
        }
#pragma warning disable CA1031 // What the server said belongs on the screen; the dashboard keeps going.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ex.Message;
        }
        finally
        {
            _reading = false;
            IsLoading = false;
        }
    }

    /// <summary>Reads the overview (databases, backups) again with the next sample.</summary>
    [RelayCommand]
    private Task ReloadOverviewAsync()
    {
        _overview = null;
        return RefreshAsync();
    }

    private async Task<QueryChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { } open)
        {
            return open;
        }

        var channel = await _session.OpenChannelAsync(cancellationToken).ConfigureAwait(false);
        if (channel.Dashboard is null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw new NotSupportedException(Strings.DashUnsupported);
        }

        _channel = channel;
        return channel;
    }

    private void ShowOverview(ServerOverview overview)
    {
        _overview = overview;
        ServerText = string.Join(" · ", new[] { overview.Product, overview.Edition, overview.Version }.Where(static p => !string.IsNullOrWhiteSpace(p)));
        var parts = new List<string>();
        if (overview.Host is { Length: > 0 } host)
        {
            parts.Add(host);
        }

        if (overview.CpuCount is { } cpus)
        {
            parts.Add(cpus.ToString(CultureInfo.CurrentCulture) + " CPU");
        }

        if (overview.PhysicalMemoryMb is { } memory)
        {
            parts.Add(Gigabytes(memory));
        }

        if (overview.StartedAt is { } started)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.DashUptime), Uptime(DateTimeOffset.Now - started)));
        }

        ServerDetail = string.Join(" · ", parts);
        if (_cpuServer.Count == 0)
        {
            // The server's own clock: the last hour of what it recorded.
            var newest = overview.CpuHistory.Count > 0 ? overview.CpuHistory[^1].At : DateTimeOffset.Now;
            foreach (var point in overview.CpuHistory.Where(p => p.At >= newest - CpuWindow))
            {
                _cpuServer.Add(new ChartPoint(point.At, point.ServerPercent));
                _cpuOther.Add(new ChartPoint(point.At, point.OtherPercent));
            }
        }

        Databases.Clear();
        foreach (var database in overview.Databases)
        {
            Databases.Add(DatabaseRowOf(database, DateTimeOffset.Now));
        }

        DatabasesChanged?.Invoke(this, EventArgs.Empty);
        ShowCpu();
    }

    /// <summary>A database's row, with a warning when its backups are missing or old.</summary>
    public static DatabaseRow DatabaseRowOf(DatabaseSummary database, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(database);
        string? warning = null;
        var system = database.Name is "master" or "model" or "msdb" or "tempdb";
        if (database.BackupsKnown && database.Name != "tempdb" && string.Equals(database.State, "ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            if (database.LastFullBackup is not { } full)
            {
                warning = Strings.DashNoFullBackup;
            }
            else if (now - full > TimeSpan.FromDays(7))
            {
                warning = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.DashOldFullBackup), (int)(now - full).TotalDays);
            }
            else if (!system && string.Equals(database.RecoveryModel, "FULL", StringComparison.OrdinalIgnoreCase) &&
                     (database.LastLogBackup is not { } log || now - log > TimeSpan.FromDays(1)))
            {
                warning = Strings.DashNoLogBackup;
            }
        }

        string When(DateTimeOffset? at) => at?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? (database.BackupsKnown ? "—" : "?");
        return new DatabaseRow(
        [
            database.Name, database.State, database.RecoveryModel, Megabytes(database.DataMb), Megabytes(database.LogMb),
            When(database.LastFullBackup), When(database.LastLogBackup), warning,
        ], warning is not null);
    }

    // A new sample: tiles from it, rates from it and the one before, charts trimmed to their window.
    private void Add(ServerSample sample)
    {
        _first ??= sample;
        var previous = _last;
        _last = sample;

        if (sample.Cpu is { } cpu && (_cpuServer.Count == 0 || cpu.At > _cpuServer[^1].At))
        {
            _cpuServer.Add(new ChartPoint(cpu.At, cpu.ServerPercent));
            _cpuOther.Add(new ChartPoint(cpu.At, cpu.OtherPercent));
        }

        CpuText = sample.Cpu is { } now ? Percent(now.ServerPercent + now.OtherPercent) : "—";
        CpuDetail = sample.Cpu is { } latest
            ? string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.DashCpuDetail), Percent(latest.ServerPercent), Percent(latest.OtherPercent))
            : Strings.DashNoCpu;
        MemoryText = sample.MemoryUsedMb is { } used ? Gigabytes(used) : "—";
        MemoryDetail = string.Join(" · ", new[]
        {
            sample.MemoryTargetMb is { } target ? string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.DashMemoryTarget), Gigabytes(target)) : null,
            sample.AvailableMemoryMb is { } free ? string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.DashMemoryFree), Gigabytes(free)) : null,
        }.OfType<string>());
        RequestsText = sample.ActiveRequests?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";
        RequestsDetail = sample.Connections is { } connections
            ? string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.DashConnections), connections)
            : string.Empty;
        BlockedCount = sample.BlockedRequests ?? 0;
        PleText = sample.PageLifeExpectancy is { } ple ? ple.ToString("N0", CultureInfo.CurrentCulture) + " " + Strings.UnitSeconds : "—";

        if (previous is not null && (sample.At - previous.At).TotalSeconds is var seconds and > 0.5)
        {
            var batches = Rate(previous.BatchRequests, sample.BatchRequests, seconds);
            var compilations = Rate(previous.Compilations, sample.Compilations, seconds);
            var read = Rate(previous.BytesRead, sample.BytesRead, seconds) / 1024 / 1024;
            var written = Rate(previous.BytesWritten, sample.BytesWritten, seconds) / 1024 / 1024;
            var operations = sample.IoOperations - previous.IoOperations;
            var stall = sample.IoStallMs - previous.IoStallMs;
            if (batches is { } b)
            {
                _batches.Add(new ChartPoint(sample.At, b));
                BatchText = b.ToString("N0", CultureInfo.CurrentCulture);
            }

            BatchDetail = compilations is { } c ? string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.DashCompilations), c.ToString("N0", CultureInfo.CurrentCulture)) : string.Empty;
            if (read is { } r && written is { } w)
            {
                _reads.Add(new ChartPoint(sample.At, r));
                _writes.Add(new ChartPoint(sample.At, w));
                LatencyDetail = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.DashIoDetail), r.ToString("0.0", CultureInfo.CurrentCulture), w.ToString("0.0", CultureInfo.CurrentCulture));
            }

            LatencyText = operations is > 0 && stall is >= 0 ? ((double)stall.Value / operations.Value).ToString("0.0", CultureInfo.CurrentCulture) + " ms" : LatencyText;
        }

        Trim(_batches, Window, sample.At);
        Trim(_reads, Window, sample.At);
        Trim(_writes, Window, sample.At);
        Trim(_cpuServer, CpuWindow, sample.At);
        Trim(_cpuOther, CpuWindow, sample.At);
        BatchSeries = [new ChartSeries(Strings.DashBatches, [.. _batches])];
        IoSeries = [new ChartSeries(Strings.DashRead, [.. _reads]), new ChartSeries(Strings.DashWritten, [.. _writes])];
        ShowCpu();
        ShowWaits(sample);
    }

    private void ShowCpu() =>
        CpuSeries = [new ChartSeries(Strings.DashCpuServer, [.. _cpuServer]), new ChartSeries(Strings.DashCpuOther, [.. _cpuOther])];

    // Since the dashboard opened when there is a difference to show; until then since the server started.
    private void ShowWaits(ServerSample sample)
    {
        var first = _first is { } opened && !ReferenceEquals(opened, sample) ? opened.Waits.ToDictionary(static w => w.Type, static w => w.WaitMs) : null;
        var waits = sample.Waits
            .Select(w => (w.Type, Ms: first is null ? w.WaitMs : w.WaitMs - first.GetValueOrDefault(w.Type, w.WaitMs)))
            .Where(static w => w.Ms > 0)
            .OrderByDescending(static w => w.Ms)
            .Take(8)
            .ToList();
        if (first is not null && waits.Count == 0)
        {
            WaitsTitle = Strings.DashWaitsOpened;
            Waits = [];
            return;
        }

        WaitsTitle = first is null ? Strings.DashWaitsStarted : Strings.DashWaitsOpened;
        Waits = [.. waits.Select(static w => new BarItem(w.Type, w.Ms, ActivityRow.Duration(TimeSpan.FromMilliseconds(w.Ms))))];
    }

    private static double? Rate(long? before, long? after, double seconds) =>
        before is { } b && after is { } a && a >= b ? (a - b) / seconds : null;

    private static void Trim(List<ChartPoint> points, TimeSpan window, DateTimeOffset now) => points.RemoveAll(p => p.At < now - window);

    private static string Percent(double value) => value.ToString("0", CultureInfo.CurrentCulture) + " %";

    private static string Gigabytes(long mb) => mb >= 1024 ? (mb / 1024d).ToString("0.0", CultureInfo.CurrentCulture) + " GB" : mb.ToString("N0", CultureInfo.CurrentCulture) + " MB";

    private static string Megabytes(double mb) => mb >= 1024 ? (mb / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " GB" : mb.ToString("N0", CultureInfo.CurrentCulture) + " MB";

    private static string Uptime(TimeSpan time) => time.TotalDays >= 1
        ? string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.DashDaysHours), (int)time.TotalDays, time.Hours)
        : string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.DashHoursMinutes), (int)time.TotalHours, time.Minutes);

    /// <summary>Stops reading and gives the dashboard's connection back.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loopVersion++;
        if (_channel is { } channel)
        {
            _channel = null;
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
