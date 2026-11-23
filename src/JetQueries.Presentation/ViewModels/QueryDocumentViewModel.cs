using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Connections;
using JetQueries.Core.Jobs;
using JetQueries.Core.Metadata;
using JetQueries.Core.Queries;
using JetQueries.Core.Sql;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.Services;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>A tab in a query's results area.</summary>
public interface IResultTab
{
    /// <summary>Caption of the tab.</summary>
    string Title { get; }
}

/// <summary>A column of a result set as a grid shows it.</summary>
/// <param name="Header">The column name.</param>
/// <param name="Width">A starting width in device-independent pixels, from the header and the first rows.</param>
/// <param name="IsNumeric">Numbers are aligned to the right.</param>
/// <param name="ClrType">The .NET type of the values.</param>
/// <param name="DataType">The database's name of the type.</param>
public sealed record ResultGridColumn(string Header, double Width, bool IsNumeric, Type ClrType, string DataType = "");

/// <summary>An open connection a query can run on.</summary>
public sealed record SessionOption(ConnectionSession Session)
{
    public string Name => Session.Definition.Name;
}

/// <summary>A line in the Messages tab.</summary>
public sealed record QueryMessageLine(string Text, bool IsError);

/// <summary>The Messages tab of a query's results.</summary>
public sealed class QueryMessagesTab : IResultTab
{
    public string Title => Strings.MessagesTitle;

    public ObservableCollection<QueryMessageLine> Lines { get; } = [];
}

/// <summary>
/// One result set, filled while the query runs. It is the row source of the results grid: rows are appended
/// on the UI thread in the batches the provider delivers, and the grid only ever asks for the cells it shows.
/// </summary>
public sealed class QueryResultSet : IResultTab
{
    private readonly List<object?[]> _rows = [];

    /// <param name="title">Caption of the tab.</param>
    /// <param name="columns">The columns of the result set.</param>
    /// <param name="sample">The first rows, used only to choose column widths; they are not added.</param>
    /// <param name="jobs">Where sorting and filtering run; without one they run on the calling thread.</param>
    /// <param name="ui">Where their results are applied.</param>
    public QueryResultSet(
        string title, IReadOnlyList<ResultColumn> columns, IReadOnlyList<object?[]>? sample = null, IJobQueue? jobs = null, IUiDispatcher? ui = null)
    {
        View = new ResultSetView(this, jobs, ui);
        ArgumentNullException.ThrowIfNull(columns);
        Title = title;
        var gridColumns = new ResultGridColumn[columns.Count];
        for (var i = 0; i < gridColumns.Length; i++)
        {
            var name = columns[i].Name.Length == 0 ? "(no name)" : columns[i].Name;

            // Wide enough for the header and for what the first rows hold, within reason; the user can still
            // see long values by widening the column later.
            var longest = name.Length + 1;
            foreach (var row in (sample ?? []).Take(200))
            {
                longest = Math.Max(longest, Format(row[i])?.Length ?? 4);
            }

            gridColumns[i] = new ResultGridColumn(name, Math.Clamp((longest * 7.4) + 24, 64, 420), IsNumeric(columns[i].ClrType), columns[i].ClrType, columns[i].DataType);
        }

        Columns = gridColumns;
    }

    public event EventHandler? RowCountChanged;

    public string Title { get; }

    public IReadOnlyList<ResultGridColumn> Columns { get; }

    public int RowCount => _rows.Count;

    /// <summary>The order and filters the grid shows the rows in.</summary>
    public ResultSetView View { get; }

    /// <summary>The value of a cell as the provider returned it.</summary>
    public object? GetValue(int row, int column) => _rows[row][column];

    /// <summary>The rows received so far, as a list that later rows do not change.</summary>
    internal object?[][] SnapshotRows() => [.. _rows];

    /// <summary>A value as the grid shows it.</summary>
    internal static string? FormatValue(object? value) => Format(value);

    public void AddRows(IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _rows.AddRange(rows);
        RowCountChanged?.Invoke(this, EventArgs.Empty);
    }

    public string? GetText(int row, int column) => Format(_rows[row][column]);

    private static string? Format(object? value) => value switch
    {
        null => null,
        string text => text,
        DateTime { TimeOfDay.Ticks: 0 } date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture),
        byte[] bytes => "0x" + Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, 32)) + (bytes.Length > 32 ? "…" : string.Empty),
        bool flag => flag ? "1" : "0",

        // Invariant: a decimal point stays a point whatever the Windows locale, as in the SQL the user writes.
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        var other => other.ToString(),
    };

    private static bool IsNumeric(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(decimal) ||
        type == typeof(double) || type == typeof(float);
}

/// <summary>View model of one query tab: the text, the connection it runs on, and what the last run returned.</summary>
public partial class QueryDocumentViewModel : ObservableObject
{
    /// <summary>Most rows kept per result set; a larger result is cut off with a message.</summary>
    public const int MaxRows = 100_000;

    private readonly IConnectionManager _manager;
    private readonly IMetadataService _metadata;
    private readonly IQueryHistory _history;
    private readonly IJobQueue _jobs;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private JobHandle? _run;

    // The window's own connection within the selected session (see QueryChannel), opened when a session is chosen.
    private ConnectionSession? _channelSession;
    private QueryChannel? _channel;
    private Task<QueryChannel?>? _channelOpening;
    private bool _applyingDatabase;
    private string? _channelError;
    private Task _databaseChange = Task.CompletedTask;

    /// <param name="database">A database to start in (New query on a database node); null for the connection's default.</param>
    public QueryDocumentViewModel(
        string title, IConnectionManager manager, IMetadataService metadata, IQueryHistory history, IJobQueue jobs, IDialogService dialogs, IUiDispatcher ui,
        string? database = null)
    {
        Title = title;

        // Set before a session is chosen below: choosing one starts opening the window's connection.
        PendingDatabase = database;
        _manager = manager;
        _metadata = metadata;
        _history = history;
        _jobs = jobs;
        _dialogs = dialogs;
        _ui = ui;
        Text = string.Empty;
        Status = string.Empty;
        Tabs.Add(Messages);
        SelectedTab = Messages;
        RefreshSessions();
    }

    public string Title { get; }

    public ObservableCollection<SessionOption> Sessions { get; } = [];

    /// <summary>The result sets of the last run, followed by the Messages tab.</summary>
    public ObservableCollection<IResultTab> Tabs { get; } = [];

    public QueryMessagesTab Messages { get; } = new();

    /// <summary>The text selected in the editor; when not empty, only this part is run.</summary>
    public string SelectedText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string Text { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyPropertyChangedFor(nameof(LanguageService))]
    public partial SessionOption? SelectedSession { get; set; }

    /// <summary>The databases (or, for Oracle, schemas) of the connection, for the window's database selector.</summary>
    public ObservableCollection<string> Databases { get; } = [];

    /// <summary>
    /// The window's current database (schema for Oracle), as the server reports it; it follows a <c>USE</c> run in
    /// the window. Choosing another one switches the window's connection, as SSMS's database selector does.
    /// </summary>
    [ObservableProperty]
    public partial string? SelectedDatabase { get; set; }

    /// <summary>Whether the connection can switch databases (SQLite cannot).</summary>
    [ObservableProperty]
    public partial bool CanChangeDatabase { get; set; }

    /// <summary>Whether the selector switches schemas (Oracle) rather than databases.</summary>
    [ObservableProperty]
    public partial bool SwitchesSchema { get; set; }

    /// <summary>A database to switch to as soon as the window's connection is open (New query on a database node).</summary>
    public string? PendingDatabase { get; set; }

    /// <summary>
    /// The language of the chosen connection: its provider's language service, or plain keyword colouring when
    /// there is no connection or the provider has none.
    /// </summary>
    public ISqlLanguageService LanguageService => SelectedSession?.Session.Provider.LanguageService ?? BasicSqlLanguageService.Instance;

    /// <summary>Raised when "go to definition" found source text to show: a title, the text, and the connection it came from.</summary>
    public event EventHandler<(string Title, string Text, Guid ConnectionId)>? DefinitionOpened;

    /// <summary>The syntax errors of a text, computed as a low-priority background job.</summary>
    public Task<IReadOnlyList<SqlDiagnostic>> DiagnoseAsync(string text, CancellationToken cancellationToken)
    {
        if (SelectedSession?.Session.Provider.LanguageService is not { } service)
        {
            return Task.FromResult<IReadOnlyList<SqlDiagnostic>>([]);
        }

        var document = new SqlDocument(text);
        return _jobs.Enqueue(
            "Check syntax",
            context => service.GetDiagnosticsAsync(document, context.CancellationToken),
            JobPriority.Low,
            cancellationToken).Completion;
    }

    /// <summary>Finds what the name at <paramref name="offset"/> refers to and opens its source in a new tab.</summary>
    public async Task GoToDefinitionAsync(string text, int offset)
    {
        if (SelectedSession?.Session is not { Provider.LanguageService: { } service } session)
        {
            return;
        }

        try
        {
            var metadata = new SessionMetadataProvider(session, _metadata);
            var document = Document(text, metadata);
            var found = await _jobs.Enqueue<(DatabaseObject? Target, string? Source)>(
                "Go to definition",
                async context =>
                {
                    var definition = await service.GetDefinitionAsync(document, offset, context.CancellationToken).ConfigureAwait(false);
                    if (definition?.Target is not { } target)
                    {
                        return (null, null);
                    }

                    return (target, await metadata.GetDefinitionAsync(target, context.CancellationToken).ConfigureAwait(false));
                },
                JobPriority.High).Completion;

            if (found.Target is null)
            {
                Status = Strings.DefinitionNotFound;
            }
            else if (found.Source is null)
            {
                Status = Format(Strings.DefinitionUnavailable, found.Target.Name);
            }
            else
            {
                DefinitionOpened?.Invoke(this, (found.Target.Name.Name, found.Source, session.Definition.Id));
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Navigation is a convenience; the server's reason is shown, nothing else is disturbed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Status = ex.Message;
        }
    }

    /// <summary>Completion items for a caret position, computed as a background job against cached metadata.</summary>
    public Task<IReadOnlyList<CompletionItem>> CompleteAsync(string text, int offset, CancellationToken cancellationToken)
    {
        if (SelectedSession?.Session is not { Provider.LanguageService: { } service } session)
        {
            return Task.FromResult<IReadOnlyList<CompletionItem>>([]);
        }

        var document = Document(text, new SessionMetadataProvider(session, _metadata));
        return _jobs.Enqueue(
            "Completion",
            context => service.GetCompletionsAsync(document, offset, context.CancellationToken),
            JobPriority.High,
            cancellationToken).Completion;
    }

    private SqlDocument Document(string text, IMetadataProvider metadata) => _channel?.ContextKind switch
    {
        DatabaseContextKind.Database => new SqlDocument(text, metadata) { DefaultDatabase = SelectedDatabase },
        DatabaseContextKind.Schema => new SqlDocument(text, metadata, SelectedDatabase),
        _ => new SqlDocument(text, metadata),
    };

    [ObservableProperty]
    public partial IResultTab? SelectedTab { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CancelCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; }

    /// <summary>Brings the connection list in line with the open sessions, keeping the choice where possible.</summary>
    public void RefreshSessions()
    {
        var open = _manager.Sessions.OrderBy(static s => s.Definition.Name, StringComparer.CurrentCulture).ToList();
        var selected = SelectedSession?.Session;
        Sessions.Clear();
        foreach (var session in open)
        {
            Sessions.Add(new SessionOption(session));
        }

        SelectedSession = Sessions.FirstOrDefault(s => ReferenceEquals(s.Session, selected)) ?? (Sessions.Count == 1 || selected is null ? Sessions.FirstOrDefault() : null);
    }

    partial void OnSelectedSessionChanged(SessionOption? value) => _ = AttachChannelAsync(value?.Session);

    partial void OnSelectedDatabaseChanged(string? value)
    {
        if (!_applyingDatabase && value is not null && _channel is { } channel)
        {
            // A run started right after waits for this, so it runs in the database just chosen.
            _databaseChange = ChangeDatabaseAsync(channel, value);
        }
    }

    /// <summary>Gives the window's connection back; called when the window closes.</summary>
    public async Task CloseAsync()
    {
        var channel = _channel;
        _channelSession = null;
        _channel = null;
        _channelOpening = null;
        if (channel is not null)
        {
            await _jobs.Enqueue("Close query connection", _ => channel.DisposeAsync().AsTask(), JobPriority.Low).Completion.ConfigureAwait(false);
        }
    }

    private async Task AttachChannelAsync(ConnectionSession? session)
    {
        if (ReferenceEquals(session, _channelSession))
        {
            return;
        }

        var old = _channel;
        _channelSession = session;
        _channel = null;
        _channelOpening = null;
        ShowDatabases(null, null, null);
        if (old is not null)
        {
            _ = _jobs.Enqueue("Close query connection", _ => old.DisposeAsync().AsTask(), JobPriority.Low);
        }

        if (await EnsureChannelAsync().ConfigureAwait(false) is { } channel)
        {
            await RefreshDatabaseAsync(channel, withList: true).ConfigureAwait(false);
        }
    }

    private Task<QueryChannel?> EnsureChannelAsync() =>
        _channel is { } open ? Task.FromResult<QueryChannel?>(open) : _channelOpening ??= OpenChannelAsync(_channelSession);

    private async Task<QueryChannel?> OpenChannelAsync(ConnectionSession? session)
    {
        if (session is null)
        {
            return null;
        }

        try
        {
            var channel = await _jobs.Enqueue("Open query connection", context => session.OpenChannelAsync(context.CancellationToken), JobPriority.High)
                .Completion.ConfigureAwait(false);
            if (!ReferenceEquals(session, _channelSession))
            {
                // Another connection was chosen meanwhile.
                await channel.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            _channel = channel;
            if (PendingDatabase is { } database)
            {
                PendingDatabase = null;
                await _jobs.Enqueue("Change database", context => channel.ChangeDatabaseAsync(database, context.CancellationToken), JobPriority.High)
                    .Completion.ConfigureAwait(false);
            }

            return channel;
        }
        catch (OperationCanceledException)
        {
            _channelOpening = null;
            return null;
        }
#pragma warning disable CA1031 // The server's reason is shown; the window stays usable and tries again on the next run.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _channelOpening = null;
            _channelError = ex.Message;
            _ui.Post(() => Status = ex.Message);
            return null;
        }
    }

    /// <summary>Reads the current database (and, with <paramref name="withList"/>, the list) from the server.</summary>
    private async Task RefreshDatabaseAsync(QueryChannel channel, bool withList)
    {
        if (channel.ContextKind is null)
        {
            return;
        }

        try
        {
            var (current, list) = await _jobs.Enqueue<(string? Current, IReadOnlyList<string>? List)>(
                    "Read databases",
                    async context => (
                        await channel.GetCurrentDatabaseAsync(context.CancellationToken).ConfigureAwait(false),
                        withList ? await channel.GetDatabasesAsync(context.CancellationToken).ConfigureAwait(false) : null),
                    JobPriority.High)
                .Completion.ConfigureAwait(false);
            _ui.Post(() =>
            {
                if (ReferenceEquals(channel, _channel))
                {
                    ShowDatabases(channel.ContextKind, current, list);
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // The selector is a convenience; a failure leaves it as it was.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void ShowDatabases(DatabaseContextKind? kind, string? current, IReadOnlyList<string>? list)
    {
        _applyingDatabase = true;
        try
        {
            if (kind is null)
            {
                Databases.Clear();
            }
            else if (list is not null)
            {
                Databases.Clear();
                foreach (var name in list)
                {
                    Databases.Add(name);
                }
            }

            // A database the list does not have (no access to the catalog view) is still shown.
            if (current is not null && !Databases.Contains(current))
            {
                Databases.Add(current);
            }

            SelectedDatabase = current;
            CanChangeDatabase = kind is not null;
            SwitchesSchema = kind == DatabaseContextKind.Schema;
        }
        finally
        {
            _applyingDatabase = false;
        }
    }

    private async Task ChangeDatabaseAsync(QueryChannel channel, string database)
    {
        try
        {
            await _jobs.Enqueue("Change database", context => channel.ChangeDatabaseAsync(database, context.CancellationToken), JobPriority.High)
                .Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // The server's reason is shown; the selector goes back to the real current database below.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _ui.Post(() => Status = ex.Message);
        }

        await RefreshDatabaseAsync(channel, withList: false).ConfigureAwait(false);
    }

    /// <summary>Chooses the session of a connection, if it is open.</summary>
    public void SelectSession(Guid connectionId) =>
        SelectedSession = Sessions.FirstOrDefault(s => s.Session.Definition.Id == connectionId) ?? SelectedSession;

    private bool CanRun() => !IsRunning && SelectedSession is not null && !string.IsNullOrWhiteSpace(Text);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var session = SelectedSession!.Session;
        var sql = string.IsNullOrWhiteSpace(SelectedText) ? Text : SelectedText;

        ClearResults();
        var risk = StatementGuard.Analyze(sql);
        if (session.Definition.IsReadOnly && risk.WritesData)
        {
            AddMessage(Strings.QueryBlockedReadOnly, isError: true);
            Status = Strings.QueryNotSent;
            return;
        }

        if (session.Definition.Environment == ConnectionEnvironment.Production && risk.DestructiveActions.Count > 0)
        {
            var question = Format(Strings.QueryConfirmMessage, session.Definition.Name, string.Join(", ", risk.DestructiveActions));
            if (!await _dialogs.ConfirmAsync(Strings.QueryConfirmTitle, question))
            {
                Status = Strings.QueryNotSent;
                return;
            }
        }

        IsRunning = true;
        Status = Format(Strings.QueryRunning, 0);
        var channel = await EnsureChannelAsync();
        await _databaseChange;
        if (channel is null)
        {
            // The window's connection could not be opened: the server's reason, and nothing was sent.
            IsRunning = false;
            AddMessage(_channelError ?? Strings.QueryNotSent, isError: true);
            Status = Strings.QueryNotSent;
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        long totalRows = 0;
        QueryHistoryEntry Recorded(QueryOutcome outcome, long? rows, string? error) => new(
            0, startedAt, session.Definition.Id, session.Definition.Name, session.Definition.ProviderId, sql, watch.Elapsed, outcome, rows, error);
        try
        {
            var request = new QueryRequest(sql) { MaxRows = MaxRows };
            _run = _jobs.Enqueue(
                "Run query",
                async context =>
                {
                    QueryResultSet? current = null;
                    IReadOnlyList<ResultColumn>? pending = null;
                    var resultNumber = 0;

                    // The tab appears with the first rows (or at the end of an empty result), so that its
                    // column widths can fit the data.
                    QueryResultSet Open(IReadOnlyList<object?[]>? sample)
                    {
                        var created = new QueryResultSet(Format(Strings.ResultTitle, ++resultNumber), pending!, sample, _jobs, _ui);
                        pending = null;
                        _ui.Post(() =>
                        {
                            Tabs.Insert(Tabs.Count - 1, created);
                            if (ReferenceEquals(SelectedTab, Messages))
                            {
                                SelectedTab = created;
                            }
                        });
                        return created;
                    }

                    await foreach (var item in channel.ExecuteAsync(request, context.CancellationToken).ConfigureAwait(false))
                    {
                        switch (item)
                        {
                            case ResultSetStarted started:
                                pending = started.Columns;
                                current = null;
                                break;
                            case RowBatch batch when current is not null || pending is not null:
                                var target = current ??= Open(batch.Rows);
                                var count = Interlocked.Add(ref totalRows, batch.Rows.Count);
                                _ui.Post(() =>
                                {
                                    target.AddRows(batch.Rows);
                                    Status = Format(Strings.QueryRunning, count);
                                });
                                break;
                            case ResultSetCompleted completed:
                                if (pending is not null)
                                {
                                    current = Open(null);
                                }

                                if (completed.Truncated)
                                {
                                    _ui.Post(() => AddMessage(Format(Strings.QueryTruncated, MaxRows), isError: false));
                                }

                                break;
                            case StatementCompleted { RowsAffected: { } affected }:
                                _ui.Post(() => AddMessage(Format(Strings.QueryRowsAffected, affected), isError: false));
                                break;
                            case QueryMessage message:
                                _ui.Post(() => AddMessage(message.Text, message.Severity == QueryMessageSeverity.Error));
                                break;
                            default:
                                break;
                        }
                    }
                });
            await _run.Completion;

            var rows = Interlocked.Read(ref totalRows);
            var hasResults = Tabs.Count > 1;
            if (!hasResults && Messages.Lines.Count == 0)
            {
                AddMessage(Strings.QueryCompleted, isError: false);
            }

            Status = hasResults ? Format(Strings.QueryDone, rows, Elapsed(watch)) : Format(Strings.QueryDoneNoRows, Elapsed(watch));
            await RecordAsync(Recorded(QueryOutcome.Succeeded, hasResults ? rows : null, null));
        }
        catch (OperationCanceledException)
        {
            Status = Format(Strings.QueryCanceled, Elapsed(watch));
            AddMessage(Status, isError: false);
            await RecordAsync(Recorded(QueryOutcome.Canceled, null, null));
        }
        catch (ReadOnlyConnectionException)
        {
            AddMessage(Strings.QueryBlockedReadOnly, isError: true);
            Status = Strings.QueryNotSent;
        }
#pragma warning disable CA1031 // A database error is a result to show, with the server's own message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AddMessage(ex.Message, isError: true);
            SelectedTab = Messages;
            Status = Format(Strings.QueryFailed, Elapsed(watch));
            await RecordAsync(Recorded(QueryOutcome.Failed, null, ex.Message));
        }
        finally
        {
            _run = null;
            IsRunning = false;

            // The script may have switched database (USE): the selector follows.
            _ = RefreshDatabaseAsync(channel, withList: false);
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _run?.Cancel();

    /// <summary>Adds the run to the history. A history that cannot be written must never disturb the query itself.</summary>
    private async Task RecordAsync(QueryHistoryEntry entry)
    {
        try
        {
            await _jobs.Enqueue("Save history", context => _history.AddAsync(entry, context.CancellationToken), JobPriority.Low).Completion;
        }
#pragma warning disable CA1031 // See the summary: the history is a convenience.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void ClearResults()
    {
        while (Tabs.Count > 1)
        {
            Tabs.RemoveAt(0);
        }

        Messages.Lines.Clear();
        SelectedTab = Messages;
    }

    private void AddMessage(string text, bool isError) => Messages.Lines.Add(new QueryMessageLine(text, isError));

    private static string Elapsed(Stopwatch watch) => watch.Elapsed.TotalSeconds >= 1
        ? watch.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.CurrentCulture) + " s"
        : watch.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.CurrentCulture) + " ms";

    private static string Format(string format, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(format), arguments);
}

/// <summary>View model of the document area: the open query tabs.</summary>
public partial class DocumentsViewModel : ObservableObject
{
    private readonly IConnectionManager _manager;
    private readonly IMetadataService _metadata;
    private readonly IQueryHistory _history;
    private readonly IJobQueue _jobs;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private int _counter;

    public DocumentsViewModel(
        IConnectionManager manager, IMetadataService metadata, IQueryHistory history, IJobQueue jobs, IDialogService dialogs, IUiDispatcher ui)
    {
        _manager = manager;
        _metadata = metadata;
        _history = history;
        _jobs = jobs;
        _dialogs = dialogs;
        _ui = ui;

        // Raised on whatever thread opened or closed the session.
        manager.SessionsChanged += (_, _) => ui.Post(RefreshSessions);
        NewQuery();
    }

    public ObservableCollection<QueryDocumentViewModel> Documents { get; } = [];

    [ObservableProperty]
    public partial QueryDocumentViewModel? Selected { get; set; }

    /// <summary>The connection a new query should start on, set by the explorer's selection.</summary>
    public Guid? PreferredConnectionId { get; set; }

    [RelayCommand]
    private void NewQuery()
    {
        var title = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.QueryTitle), ++_counter);
        Open(title, string.Empty, PreferredConnectionId);
    }

    /// <summary>Opens an empty query tab on a connection and, if given, a database of it (the explorer's New query).</summary>
    public QueryDocumentViewModel OpenEmpty(Guid? connectionId, string? database)
    {
        var title = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.QueryTitle), ++_counter);
        return Open(title, string.Empty, connectionId, database);
    }

    /// <summary>Opens a tab with the given text, on the given connection if it is open, and on a database of it if given.</summary>
    public QueryDocumentViewModel Open(string title, string text, Guid? connectionId, string? database = null)
    {
        var document = new QueryDocumentViewModel(title, _manager, _metadata, _history, _jobs, _dialogs, _ui, database) { Text = text };
        if (connectionId is { } id)
        {
            document.SelectSession(id);
        }

        // "Go to definition" in any tab opens the source in a new tab next to it.
        document.DefinitionOpened += (_, definition) => Open(definition.Title, definition.Text, definition.ConnectionId);
        Documents.Add(document);
        Selected = document;
        return document;
    }

    [RelayCommand]
    private void Close(QueryDocumentViewModel? document)
    {
        if (document is null)
        {
            return;
        }

        document.CancelCommand.Execute(null);
        _ = document.CloseAsync();
        var index = Documents.IndexOf(document);
        Documents.Remove(document);
        if (Documents.Count == 0)
        {
            NewQuery();
        }
        else if (Selected is null || ReferenceEquals(Selected, document))
        {
            Selected = Documents[Math.Clamp(index, 0, Documents.Count - 1)];
        }
    }

    private void RefreshSessions()
    {
        foreach (var document in Documents)
        {
            document.RefreshSessions();
        }
    }
}
