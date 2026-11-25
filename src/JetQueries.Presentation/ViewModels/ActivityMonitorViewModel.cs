using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Connections;
using JetQueries.Core.Jobs;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.Services;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>
/// The activity monitor of a connection: the server's sessions, what each runs and waits for, who blocks whom, and
/// ending a session. It reads on a connection of its own, through the job queue, every few seconds while it is
/// shown; it stops when hidden or closed (ADR 0009).
/// </summary>
public sealed partial class ActivityMonitorViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>The grid's column headers (each <see cref="ActivityRow.Cell"/> in this order).</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        Strings.ActivityColId, Strings.ActivityColState, Strings.ActivityColLogin, Strings.ActivityColHost, Strings.ActivityColProgram,
        Strings.ActivityColDatabase, Strings.ActivityColCommand, Strings.ActivityColWait, Strings.ActivityColWaitTime, Strings.ActivityColBlockedBy,
        Strings.ActivityColElapsed, Strings.ActivityColCpu, Strings.ActivityColReads, Strings.ActivityColWrites, Strings.ActivityColTransactions,
        Strings.ActivityColSql,
    ];

    /// <summary>How often it can read again, in seconds.</summary>
    public static IReadOnlyList<int> Intervals { get; } = [2, 5, 10, 30, 60];

    private readonly ConnectionSession _session;
    private readonly IJobQueue _jobs;
    private readonly IDialogService _dialogs;
    private QueryChannel? _channel;
    private ActivitySnapshot? _snapshot;
    private bool _disposed;
    private bool _reading;
    private int _loopVersion;

    public ActivityMonitorViewModel(ConnectionSession session, IJobQueue jobs, IDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _jobs = jobs;
        _dialogs = dialogs;
    }

    public Guid ConnectionId => _session.Definition.Id;

    /// <summary>The connection's name.</summary>
    public string ConnectionName => _session.Definition.Name;

    /// <summary>The environment (for the tab's colour and the production warning).</summary>
    public ConnectionEnvironment Environment => _session.Definition.Environment;

    /// <summary>The tab's title.</summary>
    public string HeaderText => Strings.ActivityTitle + " · " + ConnectionName;

    public string ToolTip => Strings.ActivityTitle + "\n" + ConnectionName;

    /// <summary>Colours the session's SQL.</summary>
    public ISqlLanguageService? LanguageService => _session.Provider.LanguageService;

    /// <summary>Sessions may be ended (not on a read-only connection).</summary>
    public bool CanKillSessions => !_session.Definition.IsReadOnly;

    /// <summary>The sessions shown, after the filter: blocked and blocking first, then running ones, then the rest.</summary>
    public ObservableCollection<ActivityRow> Rows { get; } = [];

    /// <summary>The blocking chains: each head blocker with the sessions waiting on it, and theirs.</summary>
    public ObservableCollection<BlockingNode> BlockingChains { get; } = [];

    /// <summary>Raised when the rows were replaced (the view keeps the chosen session by its id).</summary>
    public event EventHandler? RowsChanged;

    /// <summary>Raised to open a session's SQL in a new query tab.</summary>
    public event EventHandler<NewQueryRequest>? QueryRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(KillCommand), nameof(OpenSqlCommand))]
    public partial ActivityRow? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    /// <summary>Text the sessions must contain somewhere (id, login, host, program, database, command, wait, SQL).</summary>
    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    /// <summary>Show sleeping sessions too (without a request and without an open transaction).</summary>
    [ObservableProperty]
    public partial bool ShowIdle { get; set; }

    /// <summary>Show the server's own background sessions too.</summary>
    [ObservableProperty]
    public partial bool ShowSystem { get; set; }

    /// <summary>Read again on its own every <see cref="Interval"/> seconds while shown.</summary>
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
    public partial int SessionCount { get; set; }

    [ObservableProperty]
    public partial int ActiveCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlocking))]
    public partial int BlockedCount { get; set; }

    [ObservableProperty]
    public partial int HeadBlockerCount { get; set; }

    public bool HasBlocking => BlockedCount > 0;

    /// <summary>"Updated 14:02:11"; empty before the first read.</summary>
    [ObservableProperty]
    public partial string UpdatedText { get; set; } = string.Empty;

    /// <summary>Why only part of the server is visible; null when all is.</summary>
    [ObservableProperty]
    public partial string? Limitation { get; set; }

    /// <summary>Why the last read or kill failed; null when it did not.</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    partial void OnFilterTextChanged(string value) => Show();

    partial void OnShowIdleChanged(bool value) => Show();

    partial void OnShowSystemChanged(bool value) => Show();

    partial void OnIsShownChanged(bool value)
    {
        if (value)
        {
            _ = LoopAsync();
        }
        else
        {
            _loopVersion++;
        }
    }

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value)
        {
            _ = LoopAsync();
        }
        else
        {
            _loopVersion++;
        }
    }

    partial void OnIntervalChanged(int value) => _ = LoopAsync();

    // Reads now, then every Interval seconds while shown and auto: a newer loop (or hiding) ends an older one.
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

    /// <summary>Reads the sessions again.</summary>
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
            var snapshot = await Track(_jobs.Enqueue(
                "Read server activity",
                async context =>
                {
                    var channel = await ChannelAsync(context.CancellationToken).ConfigureAwait(false);
                    return await channel.Activity!.GetActivityAsync(context.CancellationToken).ConfigureAwait(false);
                }));
            if (_disposed)
            {
                return;
            }

            _snapshot = snapshot;
            Error = null;
            Limitation = snapshot.Limitation is null ? null : Strings.ActivityLimited;
            UpdatedText = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ActivityUpdated), snapshot.CapturedAt.ToLocalTime().ToString("T", CultureInfo.CurrentCulture));
            Show();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // The connection was closed: nothing more to read until the monitor is opened again.
            _channel = null;
            Error = Strings.ActivityDisconnected;
            AutoRefresh = false;
        }
#pragma warning disable CA1031 // What the server said belongs on the screen; the monitor keeps going.
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

    private bool CanKill(ActivityRow? row) => (row ?? Selected) is { IsOwn: false } && CanKillSessions;

    /// <summary>Ends the chosen session after asking (twice on a production connection).</summary>
    [RelayCommand(CanExecute = nameof(CanKill))]
    private async Task KillAsync(ActivityRow? row)
    {
        if ((row ?? Selected) is not { IsOwn: false } target || !CanKillSessions)
        {
            return;
        }

        var who = string.Join(" · ", new[] { target.Login, target.Host, target.Program }.Where(static s => !string.IsNullOrEmpty(s)));
        var message = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ActivityKillMessage), target.Id, who, ConnectionName);
        if (!await _dialogs.ConfirmAsync(Strings.ActivityKillTitle, message))
        {
            return;
        }

        if (Environment == ConnectionEnvironment.Production &&
            !await _dialogs.ConfirmAsync(Strings.ActivityKillTitle, string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ActivityKillProduction), ConnectionName)))
        {
            return;
        }

        try
        {
            await _jobs.Enqueue(
                "End session",
                async context =>
                {
                    var channel = await ChannelAsync(context.CancellationToken).ConfigureAwait(false);
                    await channel.Activity!.KillSessionAsync(target.Id, context.CancellationToken).ConfigureAwait(false);
                },
                JobPriority.High).Completion;
            Error = null;
        }
#pragma warning disable CA1031 // The server's refusal is shown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ex.Message;
        }

        await RefreshAsync();
    }

    private bool CanOpenSql(ActivityRow? row) => !string.IsNullOrWhiteSpace((row ?? Selected)?.Sql);

    /// <summary>Puts the session's SQL into a new query tab on this connection (not run).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenSql))]
    private void OpenSql(ActivityRow? row)
    {
        if ((row ?? Selected) is { Sql: { Length: > 0 } sql } target)
        {
            QueryRequested?.Invoke(this, new NewQueryRequest(ConnectionId, target.Database) { Text = sql });
        }
    }

    /// <summary>Chooses the session with <paramref name="id"/> (from the blocking chains).</summary>
    public void Choose(string id) => Selected = Rows.FirstOrDefault(r => r.Id == id) ?? Selected;

    private async Task<QueryChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { } open)
        {
            return open;
        }

        var channel = await _session.OpenChannelAsync(cancellationToken).ConfigureAwait(false);
        if (channel.Activity is null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw new NotSupportedException(Strings.ActivityUnsupported);
        }

        _channel = channel;
        return channel;
    }

    // The last snapshot through the filters, the counters and the blocking chains.
    private void Show()
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        var all = snapshot.Sessions;
        var blocks = all.Where(static s => s.BlockedBy is not null).GroupBy(static s => s.BlockedBy!).ToDictionary(static g => g.Key, static g => g.Count());
        var ids = all.Select(static s => s.Id).ToHashSet(StringComparer.Ordinal);
        var rows = all.Select(s => new ActivityRow(s, s.Id == snapshot.OwnSessionId, blocks.GetValueOrDefault(s.Id),
            blocks.ContainsKey(s.Id) && (s.BlockedBy is null || !ids.Contains(s.BlockedBy)))).ToList();

        SessionCount = rows.Count(static r => r.Session.IsUserSession);
        ActiveCount = rows.Count(static r => r.IsActive && r.Session.IsUserSession && !r.IsOwn);
        BlockedCount = rows.Count(static r => r.IsBlocked);
        HeadBlockerCount = rows.Count(static r => r.IsHeadBlocker);

        var filter = FilterText.Trim();
        var shown = rows
            .Where(r => ShowSystem || r.Session.IsUserSession || r.IsBlocked || r.Blocks > 0)
            .Where(r => ShowIdle || r.IsActive || r.IsBlocked || r.Blocks > 0 || r.Session.OpenTransactions > 0)
            .Where(r => filter.Length == 0 || r.Matches(filter))
            .OrderByDescending(static r => r.IsHeadBlocker)
            .ThenByDescending(static r => r.IsBlocked)
            .ThenByDescending(static r => r.IsActive)
            .ThenBy(static r => r.SortKey)
            .ToList();

        var chosen = Selected?.Id;
        Rows.Clear();
        foreach (var row in shown)
        {
            Rows.Add(row);
        }

        Selected = chosen is null ? null : Rows.FirstOrDefault(r => r.Id == chosen);

        BlockingChains.Clear();
        foreach (var head in rows.Where(static r => r.IsHeadBlocker))
        {
            BlockingChains.Add(Chain(head, rows, depth: 0));
        }

        RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static BlockingNode Chain(ActivityRow row, IReadOnlyList<ActivityRow> rows, int depth)
    {
        var node = new BlockingNode(row);
        if (depth < 32)
        {
            foreach (var waiting in rows.Where(r => r.Session.BlockedBy == row.Id && r.Id != row.Id))
            {
                node.Children.Add(Chain(waiting, rows, depth + 1));
            }
        }

        return node;
    }

    /// <summary>Stops reading and gives the monitor's connection back.</summary>
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

/// <summary>A session as the monitor's grid shows it.</summary>
public sealed class ActivityRow(ActivitySession session, bool isOwn, int blocks, bool isHeadBlocker)
{
    public ActivitySession Session { get; } = session;

    public string Id => Session.Id;

    /// <summary>The monitor's own session (never ended from here).</summary>
    public bool IsOwn { get; } = isOwn;

    /// <summary>How many sessions wait on this one.</summary>
    public int Blocks { get; } = blocks;

    /// <summary>It blocks others and nothing blocks it: the start of a chain.</summary>
    public bool IsHeadBlocker { get; } = isHeadBlocker;

    public bool IsBlocked => Session.BlockedBy is not null;

    /// <summary>It is running a request now.</summary>
    public bool IsActive => Session.Command is not null;

    public string? Login => Session.Login;

    public string? Host => Session.Host;

    public string? Program => Session.Program;

    public string? Database => Session.Database;

    public string? Sql => Session.Sql;

    /// <summary>"Engelliyor (2)", "Engellendi", the server's word otherwise.</summary>
    public string State { get; } = isHeadBlocker
        ? string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ActivityStateBlocking), blocks)
        : session.BlockedBy is not null ? Strings.ActivityStateBlocked : session.Status ?? string.Empty;

    /// <summary>The chosen session's facts for the side panel, label and value, only those it has.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Details
    {
        get
        {
            var runTime = string.Join(" · ", new[] { ElapsedText, CpuText.Length == 0 ? string.Empty : "CPU " + CpuText }.Where(static t => t.Length > 0));
            var io = string.Join(" · ", new[]
            {
                ReadsText.Length == 0 ? string.Empty : ReadsText + " " + Strings.ActivityColReads.ToLower(CultureInfo.CurrentCulture),
                WritesText.Length == 0 ? string.Empty : WritesText + " " + Strings.ActivityColWrites.ToLower(CultureInfo.CurrentCulture),
            }.Where(static t => t.Length > 0));
            var facts = new (string Label, string? Value)[]
            {
                (Strings.ActivityColState, State),
                (Strings.ActivityColLogin, Session.Login),
                (Strings.ActivityColHost, Session.Host),
                (Strings.ActivityColProgram, Session.Program),
                (Strings.ActivityColDatabase, Session.Database),
                (Strings.ActivityColCommand, Session.Command),
                (Strings.ActivityColWait, WaitText),
                (Strings.ActivityColBlockedBy, Session.BlockedBy),
                (Strings.ActivityColElapsed, runTime),
                (Strings.ActivityColReads, io),
                (Strings.ActivityColTransactions, Session.OpenTransactions is > 0 ? TransactionsText : null),
                (Strings.ActivityColLogin2, LoginTimeText),
            };
            return [.. facts.Where(static f => !string.IsNullOrWhiteSpace(f.Value)).Select(static f => new KeyValuePair<string, string>(f.Label, f.Value!))];
        }
    }

    /// <summary>"LCK_M_X · 12,4 sn"; empty when it is not waiting.</summary>
    public string WaitText => Session.WaitType is { } wait ? wait + (Session.WaitTime is { } time ? " · " + Duration(time) : string.Empty) : string.Empty;

    public string WaitTimeText => Duration(Session.WaitTime);

    public string ElapsedText => Duration(Session.Elapsed);

    public string CpuText => Duration(Session.CpuTime);

    public string ReadsText => Session.Reads?.ToString("N0", CultureInfo.CurrentCulture) ?? string.Empty;

    public string WritesText => Session.Writes?.ToString("N0", CultureInfo.CurrentCulture) ?? string.Empty;

    public string TransactionsText => Session.OpenTransactions?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

    public string LoginTimeText => Session.LoginTime?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>"Oturum 53".</summary>
    public string Title => Strings.ActivitySession + " " + Id;

    /// <summary>The cells of the grid's row, in the order of <see cref="ActivityMonitorViewModel.Columns"/>.</summary>
    public string? Cell(int column) => column switch
    {
        0 => Id,
        1 => State,
        2 => Session.Login,
        3 => Session.Host,
        4 => Session.Program,
        5 => Session.Database,
        6 => Session.Command,
        7 => Session.WaitType,
        8 => NullIfEmpty(WaitTimeText),
        9 => Session.BlockedBy,
        10 => NullIfEmpty(ElapsedText),
        11 => NullIfEmpty(CpuText),
        12 => NullIfEmpty(ReadsText),
        13 => NullIfEmpty(WritesText),
        14 => Session.OpenTransactions is > 0 ? TransactionsText : null,
        15 => NullIfEmpty(SqlLine),
        _ => null,
    };

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    /// <summary>The SQL on one line, shortened, for the grid.</summary>
    public string SqlLine => Session.Sql is { } sql ? OneLine(sql) : string.Empty;

    internal long SortKey => long.TryParse(Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : long.MaxValue;

    internal bool Matches(string filter)
    {
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        return new[] { Id, Session.Login, Session.Host, Session.Program, Session.Database, Session.Command, Session.WaitType, Session.Status, Session.Sql }
            .Any(text => text is not null && compare.IndexOf(text, filter, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
    }

    /// <summary>"850 ms", "12,4 sn", "03:15", "1:02:03".</summary>
    public static string Duration(TimeSpan? value) => value is not { } time
        ? string.Empty
        : time.TotalSeconds < 1 ? time.TotalMilliseconds.ToString("0", CultureInfo.CurrentCulture) + " ms"
        : time.TotalSeconds < 60 ? time.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture) + " " + Strings.UnitSeconds
        : time.TotalHours < 1 ? time.ToString(@"mm\:ss", CultureInfo.CurrentCulture)
        : ((int)time.TotalHours).ToString(CultureInfo.CurrentCulture) + time.ToString(@"\:mm\:ss", CultureInfo.CurrentCulture);

    private static string OneLine(string sql)
    {
        var builder = new StringBuilder(Math.Min(sql.Length, 200));
        var space = false;
        foreach (var c in sql)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
            if (builder.Length >= 200)
            {
                builder.Append('…');
                break;
            }
        }

        return builder.ToString();
    }
}

/// <summary>A session in a blocking chain, with the sessions waiting on it.</summary>
public sealed class BlockingNode(ActivityRow row)
{
    public ActivityRow Row { get; } = row;

    public ObservableCollection<BlockingNode> Children { get; } = [];

    /// <summary>"53 · ali@PC-12 · LCK_M_X 12,4 sn".</summary>
    public string Text { get; } = TextOf(row);

    private static string TextOf(ActivityRow row)
    {
        var session = row.Session;
        var parts = new List<string> { row.Id };
        var who = string.Join("@", new[] { session.Login, session.Host }.Where(static p => !string.IsNullOrEmpty(p)));
        if (who.Length > 0)
        {
            parts.Add(who);
        }

        if (session.WaitType is { } wait)
        {
            parts.Add(wait + " " + ActivityRow.Duration(session.WaitTime));
        }
        else if (session.OpenTransactions > 0 && !row.IsActive)
        {
            parts.Add(Strings.ActivityIdleInTransaction);
        }

        return string.Join(" · ", parts);
    }
}
