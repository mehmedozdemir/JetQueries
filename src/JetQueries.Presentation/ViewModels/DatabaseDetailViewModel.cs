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

/// <summary>A headline figure of the database detail screen, with a status when it needs attention.</summary>
/// <param name="Value">The figure ("1,2 GB", "12 %", "3 gün önce").</param>
/// <param name="Detail">A line under it.</param>
/// <param name="Status">Ok, Warning or Critical (shown with an icon and a word).</param>
public sealed record DetailTile(string Value, string Detail, HealthSeverity Status)
{
    /// <summary>The status needs attention.</summary>
    public bool IsAttention => Status is HealthSeverity.Warning or HealthSeverity.Critical;

    /// <summary>"Uyarı" / "Kritik"; empty when OK.</summary>
    public string StatusText => IsAttention ? HealthRow.SeverityName(Status) : string.Empty;

    /// <summary>Nothing read yet.</summary>
    public static DetailTile Empty { get; } = new("—", string.Empty, HealthSeverity.Ok);
}

/// <summary>
/// One database in depth: its settings, space of data and log, virtual log files, backups, integrity check, largest
/// tables and files; read on a connection of its own when the tab opens and on refresh (ADR 0014).
/// </summary>
public sealed partial class DatabaseDetailViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ConnectionSession _session;
    private readonly IJobQueue _jobs;
    private QueryChannel? _channel;
    private bool _disposed;

    public DatabaseDetailViewModel(ConnectionSession session, IJobQueue jobs, string database)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrEmpty(database);
        _session = session;
        _jobs = jobs;
        Database = database;
    }

    public Guid ConnectionId => _session.Definition.Id;

    public string ConnectionName => _session.Definition.Name;

    public ConnectionEnvironment Environment => _session.Definition.Environment;

    /// <summary>The database shown.</summary>
    public string Database { get; }

    public string HeaderText => Database + " · " + ConnectionName;

    public string ToolTip => Strings.DbDetailTitle + "\n" + ConnectionName + " · " + Database;

    /// <summary>"ONLINE · FULL · 160 · Turkish_CI_AS".</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    /// <summary>"Sahibi sa · oluşturuldu 12.03.2024".</summary>
    [ObservableProperty]
    public partial string SummaryDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DetailTile Data { get; set; } = DetailTile.Empty;

    [ObservableProperty]
    public partial DetailTile Log { get; set; } = DetailTile.Empty;

    [ObservableProperty]
    public partial DetailTile Vlf { get; set; } = DetailTile.Empty;

    [ObservableProperty]
    public partial DetailTile FullBackup { get; set; } = DetailTile.Empty;

    [ObservableProperty]
    public partial DetailTile LogBackup { get; set; } = DetailTile.Empty;

    [ObservableProperty]
    public partial DetailTile CheckDb { get; set; } = DetailTile.Empty;

    [ObservableProperty]
    public partial DetailTile Tables { get; set; } = DetailTile.Empty;

    /// <summary>The ten largest tables as bars.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<BarItem> LargestTables { get; set; } = [];

    /// <summary>The settings, label and value.</summary>
    public ObservableCollection<KeyValuePair<string, string>> Options { get; } = [];

    /// <summary>Tables: name, rows, reserved, data, indexes.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<IReadOnlyList<string?>> TableRows { get; set; } = [];

    /// <summary>Files: name, type, filegroup, size, used, free, growth, max, path.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<IReadOnlyList<string?>> FileRows { get; set; } = [];

    /// <summary>Backups: kind, finished, took, size, compressed, copy only, device.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<IReadOnlyList<string?>> BackupRows { get; set; } = [];

    public static IReadOnlyList<string> TableColumns { get; } =
        [Strings.PerfColTable, Strings.DbColRows, Strings.DbColReserved, Strings.DashColData, Strings.DbColIndexes];

    public static IReadOnlyList<string> FileColumns { get; } =
        [Strings.DbColFile, Strings.PerfColKind, Strings.DbColFilegroup, Strings.PerfColSize, Strings.DbColUsed, Strings.DbColFree, Strings.DbColGrowth, Strings.DbColMax, Strings.DbColPath];

    public static IReadOnlyList<string> BackupColumns { get; } =
        [Strings.PerfColKind, Strings.DbColFinished, Strings.DbColTook, Strings.PerfColSize, Strings.DbColCompressed, Strings.DbColCopyOnly, Strings.DbColDevice];

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
    public partial string LoadedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>Reads the database again.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_disposed || IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var database = Database;
            var detail = await Track(_jobs.Enqueue("Read database detail", async context =>
            {
                var channel = await ChannelAsync(context.CancellationToken).ConfigureAwait(false);
                return await channel.DatabaseDetails!.GetDatabaseDetailAsync(database, context.CancellationToken).ConfigureAwait(false);
            }));
            if (_disposed)
            {
                return;
            }

            Show(detail, DateTimeOffset.Now);
            Error = null;
            LoadedText = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ActivityUpdated), DateTime.Now.ToString("T", CultureInfo.CurrentCulture));
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // A missing permission or an offline database is shown where the figures would be.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Fills the screen from what was read; <paramref name="now"/> judges the ages of backups and checks.</summary>
    public void Show(DatabaseDetail detail, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(detail);
        Summary = string.Join(" · ", new[]
        {
            detail.State, detail.RecoveryModel,
            detail.CompatibilityLevel is { } level ? Strings.DbCompatibility + " " + level.ToString(CultureInfo.CurrentCulture) : null,
            detail.Collation,
        }.Where(static p => !string.IsNullOrEmpty(p)));
        SummaryDetail = string.Join(" · ", new[]
        {
            detail.Owner is { } owner ? Format(Strings.DbOwner, owner) : null,
            detail.Created is { } created ? Format(Strings.DbCreated, created.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)) : null,
        }.OfType<string>());

        // Space: data used of allocated; the log's fullness and what holds it.
        Data = new DetailTile(
            Size(detail.DataMb),
            detail.DataUsedMb is { } used && detail.DataMb > 0
                ? Format(Strings.DbDataUsed, Size(used), (used / detail.DataMb * 100).ToString("0", CultureInfo.CurrentCulture))
                : string.Empty,
            HealthSeverity.Ok);
        var logUsed = detail.LogUsedPercent;
        var held = detail.LogReuseWait is { } wait and not ("NOTHING" or "CHECKPOINT");
        Log = new DetailTile(
            Size(detail.LogMb),
            string.Join(" · ", new[]
            {
                logUsed is { } fullness ? Format(Strings.DbLogUsed, fullness.ToString("0", CultureInfo.CurrentCulture)) : null,
                held ? Format(Strings.DbLogHeldBy, detail.LogReuseWait!) : null,
            }.OfType<string>()),
            logUsed > 90 ? HealthSeverity.Critical : logUsed > 75 || (held && logUsed > 50) ? HealthSeverity.Warning : HealthSeverity.Ok);
        Vlf = detail.VirtualLogFiles is { } vlf
            ? new DetailTile(vlf.ToString("N0", CultureInfo.CurrentCulture), Strings.DbVlfDetail,
                vlf > 1000 ? HealthSeverity.Critical : vlf > 300 ? HealthSeverity.Warning : HealthSeverity.Ok)
            : DetailTile.Empty;

        // Backups and the integrity check, by age.
        var full = detail.Backups.Where(static b => b.Kind == BackupKind.Full).Select(static b => (DateTimeOffset?)b.Finished).FirstOrDefault();
        var log = detail.Backups.Where(static b => b.Kind == BackupKind.Log).Select(static b => (DateTimeOffset?)b.Finished).FirstOrDefault();
        var isTempdb = detail.Name.Equals("tempdb", StringComparison.OrdinalIgnoreCase);
        FullBackup = !detail.BackupsKnown
            ? new DetailTile("?", Strings.DbBackupsUnknown, HealthSeverity.Ok)
            : Age(full, now, isTempdb ? null : TimeSpan.FromDays(1), isTempdb ? null : TimeSpan.FromDays(7), critical: true);
        LogBackup = !detail.BackupsKnown
            ? new DetailTile("?", Strings.DbBackupsUnknown, HealthSeverity.Ok)
            : !string.Equals(detail.RecoveryModel, "FULL", StringComparison.OrdinalIgnoreCase)
                ? new DetailTile("—", Format(Strings.DbLogBackupNotNeeded, detail.RecoveryModel ?? "SIMPLE"), HealthSeverity.Ok)
                : Age(log, now, TimeSpan.FromHours(1), TimeSpan.FromDays(1), critical: true);
        CheckDb = detail.LastGoodCheckDb is { } checkDb
            ? Age(checkDb.Year < 1901 ? null : checkDb, now, TimeSpan.FromDays(7), TimeSpan.FromDays(30), critical: false)
            : DetailTile.Empty;
        Tables = new DetailTile(detail.TableCount.ToString("N0", CultureInfo.CurrentCulture), Format(Strings.DbRows, detail.TotalRows.ToString("N0", CultureInfo.CurrentCulture)), HealthSeverity.Ok);

        LargestTables = [.. detail.Tables.Take(10).Where(static t => t.ReservedMb > 0)
            .Select(t => new BarItem(t.Name, t.ReservedMb, Size(t.ReservedMb) + " · " + Format(Strings.DbRows, t.Rows.ToString("N0", CultureInfo.CurrentCulture))))];
        TableRows = [.. detail.Tables.Select(static t => (IReadOnlyList<string?>)
            [t.Name, t.Rows.ToString("N0", CultureInfo.CurrentCulture), Size(t.ReservedMb), Size(t.DataMb), Size(t.IndexMb)])];
        FileRows = [.. detail.Files.Select(static f => (IReadOnlyList<string?>)
        [
            f.Name, f.Type, f.Filegroup, Size(f.SizeMb), f.UsedMb is { } used ? Size(used) : null,
            f.UsedMb is { } u && f.SizeMb > 0 ? ((f.SizeMb - u) / f.SizeMb * 100).ToString("0", CultureInfo.CurrentCulture) + " %" : null,
            f.Growth, f.MaxSize, f.Path,
        ])];
        BackupRows = [.. detail.Backups.Select(static b => (IReadOnlyList<string?>)
        [
            KindName(b.Kind), b.Finished.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            b.Started is { } started ? ActivityRow.Duration(b.Finished - started) : null,
            b.SizeMb is { } size ? Size(size) : null, b.CompressedMb is { } compressed ? Size(compressed) : null,
            b.CopyOnly ? "✓" : null, b.Device,
        ])];

        Options.Clear();
        foreach (var option in detail.Options.Where(static o => !string.IsNullOrEmpty(o.Value)))
        {
            Options.Add(new KeyValuePair<string, string>(Strings.Find("DbProp_" + option.Key) ?? option.Label, option.Value!));
        }
    }

    /// <summary>"Tam", "Fark", "Log", "Diğer".</summary>
    public static string KindName(BackupKind kind) => kind switch
    {
        BackupKind.Full => Strings.DbBackupFull,
        BackupKind.Differential => Strings.DbBackupDifferential,
        BackupKind.Log => Strings.DbBackupLog,
        _ => Strings.DbBackupOther,
    };

    // "3 saat önce" with a status: OK until warn, Warning until bad, then Critical (or Warning when not critical); none is the worst.
    private static DetailTile Age(DateTimeOffset? at, DateTimeOffset now, TimeSpan? warn, TimeSpan? bad, bool critical)
    {
        var worst = critical ? HealthSeverity.Critical : HealthSeverity.Warning;
        if (at is not { } when)
        {
            return new DetailTile(Strings.DbNever, string.Empty, warn is null ? HealthSeverity.Ok : worst);
        }

        var age = now - when;
        var status = bad is { } b && age > b ? worst : warn is { } w && age > w ? HealthSeverity.Warning : HealthSeverity.Ok;
        return new DetailTile(Ago(age), when.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), status);
    }

    private static string Ago(TimeSpan age) => age.TotalDays >= 1
        ? Format(Strings.DbDaysAgo, ((int)age.TotalDays).ToString(CultureInfo.CurrentCulture))
        : age.TotalHours >= 1 ? Format(Strings.DbHoursAgo, ((int)age.TotalHours).ToString(CultureInfo.CurrentCulture))
        : Format(Strings.DbMinutesAgo, Math.Max(0, (int)age.TotalMinutes).ToString(CultureInfo.CurrentCulture));

    private static string Size(double mb) => mb >= 1024 * 1024
        ? (mb / 1024 / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " TB"
        : mb >= 1024 ? (mb / 1024).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
        : mb >= 10 ? mb.ToString("N0", CultureInfo.CurrentCulture) + " MB"
        : mb.ToString("0.#", CultureInfo.CurrentCulture) + " MB";

    private static string Format(string format, params object[] values) => string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(format), values);

    private async Task<QueryChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { } open)
        {
            return open;
        }

        var channel = await _session.OpenChannelAsync(cancellationToken).ConfigureAwait(false);
        if (channel.DatabaseDetails is null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw new NotSupportedException(Strings.DbDetailUnsupported);
        }

        _channel = channel;
        return channel;
    }

    /// <summary>Gives the screen's connection back.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_channel is { } channel)
        {
            _channel = null;
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
