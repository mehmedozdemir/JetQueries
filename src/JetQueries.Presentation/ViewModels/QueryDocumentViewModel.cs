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
public sealed partial class QueryResultSet : ObservableObject, IResultTab
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

    /// <summary>The page ended before the result did: more rows can be fetched while the cursor is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowsText))]
    public partial bool HasMore { get; set; }

    /// <summary>How many rows, how long, when — the line under the grid.</summary>
    [ObservableProperty]
    public partial string FetchStatus { get; set; } = string.Empty;

    /// <summary>The total number of rows the query returns, once counted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowsText))]
    public partial long? TotalRows { get; set; }

    /// <summary>The row count is being computed.</summary>
    [ObservableProperty]
    public partial bool IsCounting { get; set; }

    /// <summary>"200+" while more may follow, "200 / 12,345" once counted, "200" otherwise (DBeaver's row counter).</summary>
    public string RowsText => TotalRows is { } total
        ? string.Create(CultureInfo.CurrentCulture, $"{RowCount:N0} / {total:N0}")
        : string.Create(CultureInfo.CurrentCulture, $"{RowCount:N0}{(HasMore ? "+" : string.Empty)}");

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
        OnPropertyChanged(nameof(RowsText));
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
public partial class QueryDocumentViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>Most rows a result set holds, even after "fetch all"; a larger result is cut off with a message.</summary>
    public const int MaxRows = 1_000_000;

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
        FilePath = null;

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

    /// <summary>The tab's name: the file name, or "Query N".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText), nameof(ToolTip))]
    public partial string Title { get; set; }

    /// <summary>The file the query is saved in; null for a query never saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    public partial string? FilePath { get; set; }

    /// <summary>The text changed since it was opened or saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    public partial bool IsDirty { get; set; }

    /// <summary>The tab header: the name, with a star while there are unsaved changes (as in SSMS).</summary>
    public string HeaderText => IsDirty ? Title + " *" : Title;

    /// <summary>The tab's tool tip: the file's full path, or the name.</summary>
    public string ToolTip => SelectedSession is { } session ? (FilePath ?? Title) + "\n" + session.Name : FilePath ?? Title;

    /// <summary>The encoding of the file the query came from; new files are written as UTF-8.</summary>
    public System.Text.Encoding FileEncoding { get; set; } = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The connection the query was on when the workspace was saved. When that connection is opened, the tab
    /// goes back to it; until then no other connection is chosen for it (restored SQL must not run elsewhere).
    /// </summary>
    public Guid? PendingConnectionId { get; set; }

    /// <summary>The text now counts as saved.</summary>
    public void MarkClean() => IsDirty = false;

    partial void OnTextChanged(string value) => IsDirty = true;

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
    [NotifyPropertyChangedFor(nameof(LanguageService), nameof(ToolTip))]
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

        if (selected is null && PendingConnectionId is { } pending)
        {
            // A restored tab waits for its own connection.
            SelectedSession = Sessions.FirstOrDefault(s => s.Session.Definition.Id == pending);
            if (SelectedSession is not null)
            {
                PendingConnectionId = null;
            }

            return;
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

    /// <summary>Gives the window's cursor and connection back; called when the window closes.</summary>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>Gives the window's cursor and connection back; called when the window closes.</summary>
    public async Task CloseAsync()
    {
        await CloseCursorAsync();
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

        await CloseCursorAsync();
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
        await CloseCursorAsync();
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

    /// <summary>Rows fetched per page: the first page of each result set, and each "next page" (DBeaver's fetch size).</summary>
    [ObservableProperty]
    public partial int FetchSize { get; set; } = DefaultFetchSize;

    /// <summary>The fetch size of a new tab unless the settings say otherwise.</summary>
    public const int DefaultFetchSize = 200;

    /// <summary>The SQL of the last run, for Refresh and for counting its rows.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(CountRowsCommand))]
    public partial string? LastSql { get; set; }

    /// <summary>The last run stopped after a page: its cursor is open and its last result set may have more rows.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand), nameof(FetchNextPageCommand), nameof(FetchAllCommand))]
    public partial bool HasOpenCursor { get; set; }

    partial void OnFetchSizeChanged(int value)
    {
        if (value < 1)
        {
            FetchSize = 1;
        }
    }

    private QueryCursor? _cursor;

    private bool CanRun() => !IsRunning && SelectedSession is not null && !string.IsNullOrWhiteSpace(Text);

    /// <summary>Runs the selection, or the whole text. The first page of each result set is fetched; the cursor stays open for more.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunAsync() => RunSqlAsync(string.IsNullOrWhiteSpace(SelectedText) ? Text : SelectedText);

    private bool CanRefresh() => !IsRunning && LastSql is not null && SelectedSession is not null;

    /// <summary>Runs the last SQL again (DBeaver's Refresh of a result).</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => RunSqlAsync(LastSql!);

    private async Task RunSqlAsync(string sql)
    {
        var session = SelectedSession!.Session;
        await CloseCursorAsync();
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

        LastSql = sql;
        var fetchSize = Math.Max(1, FetchSize);

        // Batches of one page (within reason), so that a page ends exactly at the fetch size.
        var request = new QueryRequest(sql) { BatchSize = Math.Min(fetchSize, 5000) };
        _cursor = new QueryCursor(channel, request, session, fetchSize);
        await ReadPageAsync(_cursor);
    }

    private bool CanFetchMore(QueryResultSet? set) => !IsRunning && HasOpenCursor && set is { HasMore: true };

    /// <summary>Fetches the next page of the result set whose cursor is open.</summary>
    [RelayCommand(CanExecute = nameof(CanFetchMore))]
    private Task FetchNextPageAsync(QueryResultSet? set)
    {
        if (_cursor is not { } cursor || set is null)
        {
            return Task.CompletedTask;
        }

        cursor.PageEnd = (int)Math.Min(MaxRows, (long)set.RowCount + Math.Max(1, FetchSize));
        return ReadPageAsync(cursor);
    }

    /// <summary>Fetches every remaining row (up to <see cref="MaxRows"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanFetchMore))]
    private Task FetchAllAsync(QueryResultSet? set)
    {
        if (_cursor is not { } cursor || set is null)
        {
            return Task.CompletedTask;
        }

        cursor.PageEnd = MaxRows;
        return ReadPageAsync(cursor);
    }

    private bool CanCount(QueryResultSet? set) => set is { IsCounting: false } && LastSql is not null && SelectedSession is not null;

    /// <summary>
    /// Counts the rows of the last query without fetching them (DBeaver's "calculate total row count"), on the
    /// window's connection when it is free, otherwise on a second connection in the same database.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCount))]
    private async Task CountRowsAsync(QueryResultSet? set)
    {
        if (set is null || LastSql is not { } sql || SelectedSession?.Session is not { } session)
        {
            return;
        }

        if (session.Provider is not IRowCountQueryBuilder builder || builder.BuildCountQuery(sql) is not { } countSql)
        {
            Status = Strings.CountUnavailable;
            return;
        }

        set.IsCounting = true;
        CountRowsCommand.NotifyCanExecuteChanged();
        try
        {
            var database = SelectedDatabase;
            var own = _cursor is null && !IsRunning ? _channel : null;
            set.TotalRows = await _jobs.Enqueue<long?>(
                "Count rows",
                async context =>
                {
                    var token = context.CancellationToken;
                    var channel = own ?? await session.OpenChannelAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (own is null && database is not null && channel.ContextKind is not null)
                        {
                            await channel.ChangeDatabaseAsync(database, token).ConfigureAwait(false);
                        }

                        await foreach (var item in channel.ExecuteAsync(new QueryRequest(countSql) { MaxRows = 1 }, token).ConfigureAwait(false))
                        {
                            if (item is RowBatch { Rows: [var row, ..] } && row[0] is { } value)
                            {
                                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
                            }
                        }

                        return null;
                    }
                    finally
                    {
                        if (own is null)
                        {
                            await channel.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                }).Completion;
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // The server's reason is shown; the rows already fetched stay.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Status = Format(Strings.CountFailed, ex.Message);
        }
        finally
        {
            set.IsCounting = false;
            CountRowsCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanCancel() => IsRunning || HasOpenCursor;

    /// <summary>Stops the running statement, or closes the open cursor of a paged result.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        if (_run is { } run)
        {
            run.Cancel();
        }
        else
        {
            await CloseCursorAsync();
        }
    }

    /// <summary>Reads events until the current result set has its page, or the run ends.</summary>
    private async Task ReadPageAsync(QueryCursor cursor)
    {
        IsRunning = true;
        HasOpenCursor = false;
        cursor.PageStarted = Stopwatch.GetTimestamp();
        try
        {
            var job = _jobs.Enqueue("Run query", context => cursor.ReadAsync(OnEvent, context.CancellationToken));
            _run = job;
            var finished = await job.Completion;
            if (!finished)
            {
                // A page is complete and more rows may follow: the cursor stays open (as DBeaver does).
                HasOpenCursor = true;
                Status = Format(Strings.QueryPaused, cursor.CurrentRows);
                await RecordOnceAsync(cursor, QueryOutcome.Succeeded, null);
                return;
            }

            var hasResults = Tabs.Count > 1;
            if (!hasResults && Messages.Lines.Count == 0)
            {
                AddMessage(Strings.QueryCompleted, isError: false);
            }

            Status = hasResults ? Format(Strings.QueryDone, cursor.TotalRows, Elapsed(cursor.Watch)) : Format(Strings.QueryDoneNoRows, Elapsed(cursor.Watch));
            await RecordOnceAsync(cursor, QueryOutcome.Succeeded, null);
            await DisposeCursorAsync(cursor);
        }
        catch (OperationCanceledException)
        {
            Status = Format(Strings.QueryCanceled, Elapsed(cursor.Watch));
            AddMessage(Status, isError: false);
            await RecordOnceAsync(cursor, QueryOutcome.Canceled, null);
            await DisposeCursorAsync(cursor);
        }
        catch (ReadOnlyConnectionException)
        {
            AddMessage(Strings.QueryBlockedReadOnly, isError: true);
            Status = Strings.QueryNotSent;
            await DisposeCursorAsync(cursor);
        }
#pragma warning disable CA1031 // A database error is a result to show, with the server's own message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AddMessage(ex.Message, isError: true);
            SelectedTab = Messages;
            Status = Format(Strings.QueryFailed, Elapsed(cursor.Watch));
            await RecordOnceAsync(cursor, QueryOutcome.Failed, ex.Message);
            await DisposeCursorAsync(cursor);
        }
        finally
        {
            _run = null;
            IsRunning = false;
            FetchNextPageCommand.NotifyCanExecuteChanged();
            FetchAllCommand.NotifyCanExecuteChanged();
        }

        // The script may have switched database (USE): the selector follows (the channel is free again).
        if (!HasOpenCursor)
        {
            _ = RefreshDatabaseAsync(cursor.Channel, withList: false);
        }
    }

    /// <summary>Handles one event on the job's thread; false stops reading (a page is complete).</summary>
    private bool OnEvent(QueryCursor cursor, QueryEvent item)
    {
        switch (item)
        {
            case ResultSetStarted started:
                cursor.PendingColumns = started.Columns;
                cursor.Current = null;
                cursor.CurrentRows = 0;
                cursor.PageEnd = cursor.FetchSize;
                cursor.ExecuteTime ??= Stopwatch.GetElapsedTime(cursor.PageStarted);
                break;
            case RowBatch batch when cursor.Current is not null || cursor.PendingColumns is not null:
                var target = cursor.Current ??= OpenResult(cursor, batch.Rows);
                cursor.CurrentRows += batch.Rows.Count;
                cursor.TotalRows += batch.Rows.Count;
                var count = cursor.TotalRows;
                _ui.Post(() =>
                {
                    target.AddRows(batch.Rows);
                    Status = Format(Strings.QueryRunning, count);
                });
                if (cursor.CurrentRows >= cursor.PageEnd)
                {
                    FinishPage(cursor, target, hasMore: cursor.CurrentRows < MaxRows);
                    if (cursor.CurrentRows >= MaxRows)
                    {
                        _ui.Post(() => AddMessage(Format(Strings.QueryTruncated, MaxRows), isError: false));
                    }

                    return false;
                }

                break;
            case ResultSetCompleted completed:
                if (cursor.PendingColumns is not null)
                {
                    cursor.Current = OpenResult(cursor, null);
                }

                if (cursor.Current is { } done)
                {
                    FinishPage(cursor, done, hasMore: false);
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

        return true;
    }

    // The tab appears with the first rows (or at the end of an empty result), so that its column widths can fit the data.
    private QueryResultSet OpenResult(QueryCursor cursor, IReadOnlyList<object?[]>? sample)
    {
        var created = new QueryResultSet(Format(Strings.ResultTitle, ++cursor.ResultNumber), cursor.PendingColumns!, sample, _jobs, _ui);
        cursor.PendingColumns = null;
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

    /// <summary>The fetch line under the grid: how many rows, how long, when (as DBeaver shows it).</summary>
    private void FinishPage(QueryCursor cursor, QueryResultSet set, bool hasMore)
    {
        var total = Stopwatch.GetElapsedTime(cursor.PageStarted);
        var fetch = total - (cursor.ResultNumber == 1 && cursor.FirstPage ? cursor.ExecuteTime ?? TimeSpan.Zero : TimeSpan.Zero);
        cursor.FirstPage = false;
        var rows = cursor.CurrentRows;
        var at = DateTime.Now;
        // Formatted on the UI thread, in the user's language and culture.
        _ui.Post(() =>
        {
            set.HasMore = hasMore;
            set.FetchStatus = Format(Strings.ResultFetched, rows, Seconds(total), Seconds(fetch), at.ToString("T", CultureInfo.CurrentCulture));
            FetchNextPageCommand.NotifyCanExecuteChanged();
            FetchAllCommand.NotifyCanExecuteChanged();
        });
    }

    /// <summary>Closes the open cursor of a paged result, if there is one; its rows stay.</summary>
    private async Task CloseCursorAsync()
    {
        if (_cursor is not { } cursor || IsRunning)
        {
            return;
        }

        foreach (var set in Tabs.OfType<QueryResultSet>())
        {
            set.HasMore = false;
        }

        await DisposeCursorAsync(cursor);
        _ = RefreshDatabaseAsync(cursor.Channel, withList: false);
    }

    private async Task DisposeCursorAsync(QueryCursor cursor)
    {
        if (ReferenceEquals(_cursor, cursor))
        {
            _cursor = null;
        }

        HasOpenCursor = false;

        // Canceled first: closing a reader with rows left would otherwise read them all (SqlClient does).
        await _jobs.Enqueue("Close cursor", _ => cursor.DisposeAsync().AsTask(), JobPriority.High).Completion;
    }

    private async Task RecordOnceAsync(QueryCursor cursor, QueryOutcome outcome, string? error)
    {
        if (cursor.Recorded)
        {
            return;
        }

        cursor.Recorded = true;
        var definition = cursor.Session.Definition;
        var rows = Tabs.Count > 1 ? cursor.TotalRows : (long?)null;
        await RecordAsync(new QueryHistoryEntry(
            0, cursor.StartedAt, definition.Id, definition.Name, definition.ProviderId, cursor.Request.Sql, cursor.Watch.Elapsed, outcome,
            outcome == QueryOutcome.Succeeded ? rows : null, error));
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.000", CultureInfo.CurrentCulture);

    /// <summary>
    /// A run's events, read a page at a time. Between pages nothing reads it: the statement's cursor stays open
    /// on the window's own connection, as DBeaver keeps it, until the next page, a new run, or Cancel.
    /// </summary>
    private sealed class QueryCursor : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancel = new();
        private readonly IAsyncEnumerator<QueryEvent> _events;
        private bool _finished;

        public QueryCursor(QueryChannel channel, QueryRequest request, ConnectionSession session, int fetchSize)
        {
            Channel = channel;
            Request = request;
            Session = session;
            FetchSize = fetchSize;
            PageEnd = fetchSize;
            _events = channel.ExecuteAsync(request, _cancel.Token).GetAsyncEnumerator(_cancel.Token);
        }

        public QueryChannel Channel { get; }

        public QueryRequest Request { get; }

        public ConnectionSession Session { get; }

        public int FetchSize { get; }

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public Stopwatch Watch { get; } = Stopwatch.StartNew();

        public long PageStarted { get; set; }

        public TimeSpan? ExecuteTime { get; set; }

        public bool FirstPage { get; set; } = true;

        public IReadOnlyList<ResultColumn>? PendingColumns { get; set; }

        public QueryResultSet? Current { get; set; }

        public int ResultNumber { get; set; }

        public int CurrentRows { get; set; }

        public long TotalRows { get; set; }

        public int PageEnd { get; set; }

        public bool Recorded { get; set; }

        /// <summary>Reads until <paramref name="onEvent"/> says stop (false: paused) or the events end (true).</summary>
        public async Task<bool> ReadAsync(Func<QueryCursor, QueryEvent, bool> onEvent, CancellationToken cancellationToken)
        {
            await using var link = cancellationToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), _cancel).ConfigureAwait(false);
            while (await _events.MoveNextAsync().ConfigureAwait(false))
            {
                if (!onEvent(this, _events.Current))
                {
                    return false;
                }
            }

            _finished = true;
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_finished)
            {
                await _cancel.CancelAsync().ConfigureAwait(false);
            }

            try
            {
                await _events.DisposeAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Closing a canceled statement may report the cancel; the cursor is gone either way.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            _cancel.Dispose();
        }
    }

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
