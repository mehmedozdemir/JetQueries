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
    private readonly IJobQueue? _jobs;

    /// <param name="title">Caption of the tab.</param>
    /// <param name="columns">The columns of the result set.</param>
    /// <param name="sample">The first rows, used only to choose column widths; they are not added.</param>
    /// <param name="jobs">Where sorting and filtering run; without one they run on the calling thread.</param>
    /// <param name="ui">Where their results are applied.</param>
    public QueryResultSet(
        string title, IReadOnlyList<ResultColumn> columns, IReadOnlyList<object?[]>? sample = null, IJobQueue? jobs = null, IUiDispatcher? ui = null)
    {
        _jobs = jobs;
        View = new ResultSetView(this, jobs, ui);
        View.Changed += (_, _) => RefreshRecord();
        RowCountChanged += (_, _) => RefreshRecord();
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
        _scales = [.. columns.Select(static c => DecimalScale.Of(c.DataType))];
        _rescale = _scales.Any(static s => s is not null);
    }

    // The declared scale of decimal(p,s) / numeric / NUMBER columns: their values are shown with that many
    // decimals (12.50, not 12.5), whatever the driver returned.
    private readonly int?[] _scales;
    private readonly bool _rescale;

    private void Rescale(IReadOnlyList<object?[]> rows)
    {
        if (!_rescale)
        {
            return;
        }

        foreach (var row in rows)
        {
            for (var c = 0; c < _scales.Length && c < row.Length; c++)
            {
                if (_scales[c] is { } scale && row[c] is decimal value)
                {
                    row[c] = DecimalScale.Apply(value, scale);
                }
            }
        }
    }

    public event EventHandler? RowCountChanged;

    /// <summary>The page ended before the result did: more rows can be fetched while the cursor is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowsText))]
    public partial bool HasMore { get; set; }

    /// <summary>
    /// The single SELECT this result comes from, run again for more pages, a count or a filter; null for a result of
    /// a script or a procedure call (read to its end at once).
    /// </summary>
    internal string? PagedSql { get; set; }

    /// <summary>The parameter values the result was read with; its SQL runs again with them (pages, count, export).</summary>
    internal IReadOnlyList<QueryParameter> Parameters { get; set; } = [];

    /// <summary>The result comes from a single SELECT, so a SQL condition can narrow it on the server.</summary>
    [ObservableProperty]
    public partial bool CanFilter { get; set; }

    /// <summary>The SQL condition being typed in the filter box over the grid.</summary>
    [ObservableProperty]
    public partial string SqlFilter { get; set; } = string.Empty;

    /// <summary>The SQL condition the rows shown were fetched with; null when unfiltered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltered))]
    public partial string? AppliedFilter { get; set; }

    /// <summary>The rows shown were narrowed by a SQL condition.</summary>
    public bool IsFiltered => AppliedFilter is not null;

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

    /// <summary>Grid, text or record view.</summary>
    [ObservableProperty]
    public partial ResultViewMode ViewMode { get; set; }

    /// <summary>The SQL filter bar is open (it shows anyway while a filter is applied).</summary>
    [ObservableProperty]
    public partial bool IsSqlFilterOpen { get; set; }

    /// <summary>The shown row (in the view's order) the record view shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordFields), nameof(RecordPosition))]
    [NotifyCanExecuteChangedFor(nameof(FirstRecordCommand), nameof(PreviousRecordCommand), nameof(NextRecordCommand), nameof(LastRecordCommand))]
    public partial int RecordIndex { get; set; }

    /// <summary>The columns the record view lists, in order; null for all (the grid's hidden columns are left out).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordFields))]
    public partial IReadOnlyList<int>? RecordColumns { get; set; }

    /// <summary>The columns of the record view's row.</summary>
    public IReadOnlyList<ResultRecordField> RecordFields
    {
        get
        {
            if (View.RowCount == 0)
            {
                return [];
            }

            var row = View.SourceRow(Math.Clamp(RecordIndex, 0, View.RowCount - 1));
            return [.. (RecordColumns ?? [.. Enumerable.Range(0, Columns.Count)]).Select(c =>
            {
                var text = GetText(row, c);
                return new ResultRecordField(Columns[c].Header, text ?? string.Empty, Columns[c].DataType, text is null);
            })];
        }
    }

    /// <summary>"Record 5 / 200" (one-based).</summary>
    public string RecordPosition => View.RowCount == 0
        ? string.Empty
        : string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.RecordPosition), Math.Clamp(RecordIndex, 0, View.RowCount - 1) + 1, View.RowCount);

    partial void OnRecordIndexChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, Math.Max(0, View.RowCount - 1));
        if (clamped != value)
        {
            RecordIndex = clamped;
        }
    }

    private bool CanGoBack() => RecordIndex > 0;

    private bool CanGoOn() => RecordIndex < View.RowCount - 1;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void FirstRecord() => RecordIndex = 0;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousRecord() => RecordIndex--;

    [RelayCommand(CanExecute = nameof(CanGoOn))]
    private void NextRecord() => RecordIndex++;

    [RelayCommand(CanExecute = nameof(CanGoOn))]
    private void LastRecord() => RecordIndex = View.RowCount - 1;

    /// <summary>Rows <paramref name="first"/>…<paramref name="last"/> as shown (sorted and filtered), read on the UI thread.</summary>
    public IReadOnlyList<object?[]> ShownRows(int first, int last)
    {
        last = Math.Min(last, View.RowCount - 1);
        var rows = new List<object?[]>(Math.Max(0, last - first + 1));
        for (var r = Math.Max(0, first); r <= last; r++)
        {
            rows.Add(_rows[View.SourceRow(r)]);
        }

        return rows;
    }

    /// <summary>The value at a shown row, for the value panel.</summary>
    public object? ShownValue(int row, int column) =>
        row >= 0 && row < View.RowCount && column >= 0 && column < Columns.Count ? _rows[View.SourceRow(row)][column] : null;

    /// <summary>Counts and sums the selected cells as a background job.</summary>
    public Task<SelectionStats> SelectionStatsAsync(int first, int last, IReadOnlyList<int> columns)
    {
        var rows = ShownRows(first, last);
        return _jobs is null
            ? Task.FromResult(SelectionStats.Compute(rows, columns, CancellationToken.None))
            : _jobs.Enqueue("Selection totals", context => Task.FromResult(SelectionStats.Compute(rows, columns, context.CancellationToken)), JobPriority.Low).Completion;
    }

    /// <summary>The rows as the text view shows them (in the view's order), built as a background job.</summary>
    /// <param name="visible">The columns to write; null for all.</param>
    public Task<string> BuildTextAsync(IReadOnlyList<int>? visible)
    {
        // Read on the UI thread (rows arrive there); written off it.
        var rows = SnapshotRows();
        var map = View.RowMap;
        IReadOnlyList<object?[]> ordered = map is null ? rows : [.. map.Select(i => rows[i])];
        var columns = Columns;
        return _jobs is null
            ? Task.FromResult(ResultTextFormatter.Format(columns, ordered, visible, CancellationToken.None))
            : _jobs.Enqueue("Format results as text", context => Task.FromResult(ResultTextFormatter.Format(columns, ordered, visible, context.CancellationToken)), JobPriority.High).Completion;
    }

    /// <summary>The record view follows new rows, sorting and filtering.</summary>
    private void RefreshRecord()
    {
        OnPropertyChanged(nameof(RecordFields));
        OnPropertyChanged(nameof(RecordPosition));
        FirstRecordCommand.NotifyCanExecuteChanged();
        PreviousRecordCommand.NotifyCanExecuteChanged();
        NextRecordCommand.NotifyCanExecuteChanged();
        LastRecordCommand.NotifyCanExecuteChanged();
    }

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

    /// <summary>The grid's data editor: set when the rows come from one table with a key; null when read-only.</summary>
    [ObservableProperty]
    public partial ResultEditor? Editor { get; set; }

    /// <summary>Why the rows cannot be edited, when they cannot (the read-only mark's tip).</summary>
    [ObservableProperty]
    public partial string? ReadOnlyReason { get; set; }

    /// <summary>The values of a row (the array itself, for the editor to copy).</summary>
    internal object?[] Row(int row) => _rows[row];

    /// <summary>Sets a cell (the data editor).</summary>
    internal void SetValue(int row, int column, object? value) => _rows[row][column] = value;

    /// <summary>Drops the rows from <paramref name="first"/> on (rows the data editor added).</summary>
    internal void RemoveRowsFrom(int first)
    {
        if (first < 0 || first >= _rows.Count)
        {
            return;
        }

        _rows.RemoveRange(first, _rows.Count - first);
        RowCountChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(RowsText));
    }

    /// <summary>The rows received so far, as a list that later rows do not change.</summary>
    internal object?[][] SnapshotRows() => [.. _rows];

    /// <summary>A value as the grid shows it.</summary>
    internal static string? FormatValue(object? value) => Format(value);

    /// <summary>Puts the rows of a new execution of the same query in place of those shown.</summary>
    public void ReplaceRows(IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Editor?.Clear();
        _rows.Clear();
        Rescale(rows);
        _rows.AddRange(rows);
        RowCountChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(RowsText));
    }

    public void AddRows(IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Rescale(rows);
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

    /// <summary>The query of the library this tab was opened from or saved to; saving to the library updates it.</summary>
    public Guid? SavedQueryId { get; set; }

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

    /// <summary>The tab's tool tip: the file's full path (or the name), then the connection and its database.</summary>
    public string ToolTip => SelectedSession is { } session
        ? (FilePath ?? Title) + "\n" + session.Name + (SelectedDatabase is { Length: > 0 } database ? " · " + database : string.Empty)
        : FilePath ?? Title;

    /// <summary>
    /// Whether the tab may be moved to another open connection: not while it runs, nor while a transaction is open
    /// on the one it is on (its changes would be left behind).
    /// </summary>
    public bool CanChangeSession => !IsRunning && !HasOpenTransaction;

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

    /// <summary>"Sat 12, Süt 5" and the selection's length, for the status bar; empty until the editor reports.</summary>
    [ObservableProperty]
    public partial string CaretText { get; set; } = string.Empty;

    /// <summary>Where the editor's caret is (line and column from 1) and how many characters are selected.</summary>
    public void SetCaret(int line, int column, int selectionLength)
    {
        var text = Format(Strings.CaretPosition, line, column);
        CaretText = selectionLength > 0 ? text + " · " + Format(Strings.CaretSelection, selectionLength) : text;
    }

    /// <summary>The text selected in the editor; when not empty, only this part is run.</summary>
    public string SelectedText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(RunInNewTabCommand), nameof(RunStatementCommand))]
    public partial string Text { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(RunInNewTabCommand), nameof(RunStatementCommand))]
    [NotifyPropertyChangedFor(nameof(LanguageService), nameof(ToolTip), nameof(Environment), nameof(EnvironmentName), nameof(IsMarkedEnvironment), nameof(EnvironmentFrameTip))]
    public partial SessionOption? SelectedSession { get; set; }

    /// <summary>The environment of the tab's connection (drawn on the tab and above the editor); null with no connection.</summary>
    public ConnectionEnvironment? Environment => SelectedSession?.Session.Definition.Environment;

    /// <summary>Test and production connections are marked with a coloured strip and their name.</summary>
    public bool IsMarkedEnvironment => Environment is ConnectionEnvironment.Test or ConnectionEnvironment.Production;

    /// <summary>The frame's tooltip: "PRODUCTION · connection name"; null without an environment.</summary>
    public string? EnvironmentFrameTip => EnvironmentName is { } name ? name + " · " + SelectedSession!.Name : null;

    /// <summary>The environment's name ("PRODUCTION"); null when not marked.</summary>
    public string? EnvironmentName => Environment switch
    {
        ConnectionEnvironment.Production => Strings.EnvironmentProduction.ToUpper(CultureInfo.CurrentCulture),
        ConnectionEnvironment.Test => Strings.EnvironmentTest.ToUpper(CultureInfo.CurrentCulture),
        ConnectionEnvironment.Development => Strings.EnvironmentDevelopment,
        _ => null,
    };

    /// <summary>The databases (or, for Oracle, schemas) of the connection, for the window's database selector.</summary>
    public ObservableCollection<string> Databases { get; } = [];

    /// <summary>Text the database picker's list is narrowed by (any part of the name, ignoring case); empty shows all.</summary>
    [ObservableProperty]
    public partial string DatabaseFilter { get; set; } = string.Empty;

    /// <summary>The databases (schemas) whose names contain <see cref="DatabaseFilter"/>, in the server's order.</summary>
    public ObservableCollection<string> ShownDatabases { get; } = [];

    partial void OnDatabaseFilterChanged(string value) => FilterDatabases();

    private void FilterDatabases()
    {
        var filter = DatabaseFilter.Trim();
        ShownDatabases.Clear();
        foreach (var name in Databases)
        {
            if (filter.Length == 0 || CultureInfo.CurrentCulture.CompareInfo.IndexOf(name, filter, CompareOptions.IgnoreCase) >= 0)
            {
                ShownDatabases.Add(name);
            }
        }
    }

    /// <summary>
    /// The window's current database (schema for Oracle), as the server reports it; it follows a <c>USE</c> run in
    /// the window. Choosing another one switches the window's connection, as SSMS's database selector does.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
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

    /// <summary>Raised when the details view of the object under the caret should open (Ctrl+Click, F12).</summary>
    public event EventHandler<ObjectDetailsRequest>? DetailsRequested;

    /// <summary>Finds what the name at <paramref name="offset"/> refers to and opens its details view.</summary>
    public async Task ShowDetailsAsync(string text, int offset)
    {
        if (SelectedSession?.Session is not { Provider.LanguageService: { } service } session)
        {
            return;
        }

        // A chip over the editor says something is happening ("Finding the object…"), then what came of it.
        var version = ++_navigationVersion;
        NavigationText = Strings.NavigationFinding;
        IsNavigating = true;
        string? outcome = null;
        try
        {
            var metadata = SessionMetadata(session);
            var document = Document(text, metadata);
            var target = await _jobs.Enqueue(
                "Find object",
                async context => await TargetOfAsync(await service.GetDefinitionAsync(document, offset, context.CancellationToken).ConfigureAwait(false), metadata, context.CancellationToken)
                    .ConfigureAwait(false),
                JobPriority.High).Completion;
            if (target is not null)
            {
                DetailsRequested?.Invoke(this, new ObjectDetailsRequest(session.Definition.Id, target));
            }
            else
            {
                outcome = Strings.DefinitionNotFound;
                Status = outcome;
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Navigation is a convenience; the server's reason is shown, nothing else is disturbed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            outcome = ex.Message;
            Status = outcome;
        }
        finally
        {
            if (version == _navigationVersion)
            {
                IsNavigating = false;
                NavigationText = outcome;
            }
        }

        // A "not found" stays a moment, then goes.
        if (outcome is not null)
        {
            await Task.Delay(NavigationMessageTime);
            if (version == _navigationVersion)
            {
                NavigationText = null;
            }
        }
    }

    /// <summary>
    /// What the editor shows when the mouse rests on a name: a table's or view's columns, a column's type and
    /// table, or what kind of object it is. Resolved as Ctrl+Click resolves it (aliases too), from cached metadata.
    /// Null for anything else (keywords, literals) or when the window has no connection.
    /// </summary>
    public async Task<SqlHoverInfo?> HoverAsync(string text, int offset, CancellationToken cancellationToken)
    {
        if (SelectedSession?.Session is not { Provider.LanguageService: { } service } session)
        {
            return null;
        }

        var metadata = SessionMetadata(session);
        var document = Document(text, metadata);
        try
        {
            return await _jobs.Enqueue(
                "Hover",
                async context =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, cancellationToken);
                    var token = linked.Token;
                    var definition = await service.GetDefinitionAsync(document, offset, token).ConfigureAwait(false);
                    if (definition?.Target is not { } target)
                    {
                        return null;
                    }

                    var kind = ObjectDetailsViewModel.KindName(target.Kind);
                    if (target.Kind is not (DatabaseObjectKinds.Table or DatabaseObjectKinds.View))
                    {
                        return new SqlHoverInfo(target.Name.ToString(), kind, definition.MemberName);
                    }

                    var columns = await metadata.GetColumnsAsync(target.Name, token).ConfigureAwait(false);
                    if (definition.MemberName is { } member)
                    {
                        var column = columns.FirstOrDefault(c => string.Equals(c.Name, member, StringComparison.OrdinalIgnoreCase));
                        return column is null
                            ? null
                            : new SqlHoverInfo(column.Name, Strings.HoverColumn, column.DataType + " · " + target.Name) { Column = column };
                    }

                    return new SqlHoverInfo(target.Name.ToString(), kind, Format(Strings.HoverColumns, columns.Count))
                    {
                        Columns = [.. columns.Take(SqlHoverInfo.MaxColumns)],
                        MoreColumns = Math.Max(0, columns.Count - SqlHoverInfo.MaxColumns),
                    };
                },
                JobPriority.High,
                cancellationToken).Completion;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
#pragma warning disable CA1031 // A hover is a convenience: nothing is shown when the lookup fails.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>
    /// What a definition opens: its object, or — for a routine pointed at inside a package (<c>pkg.member</c>) —
    /// that routine, as the package lists it (the package itself when it does not).
    /// </summary>
    private static async Task<DatabaseObject?> TargetOfAsync(SqlDefinition? definition, SessionMetadataProvider metadata, CancellationToken cancellationToken)
    {
        if (definition?.Target is not { } target)
        {
            return null;
        }

        if (definition.MemberName is { } member && target.Kind == DatabaseObjectKinds.Package)
        {
            await foreach (var routine in metadata.GetObjectsAsync(target, DatabaseObjectKinds.Procedure | DatabaseObjectKinds.Function, cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(routine.Name.Name, member, StringComparison.OrdinalIgnoreCase))
                {
                    return routine;
                }
            }
        }

        return target;
    }

    /// <summary>How long a navigation message ("not found") stays over the editor.</summary>
    internal static TimeSpan NavigationMessageTime { get; set; } = TimeSpan.FromSeconds(3);

    private int _navigationVersion;

    /// <summary>The chip over the editor while Ctrl+Click looks an object up, or what came of it; null hides it.</summary>
    [ObservableProperty]
    public partial string? NavigationText { get; set; }

    /// <summary>Ctrl+Click is looking an object up (the chip turns, the cursor waits).</summary>
    [ObservableProperty]
    public partial bool IsNavigating { get; set; }

    /// <summary>Signature help at a caret position, computed as a background job against cached metadata.</summary>
    public Task<SignatureHelp?> SignatureHelpAsync(string text, int offset, CancellationToken cancellationToken)
    {
        if (SelectedSession?.Session is not { Provider.LanguageService: ISignatureHelpProvider service } session)
        {
            return Task.FromResult<SignatureHelp?>(null);
        }

        var document = Document(text, SessionMetadata(session));
        return _jobs.Enqueue("Signature help", context => service.GetSignatureHelpAsync(document, offset, context.CancellationToken), JobPriority.High, cancellationToken)
            .Completion;
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
            var metadata = SessionMetadata(session);
            var document = Document(text, metadata);
            var found = await _jobs.Enqueue<(DatabaseObject? Target, string? Source)>(
                "Go to definition",
                async context =>
                {
                    var definition = await service.GetDefinitionAsync(document, offset, context.CancellationToken).ConfigureAwait(false);
                    if (await TargetOfAsync(definition, metadata, context.CancellationToken).ConfigureAwait(false) is not { } target)
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

        var document = Document(text, SessionMetadata(session));
        return _jobs.Enqueue(
            "Completion",
            context => service.GetCompletionsAsync(document, offset, context.CancellationToken),
            JobPriority.High,
            cancellationToken).Completion;
    }

    // The selected database matters to providers whose metadata sees one database only (PostgreSQL).
    private SessionMetadataProvider SessionMetadata(ConnectionSession session) =>
        new(session, _metadata, _channel?.ContextKind == DatabaseContextKind.Database ? SelectedDatabase : null);

    private SqlDocument Document(string text, IMetadataProvider metadata) => _channel?.ContextKind switch
    {
        DatabaseContextKind.Database => new SqlDocument(text, metadata) { DefaultDatabase = SelectedDatabase },
        DatabaseContextKind.Schema => new SqlDocument(text, metadata, SelectedDatabase),
        _ => new SqlDocument(text, metadata),
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial IResultTab? SelectedTab { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(RunInNewTabCommand), nameof(RunStatementCommand), nameof(CancelCommand), nameof(RefreshCommand), nameof(CloseResultCommand), nameof(SaveChangesCommand))]
    [NotifyPropertyChangedFor(nameof(CanChangeSession))]
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

        _ui.Post(() =>
        {
            SupportsTransactions = false;
            HasOpenTransaction = false;
        });
        if (await EnsureChannelAsync().ConfigureAwait(false) is { } channel)
        {
            _ui.Post(() => SupportsTransactions = channel.SupportsTransactions);
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

            FilterDatabases();
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
        if (HasOpenTransaction)
        {
            // Some providers reconnect to switch: the open transaction would be lost without a word.
            _ui.Post(() => Status = Strings.TransactionBlocksDatabase);
            await RefreshDatabaseAsync(channel, withList: false).ConfigureAwait(false);
            return;
        }

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

    // How many results the tab has numbered since it was last cleared ("Result 3" follows "Result 2" when a run
    // keeps the earlier results). Results of a single SELECT keep their SQL (QueryResultSet.PagedSql): more rows
    // are fetched by running it again; nothing stays open on the server between pages.
    private int _resultCount;

    partial void OnFetchSizeChanged(int value)
    {
        if (value < 1)
        {
            FetchSize = 1;
        }
    }

    private QueryCursor? _cursor;

    private bool CanRun() => !IsRunning && SelectedSession is not null && !string.IsNullOrWhiteSpace(Text);

    /// <summary>
    /// Runs the selection, or the whole text. A single SELECT stops after its first page and is closed at once; any
    /// other text (a script, a procedure call, DML) is read to its end, as SSMS does, because running it again
    /// for more rows would repeat what it does.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunAsync() => RunSqlAsync(TextToRun());

    /// <summary>
    /// Runs the selection, or the whole text, keeping the results shown: its results open in new tabs after them
    /// ("Result 2", "Result 3"…), as DBeaver's "Execute in new tab".
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunInNewTabAsync() => RunSqlAsync(TextToRun(), keepResults: true);

    /// <summary>
    /// Each statement commits on its own (true, every driver's default) or, in manual mode, text that can change
    /// data starts a transaction that stays open until Commit or Rollback. A single SELECT never starts one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransactionModeText), nameof(ShowTransactionButtons))]
    public partial bool AutoCommit { get; set; } = true;

    /// <summary>The connection lets the tab keep a transaction open (all built-in providers do).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTransactionButtons))]
    public partial bool SupportsTransactions { get; set; }

    /// <summary>A transaction with changes is open on the tab's connection (also one the user began in SQL).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(RollbackCommand))]
    [NotifyPropertyChangedFor(nameof(ShowTransactionButtons), nameof(CanChangeSession))]
    public partial bool HasOpenTransaction { get; set; }

    /// <summary>"Auto-commit" or "Manual commit", for the toggle.</summary>
    public string TransactionModeText => AutoCommit ? Strings.TransactionAuto : Strings.TransactionManual;

    /// <summary>Commit and Rollback are shown in manual mode, or while a transaction is open.</summary>
    public bool ShowTransactionButtons => SupportsTransactions && (!AutoCommit || HasOpenTransaction);

    private bool CanEndTransaction() => !IsRunning && HasOpenTransaction && _channel is not null;

    /// <summary>Commits the tab's open transaction.</summary>
    [RelayCommand(CanExecute = nameof(CanEndTransaction))]
    private Task<bool> CommitAsync() => EndTransactionAsync(commit: true);

    /// <summary>Rolls the tab's open transaction back.</summary>
    [RelayCommand(CanExecute = nameof(CanEndTransaction))]
    private Task<bool> RollbackAsync() => EndTransactionAsync(commit: false);

    /// <summary>
    /// Switches between auto-commit and manual commit. Going back to auto-commit with a transaction open asks
    /// first whether to commit it or roll it back (Cancel stays in manual mode).
    /// </summary>
    [RelayCommand]
    private async Task ToggleAutoCommitAsync()
    {
        if (!AutoCommit && HasOpenTransaction)
        {
            switch (await _dialogs.AskOpenTransactionAsync(Title))
            {
                case TransactionChoice.Commit when !await EndTransactionAsync(commit: true):
                case TransactionChoice.Rollback when !await EndTransactionAsync(commit: false):
                case TransactionChoice.Cancel:
                    OnPropertyChanged(nameof(AutoCommit));
                    return;
                default:
                    break;
            }
        }

        AutoCommit = !AutoCommit;
    }

    /// <summary>Commits or rolls back; false (with the server's reason shown) when that failed.</summary>
    public async Task<bool> EndTransactionAsync(bool commit)
    {
        if (_channel is not { } channel)
        {
            return false;
        }

        IsRunning = true;
        try
        {
            await _jobs.Enqueue(
                commit ? "Commit" : "Rollback",
                context => commit ? channel.CommitAsync(context.CancellationToken) : channel.RollbackAsync(context.CancellationToken),
                JobPriority.High).Completion;
            AddMessage(commit ? Strings.TransactionCommitted : Strings.TransactionRolledBack, isError: false);
            Status = commit ? Strings.TransactionCommitted : Strings.TransactionRolledBack;
            return true;
        }
#pragma warning disable CA1031 // The server's reason is shown; the transaction state is read again below.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AddMessage(ex.Message, isError: true);
            Status = ex.Message;
            return false;
        }
        finally
        {
            IsRunning = false;
            await RefreshTransactionAsync();
        }
    }

    /// <summary>Asks the server whether a transaction is open on the tab's connection.</summary>
    public async Task RefreshTransactionAsync()
    {
        if (_channel is not { SupportsTransactions: true } channel)
        {
            HasOpenTransaction = false;
            return;
        }

        try
        {
            HasOpenTransaction = await _jobs.Enqueue("Transaction state", context => channel.HasOpenTransactionAsync(context.CancellationToken), JobPriority.High)
                .Completion;
        }
#pragma warning disable CA1031 // A broken connection holds no transaction the tab can end.
        catch (Exception)
#pragma warning restore CA1031
        {
            HasOpenTransaction = false;
        }
    }

    /// <summary>
    /// Lays <paramref name="text"/> out with the connection's SQL formatter, as a background job. Null when the
    /// connection's language has no formatter (or there is no connection); the status says so.
    /// </summary>
    public async Task<string?> FormatAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (LanguageService is not ISqlFormatter formatter)
        {
            Status = SelectedSession is null ? Strings.FormatNeedsConnection : Strings.FormatUnavailable;
            return null;
        }

        var options = FormatOptionsSource?.Invoke() ?? FormatOptions;
        var formatted = await _jobs.Enqueue("Format SQL", _ => Task.FromResult(formatter.Format(text, options)), JobPriority.High).Completion;
        Status = formatted == text ? Strings.FormatNoChange : Strings.FormatDone;
        return formatted;
    }

    /// <summary>The snippets the editor expands (shortcut + Tab) and lists; null for none.</summary>
    public ISnippetService? Snippets { get; set; }

    /// <summary>How <see cref="FormatAsync"/> lays SQL out, unless <see cref="FormatOptionsSource"/> says.</summary>
    public SqlFormatOptions FormatOptions { get; set; } = SqlFormatOptions.Default;

    /// <summary>Gives the options in effect at the moment of formatting (the settings, which may change).</summary>
    public Func<SqlFormatOptions>? FormatOptionsSource { get; set; }

    /// <summary>The statement at <paramref name="offset"/> of the text (Ctrl+Enter); null without a language or a statement.</summary>
    public TextSpan? StatementAt(int offset) => LanguageService is { } language ? StatementLocator.Find(Text, offset, language) : null;

    /// <summary>
    /// Runs the selection, or else only the statement at <paramref name="offset"/> (DBeaver's Ctrl+Enter):
    /// statements end at a semicolon, an empty line or a batch separator.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunStatement))]
    private Task RunStatementAsync(int offset)
    {
        if (!string.IsNullOrWhiteSpace(SelectedText))
        {
            return RunSqlAsync(SelectedText);
        }

        if (StatementAt(offset) is not { } span)
        {
            Status = Strings.NoStatementAtCaret;
            return Task.CompletedTask;
        }

        return RunSqlAsync(Text.Substring(span.Start, span.Length));
    }

    private bool CanRunStatement(int offset) => CanRun();

    private string TextToRun() => string.IsNullOrWhiteSpace(SelectedText) ? Text : SelectedText;

    private bool CanRefresh(QueryResultSet? set) => !IsRunning && (set?.PagedSql ?? LastSql) is not null && SelectedSession is not null;

    /// <summary>
    /// DBeaver's Refresh of a result: a result of a single SELECT reads its first page again in place (its filter
    /// kept); otherwise, or without a result, the last SQL runs again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync(QueryResultSet? set)
    {
        if (set is { PagedSql: not null })
        {
            if (await RefetchAsync(set, Math.Max(1, FetchSize)))
            {
                set.TotalRows = null;
            }

            return;
        }

        if (LastSql is { } sql)
        {
            await RunSqlAsync(sql, Tabs.OfType<QueryResultSet>().LastOrDefault(s => s.PagedSql == sql)?.AppliedFilter, parameters: _lastParameters);
        }
    }

    private bool CanCloseResult(IResultTab? tab) => tab is QueryResultSet && !IsRunning;

    /// <summary>Closes one result tab (the messages stay).</summary>
    [RelayCommand(CanExecute = nameof(CanCloseResult))]
    private void CloseResult(IResultTab? tab)
    {
        var index = tab is QueryResultSet found ? Tabs.IndexOf(found) : -1;
        if (index < 0 || tab is not QueryResultSet set)
        {
            return;
        }

        Tabs.Remove(set);
        if (ReferenceEquals(SelectedTab, set) || SelectedTab is null)
        {
            SelectedTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        }

        if (!Tabs.OfType<QueryResultSet>().Any())
        {
            _resultCount = 0;
        }
    }

    /// <summary>
    /// Runs <paramref name="sql"/>; with <paramref name="filter"/>, narrowed by that SQL condition (Refresh keeps the
    /// filter); with <paramref name="keepResults"/>, after the results already shown instead of in their place.
    /// </summary>
    /// <param name="parameters">
    /// The values of its parameters (Refresh); null to look for placeholders (<c>:name</c>, <c>${name}</c>) and ask for them.
    /// </param>
    private async Task RunSqlAsync(string sql, string? filter = null, bool keepResults = false, IReadOnlyList<QueryParameter>? parameters = null)
    {
        var session = SelectedSession!.Session;
        if (parameters is null)
        {
            if (await PrepareParametersAsync(sql, session) is not { } prepared)
            {
                Status = Strings.QueryNotSent;
                return;
            }

            (sql, parameters) = prepared;
        }

        await CloseCursorAsync();
        if (!keepResults)
        {
            ClearResults();
        }
        var risk = StatementGuard.Analyze(sql, session.Provider);
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
        _lastParameters = parameters;
        var fetchSize = Math.Max(1, FetchSize);
        var pageable = !risk.WritesData && session.Provider is IRowCountQueryBuilder builder && builder.BuildCountQuery(sql) is not null;
        if (!pageable || Filtered(session, sql, filter) is not { } run)
        {
            (run, filter) = (sql, null);
        }

        // Manual commit: what can change data runs inside a transaction that waits for Commit or Rollback.
        if (!AutoCommit && risk.WritesData && channel.SupportsTransactions)
        {
            try
            {
                await _jobs.Enqueue("Begin transaction", context => channel.BeginTransactionAsync(context.CancellationToken), JobPriority.High).Completion;
            }
#pragma warning disable CA1031 // The server's reason is shown and nothing is sent.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                IsRunning = false;
                AddMessage(ex.Message, isError: true);
                Status = Strings.QueryNotSent;
                return;
            }
        }

        // Batches of one page (within reason), so that a page ends exactly at the fetch size.
        var request = new QueryRequest(run) { BatchSize = Math.Min(fetchSize, 5000), Parameters = parameters };
        var cursor = new QueryCursor(channel, request, session, pageable ? fetchSize : MaxRows) { SelectFirst = keepResults };
        _cursor = cursor;
        await ReadPageAsync(cursor);
        await RefreshTransactionAsync();

        // The one result of a single SELECT can be paged, counted and filtered on the server.
        if (pageable && cursor.First is { } set)
        {
            set.PagedSql = sql;
            set.Parameters = parameters;
            set.CanFilter = session.Provider is IResultFilterQueryBuilder;
            set.AppliedFilter = filter;
            set.SqlFilter = filter ?? string.Empty;
            await PrepareEditorAsync(set, session, sql);
            FetchNextPageCommand.NotifyCanExecuteChanged();
            FetchAllCommand.NotifyCanExecuteChanged();
            ApplyFilterCommand.NotifyCanExecuteChanged();
            ClearFilterCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary><paramref name="sql"/> narrowed by <paramref name="filter"/>; the SQL itself when there is no filter; null when it cannot be filtered.</summary>
    private static string? Filtered(ConnectionSession session, string sql, string? filter) =>
        string.IsNullOrWhiteSpace(filter) ? sql
        : session.Provider is IResultFilterQueryBuilder builder && builder.BuildFilterQuery(sql, filter) is { } filtered && !StatementGuard.Analyze(filtered).WritesData ? filtered
        : null;

    /// <summary>The SQL the rows of <paramref name="set"/> come from, with its filter; the last run for a result of a script.</summary>
    private string? SqlOf(QueryResultSet set, ConnectionSession session) =>
        set.PagedSql is { } sql ? Filtered(session, sql, set.AppliedFilter) : LastSql;

    private static readonly string[] FilterKeywords = ["AND", "OR", "NOT", "LIKE", "IN", "IS NULL", "IS NOT NULL", "BETWEEN"];

    /// <summary>
    /// Completion in a result's SQL filter box: the result's columns (with their types, quoted by the provider
    /// when the name needs it) and the words of a condition. After AND, OR, NOT, "(" or at the start only the
    /// columns are offered; after a column or a value, the words.
    /// </summary>
    /// <param name="set">The result the box filters.</param>
    /// <param name="text">The box's text.</param>
    /// <param name="offset">The caret.</param>
    public IReadOnlyList<CompletionItem> FilterCompletions(QueryResultSet set, string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(text);
        offset = Math.Clamp(offset, 0, text.Length);
        var start = offset;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '$' or '#'))
        {
            start--;
        }

        // Inside a string literal nothing is offered.
        if (text.AsSpan(0, start).Count('\'') % 2 == 1)
        {
            return [];
        }

        var prefix = text[start..offset];
        var span = new TextSpan(start, offset - start);
        var before = text[..start].TrimEnd();
        var lastWord = before.Length == 0 ? string.Empty : before[(before.LastIndexOfAny([' ', '(', '\t']) + 1)..];
        var wantsColumn = before.Length == 0 || before.EndsWith('(') || lastWord.ToUpperInvariant() is "AND" or "OR" or "NOT";
        var provider = SelectedSession?.Session.Provider;

        var items = new List<CompletionItem>();
        if (wantsColumn || prefix.Length > 0)
        {
            foreach (var column in set.Columns.DistinctBy(static c => c.Header, StringComparer.Ordinal))
            {
                if (column.Header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var plain = IsPlainName(column.Header);
                    items.Add(new CompletionItem(column.Header, CompletionItemKind.Column, span)
                    {
                        InsertText = plain || provider is null ? column.Header : provider.QuoteIdentifier(column.Header),
                        Detail = column.DataType.Length > 0 ? column.DataType : null,
                    });
                }
            }
        }

        if (!wantsColumn || prefix.Length > 0)
        {
            foreach (var keyword in FilterKeywords)
            {
                if (keyword.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !(before.Length == 0 && keyword is "AND" or "OR"))
                {
                    items.Add(new CompletionItem(keyword, CompletionItemKind.Keyword, span) { InsertText = keyword });
                }
            }
        }

        return items;

        static bool IsPlainName(string name) =>
            name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_')
            && !FilterKeywords.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private bool CanApplyFilter(QueryResultSet? set) =>
        !IsRunning && set is { CanFilter: true, PagedSql: not null } && SelectedSession is not null;

    /// <summary>
    /// Narrows the result to the rows matching the SQL condition in its filter box (DBeaver's "Enter a SQL
    /// expression to filter results"): the query runs again wrapped in a WHERE and its first page is shown. An
    /// empty box removes the filter.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyFilter))]
    private async Task ApplyFilterAsync(QueryResultSet? set)
    {
        if (set?.PagedSql is not { } sql || SelectedSession?.Session is not { } session)
        {
            return;
        }

        var filter = string.IsNullOrWhiteSpace(set.SqlFilter) ? null : set.SqlFilter.Trim();
        if (Filtered(session, sql, filter) is not { } run)
        {
            AddMessage(Strings.FilterInvalid, isError: true);
            Status = Strings.FilterInvalid;
            return;
        }

        if (await RefetchAsync(set, Math.Max(1, FetchSize), run))
        {
            set.AppliedFilter = filter;
            set.TotalRows = null;
        }
    }

    /// <summary>Removes the result's SQL filter and shows its first page again.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyFilter))]
    private Task ClearFilterAsync(QueryResultSet? set)
    {
        if (set is null)
        {
            return Task.CompletedTask;
        }

        set.SqlFilter = string.Empty;
        return set.AppliedFilter is null ? Task.CompletedTask : ApplyFilterAsync(set);
    }

    private bool CanFetchMore(QueryResultSet? set) =>
        !IsRunning && set is { HasMore: true, PagedSql: not null } && SelectedSession is not null;

    /// <summary>Fetches one more page: the query runs again and is read up to the next page's end, then closed.</summary>
    [RelayCommand(CanExecute = nameof(CanFetchMore))]
    private Task FetchNextPageAsync(QueryResultSet? set) =>
        set is null ? Task.CompletedTask : RefetchAsync(set, (int)Math.Min(MaxRows, (long)set.RowCount + Math.Max(1, FetchSize)));

    /// <summary>Fetches every row (up to <see cref="MaxRows"/>): the query runs again and is read to its end.</summary>
    [RelayCommand(CanExecute = nameof(CanFetchMore))]
    private Task FetchAllAsync(QueryResultSet? set) => set is null ? Task.CompletedTask : RefetchAsync(set, MaxRows);

    /// <summary>
    /// Runs the paged SELECT again and reads it up to <paramref name="target"/> rows, then closes it. The rows
    /// replace those shown, so that all of them come from one execution: no row is skipped or shown twice when
    /// the data changed in between, or when the query has no ORDER BY.
    /// </summary>
    /// <returns>Whether the rows were replaced.</returns>
    private async Task<bool> RefetchAsync(QueryResultSet set, int target, string? sqlToRun = null)
    {
        if (SelectedSession?.Session is not { } session || (sqlToRun ?? SqlOf(set, session)) is not { } sql)
        {
            return false;
        }

        // Rows read again replace the edited ones: the user says whether the changes may go.
        if (set.Editor is { HasChanges: true } editor &&
            !await _dialogs.ConfirmAsync(Strings.EditDiscardTitle, Format(Strings.EditDiscard, editor.ChangeCount)))
        {
            return false;
        }

        if (await EnsureChannelAsync() is not { } channel)
        {
            return false;
        }

        IsRunning = true;
        Status = Format(Strings.QueryRunning, set.RowCount);
        var started = Stopwatch.GetTimestamp();
        var request = new QueryRequest(sql) { BatchSize = Math.Min(Math.Max(1, FetchSize), 5000), Parameters = set.Parameters };
        var cursor = new QueryCursor(channel, request, session, target);
        var rows = new List<object?[]>(Math.Min(target, 100_000));
        var resultSets = 0;
        var columns = 0;
        try
        {
            var job = _jobs.Enqueue(
                "Fetch rows",
                context => cursor.ReadAsync(
                    (_, item) =>
                    {
                        switch (item)
                        {
                            case ResultSetStarted startedSet:
                                columns = startedSet.Columns.Count;
                                return ++resultSets == 1;
                            case RowBatch batch:
                                rows.AddRange(batch.Rows);
                                var count = rows.Count;
                                _ui.Post(() => Status = Format(Strings.QueryRunning, count));
                                return rows.Count < target;
                            default:
                                return true;
                        }
                    },
                    context.CancellationToken));
            _run = job;
            var finished = await job.Completion;
            await DisposeCursorAsync(cursor);

            if (columns != set.Columns.Count)
            {
                // The table changed between the runs: the shown rows cannot take the new ones.
                AddMessage(Strings.ResultShapeChanged, isError: true);
                Status = Strings.ResultShapeChanged;
                return false;
            }

            var more = !finished && rows.Count >= target && target < MaxRows;
            set.ReplaceRows(rows);
            set.HasMore = more;
            var elapsed = Stopwatch.GetElapsedTime(started);
            set.FetchStatus = Format(Strings.ResultFetched, rows.Count, Seconds(elapsed), Seconds(elapsed), DateTime.Now.ToString("T", CultureInfo.CurrentCulture));
            Status = more ? Format(Strings.QueryPaused, rows.Count) : Format(Strings.QueryDone, rows.Count, Elapsed(cursor.Watch));
            if (!finished && target >= MaxRows)
            {
                AddMessage(Format(Strings.QueryTruncated, MaxRows), isError: false);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            await DisposeCursorAsync(cursor);
            Status = Format(Strings.QueryCanceled, Elapsed(cursor.Watch));
            return false;
        }
#pragma warning disable CA1031 // The server's reason is shown; the rows already shown stay.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await DisposeCursorAsync(cursor);
            AddMessage(ex.Message, isError: true);
            Status = Format(Strings.QueryFailed, Elapsed(cursor.Watch));
            return false;
        }
        finally
        {
            _run = null;
            IsRunning = false;
            FetchNextPageCommand.NotifyCanExecuteChanged();
            FetchAllCommand.NotifyCanExecuteChanged();
            ApplyFilterCommand.NotifyCanExecuteChanged();
            ClearFilterCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanCount(QueryResultSet? set) => set is { IsCounting: false } && LastSql is not null && SelectedSession is not null;

    private bool CanExport(QueryResultSet? set) => (set ?? SelectedTab as QueryResultSet) is { Columns.Count: > 0 } && !IsExporting;

    /// <summary>An export is writing its file (the command waits for it).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool IsExporting { get; set; }

    /// <summary>
    /// Writes a result to a file (CSV, Excel, JSON, SQL, Markdown, HTML): the rows shown, in the grid's order and
    /// columns, or the whole result read again on a connection of its own and streamed to the file.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(QueryResultSet? set)
    {
        set ??= SelectedTab as QueryResultSet;
        if (set is null)
        {
            return;
        }

        var session = SelectedSession?.Session;
        var sql = session is null ? null : SqlOf(set, session);
        var export = new ExportResultViewModel(set.View.RowCount, sql is not null && set.PagedSql is not null, Title.TrimEnd('*', ' '), _dialogs.DocumentsFolder);
        if (!await _dialogs.ExportResultAsync(export) || !export.IsValid)
        {
            return;
        }

        var options = export.Options;
        var path = export.Path.Trim();
        var columns = set.RecordColumns ?? [.. Enumerable.Range(0, set.Columns.Count)];
        var headers = columns.Select(c => set.Columns[c].Header).ToList();
        var database = SelectedDatabase;
        IAsyncEnumerable<object?[]> rows;
        if (export.AllRows && session is not null && sql is not null)
        {
            rows = ReadAgain(session, sql, set.Parameters, database, columns);
        }
        else
        {
            var snapshot = set.SnapshotRows();
            var order = Enumerable.Range(0, set.View.RowCount).Select(set.View.SourceRow).ToList();
            rows = Shown(snapshot, order, columns);
        }

        IsExporting = true;
        Status = Format(Strings.ExportRunning, 0L);
        var progress = new Progress<long>(count => Status = Format(Strings.ExportRunning, count));
        try
        {
            var written = await _jobs.Enqueue(
                "Export result",
                async context =>
                {
                    var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                    await using (file.ConfigureAwait(false))
                    {
                        return await ResultExporter.WriteAsync(file, options, headers, rows, progress, context.CancellationToken).ConfigureAwait(false);
                    }
                },
                JobPriority.Normal).Completion;
            Status = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ExportDone), written, path);
            if (options.Format == ResultExportFormat.Excel && written > ResultExporter.ExcelMaxRows)
            {
                AddMessage(Format(Strings.ExportExcelTruncated, ResultExporter.ExcelMaxRows), isError: false);
            }

            if (export.OpenWhenDone)
            {
                _dialogs.OpenWithShell(path);
            }
        }
        catch (OperationCanceledException)
        {
            Status = Strings.ExportCanceled;
        }
#pragma warning disable CA1031 // The file system's or the server's reason is shown; nothing else is disturbed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Status = Format(Strings.ExportFailed, ex.Message);
            AddMessage(Status, isError: true);
        }
        finally
        {
            IsExporting = false;
        }

        static async IAsyncEnumerable<object?[]> Shown(object?[][] snapshot, List<int> order, IReadOnlyList<int> columns)
        {
            foreach (var index in order)
            {
                var source = snapshot[index];
                yield return [.. columns.Select(c => source[c])];
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        // The query again, on a connection of its own in the same database: every row, in batches, to the file.
        static async IAsyncEnumerable<object?[]> ReadAgain(
            ConnectionSession session, string sql, IReadOnlyList<QueryParameter> parameters, string? database, IReadOnlyList<int> columns,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var channel = await session.OpenChannelAsync(cancellationToken).ConfigureAwait(false);
            await using (channel.ConfigureAwait(false))
            {
                if (database is not null && channel.ContextKind is not null)
                {
                    await channel.ChangeDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
                }

                await foreach (var item in channel.ExecuteAsync(new QueryRequest(sql) { BatchSize = 2000, Parameters = parameters }, cancellationToken).ConfigureAwait(false))
                {
                    if (item is RowBatch batch)
                    {
                        foreach (var row in batch.Rows)
                        {
                            yield return [.. columns.Select(c => c < row.Length ? row[c] : null)];
                        }
                    }
                    else if (item is ResultSetCompleted)
                    {
                        yield break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Counts the rows of the last query without fetching them (DBeaver's "calculate total row count"), on the
    /// window's connection when it is free, otherwise on a second connection in the same database.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCount))]
    private async Task CountRowsAsync(QueryResultSet? set)
    {
        if (set is null || SelectedSession?.Session is not { } session || SqlOf(set, session) is not { } sql)
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
            var own = !IsRunning ? _channel : null;
            var parameters = set.Parameters;
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

                        await foreach (var item in channel.ExecuteAsync(new QueryRequest(countSql) { MaxRows = 1, Parameters = parameters }, token).ConfigureAwait(false))
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

    /// <summary>Stops the running statement.</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        if (_run is not { } run)
        {
            return;
        }

        run.Cancel();
        Status = Strings.QueryCanceling;
        _ = WarnIfCancelIsSlowAsync(run);
    }

    /// <summary>How long a cancel may take before the status says why it has not stopped yet.</summary>
    internal static TimeSpan SlowCancel { get; set; } = TimeSpan.FromSeconds(4);

    // A statement waiting on a lock stops only when the server gets the cancel; say so instead of seeming stuck.
    private async Task WarnIfCancelIsSlowAsync(object run)
    {
        await Task.Delay(SlowCancel);
        if (IsRunning && ReferenceEquals(run, _run))
        {
            Status = Strings.QueryCancelSlow;
        }
    }

    /// <summary>Reads events until the current result set has its page, or the run ends.</summary>
    private async Task ReadPageAsync(QueryCursor cursor)
    {
        IsRunning = true;
        cursor.PageStarted = Stopwatch.GetTimestamp();
        try
        {
            var job = _jobs.Enqueue("Run query", context => cursor.ReadAsync(OnEvent, context.CancellationToken));
            _run = job;
            var finished = await job.Completion;
            if (!finished)
            {
                // A page is complete and more rows may follow. The statement is closed at once: nothing waits on
                // the server between pages; the next page runs the query again.
                await DisposeCursorAsync(cursor);
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

        // The script may have switched database (USE): the selector follows.
        _ = RefreshDatabaseAsync(cursor.Channel, withList: false);
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
                cursor.PageEnd = cursor.FirstPageEnd;
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
        var created = new QueryResultSet(Format(Strings.ResultTitle, Interlocked.Increment(ref _resultCount)), cursor.PendingColumns!, sample, _jobs, _ui);
        cursor.PendingColumns = null;
        var first = ++cursor.ResultNumber == 1;
        if (first)
        {
            cursor.First = created;
        }

        var select = first && cursor.SelectFirst;
        _ui.Post(() =>
        {
            Tabs.Insert(Tabs.Count - 1, created);
            if (select || ReferenceEquals(SelectedTab, Messages))
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

    /// <summary>
    /// Makes the result of a single SELECT editable when its rows come from one table whose primary key the result
    /// has (the table resolved as Ctrl+Click resolves it, its key read from the catalog); else says why not.
    /// </summary>
    private async Task PrepareEditorAsync(QueryResultSet set, ConnectionSession session, string sql)
    {
        if (session.Provider is not IRowEditScripting || session.Provider.LanguageService is not { } service)
        {
            return;
        }

        if (session.Definition.IsReadOnly)
        {
            set.ReadOnlyReason = Strings.EditReadOnlyConnection;
            return;
        }

        if (EditableSelect.Analyze(sql) is not { } shape)
        {
            set.ReadOnlyReason = Strings.EditNotSingleTable;
            return;
        }

        var metadata = SessionMetadata(session);
        var document = Document(sql, metadata);
        var selected = _channel?.ContextKind == DatabaseContextKind.Database ? SelectedDatabase : null;
        try
        {
            var found = await _jobs.Enqueue(
                "Find the table to edit",
                async context =>
                {
                    var definition = await service.GetDefinitionAsync(document, shape.TableOffset, context.CancellationToken).ConfigureAwait(false);
                    if (definition?.Target is not { Kind: DatabaseObjectKinds.Table } table)
                    {
                        return ((DatabaseObjectName, IReadOnlyList<ColumnInfo>)?)null;
                    }

                    var name = session.HasMetadataPerDatabase ? table.Name with { Database = null } : table.Name;
                    var database = session.HasMetadataPerDatabase ? table.Name.Database ?? selected : null;
                    var columns = await _metadata.GetColumnsAsync(session, name, refresh: false, context.CancellationToken, database).ConfigureAwait(false);
                    return (name, columns);
                },
                JobPriority.Low).Completion;
            if (found is not var (table, columns) || columns.Count == 0)
            {
                set.ReadOnlyReason = Strings.EditNoTable;
                return;
            }

            // Each result column shows the table column of its name, when the select list has it plain (or *).
            var byName = columns.GroupBy(static c => c.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(static g => g.Key, static g => g.First(), StringComparer.OrdinalIgnoreCase);
            var headers = set.Columns.Select(static c => c.Header).ToArray();
            var duplicated = headers.GroupBy(static h => h, StringComparer.OrdinalIgnoreCase).Where(static g => g.Count() > 1).Select(static g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targets = headers.Select(h =>
                duplicated.Contains(h) ? null
                : shape.Plain.TryGetValue(h, out var source) ? byName.GetValueOrDefault(source)
                : shape.AllColumns ? byName.GetValueOrDefault(h)
                : null).ToArray();
            var keys = columns.Where(static c => c.IsPrimaryKey).Select(k => Array.FindIndex(targets, t => t is not null && string.Equals(t.Name, k.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (keys.Length == 0 || keys.Any(static k => k < 0))
            {
                set.ReadOnlyReason = Strings.EditNoKey;
                return;
            }

            var editor = new ResultEditor(set, table, selected, targets, keys, session.Provider is IBulkInsertScripting);
            editor.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ResultEditor.ChangeCount))
                {
                    SaveChangesCommand.NotifyCanExecuteChanged();
                }
            };
            set.ReadOnlyReason = null;
            set.Editor = editor;
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Editing is an extra: the rows stay read-only and say why.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            set.ReadOnlyReason = ex.Message;
        }
    }

    private bool CanSaveChanges(QueryResultSet? set) => !IsRunning && set?.Editor is { HasChanges: true } && SelectedSession is not null;

    /// <summary>
    /// Saves the result's changes to its table in one transaction on a connection of its own (a production
    /// connection asks first), then reads the rows again; on an error nothing is kept and the changes stay.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveChanges))]
    private async Task SaveChangesAsync(QueryResultSet? set)
    {
        if (set?.Editor is not { HasChanges: true } editor || SelectedSession?.Session is not { } session)
        {
            return;
        }

        var table = editor.Table.Name;
        if (session.Definition.Environment == ConnectionEnvironment.Production &&
            !await _dialogs.ConfirmAsync(Strings.EditConfirmTitle, Format(Strings.EditConfirmProduction, editor.ChangeCount, table, session.Definition.Name)))
        {
            return;
        }

        // The open cursor of the result is closed first: it may hold a lock on the rows being written.
        await CloseCursorAsync();
        var changes = editor.Changes();
        IsRunning = true;
        Status = Strings.EditSaving;
        try
        {
            var saved = await _jobs.Enqueue(
                "Save changes",
                context => ResultEditor.SaveAsync(session, editor.Table, editor.Database, changes, context.CancellationToken),
                JobPriority.High).Completion;
            editor.Clear();
            AddMessage(Format(Strings.EditSaved, saved, table), isError: false);
        }
        catch (OperationCanceledException)
        {
            Status = Strings.EditSaveFailed;
            return;
        }
#pragma warning disable CA1031 // The server's reason is shown; the changes stay to be fixed or undone.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AddMessage(ex.Message, isError: true);
            AddMessage(Strings.EditSaveFailed, isError: true);
            Status = Strings.EditSaveFailed;
            return;
        }
        finally
        {
            IsRunning = false;
        }

        if (await RefetchAsync(set, Math.Max(Math.Max(1, FetchSize), set.RowCount)))
        {
            set.TotalRows = null;
        }

        Status = Format(Strings.EditSaved, changes.Count, table);
    }

    private IReadOnlyList<QueryParameter> _lastParameters = [];

    // The values given in this window, offered again the next time a placeholder of the same name is asked for.
    private readonly Dictionary<string, (QueryParameterKind Kind, string Value)> _parameterValues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The text to run and its parameters: without placeholders the text as it is; with them, the values asked for
    /// (a dialog) bound as parameters and the texts put in. Null when the user cancels.
    /// </summary>
    private async Task<(string Sql, IReadOnlyList<QueryParameter> Parameters)?> PrepareParametersAsync(string sql, ConnectionSession session)
    {
        var placeholders = QueryPlaceholders.Find(sql);
        var syntax = session.Provider as IQueryParameterSyntax;
        if (syntax is null)
        {
            // Values cannot be bound here: only the text placeholders are asked for.
            placeholders = [.. placeholders.Where(static p => p.IsText)];
        }

        if (placeholders.Count == 0)
        {
            return (sql, []);
        }

        var items = placeholders
            .DistinctBy(static p => (p.Name.ToUpperInvariant(), p.IsText))
            .Select(p =>
            {
                var item = new QueryParameterItem(p.Name, p.IsText);
                if (_parameterValues.TryGetValue((p.IsText ? "$" : ":") + p.Name, out var last))
                {
                    (item.Kind, item.Value) = last;
                }

                return item;
            })
            .ToList();
        var dialog = new QueryParametersViewModel(items);
        if (!await _dialogs.AskQueryParametersAsync(dialog) || !dialog.TryBuild(out var values, out var texts))
        {
            return null;
        }

        foreach (var item in items)
        {
            _parameterValues[(item.IsText ? "$" : ":") + item.Name] = (item.Kind, item.Value);
        }

        // Each name once, as the placeholders spell it (Oracle binds by name; the others take repeats too).
        var bound = values.Select(v => v with { Name = placeholders.First(p => !p.IsText && p.Name.Equals(v.Name, StringComparison.OrdinalIgnoreCase)).Name }).ToList();
        var byName = bound.ToDictionary(static v => v.Name, StringComparer.OrdinalIgnoreCase);
        var text = QueryPlaceholders.Apply(
            sql,
            placeholders,
            name => syntax!.ParameterReference(byName[name].Name),
            texts.ToDictionary(static t => t.Key, static t => t.Value, StringComparer.OrdinalIgnoreCase));
        return (text, bound);
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

        /// <param name="firstPageEnd">Rows after which a result set stops: the fetch size, or MaxRows to read it all.</param>
        public QueryCursor(QueryChannel channel, QueryRequest request, ConnectionSession session, int firstPageEnd)
        {
            Channel = channel;
            Request = request;
            Session = session;
            FirstPageEnd = firstPageEnd;
            PageEnd = firstPageEnd;
            _events = channel.ExecuteAsync(request, _cancel.Token).GetAsyncEnumerator(_cancel.Token);
        }

        public QueryChannel Channel { get; }

        public QueryRequest Request { get; }

        public ConnectionSession Session { get; }

        public int FirstPageEnd { get; }

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public Stopwatch Watch { get; } = Stopwatch.StartNew();

        public long PageStarted { get; set; }

        public TimeSpan? ExecuteTime { get; set; }

        public bool FirstPage { get; set; } = true;

        public IReadOnlyList<ResultColumn>? PendingColumns { get; set; }

        public QueryResultSet? Current { get; set; }

        /// <summary>Results opened by this run so far.</summary>
        public int ResultNumber { get; set; }

        /// <summary>The run's first result.</summary>
        public QueryResultSet? First { get; set; }

        /// <summary>Show the first result as soon as it opens (a run that keeps the earlier results).</summary>
        public bool SelectFirst { get; init; }

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
        _resultCount = 0;
        SelectedTab = Messages;
    }

    private void AddMessage(string text, bool isError) => Messages.Lines.Add(new QueryMessageLine(text, isError));

    private static string Elapsed(Stopwatch watch) => watch.Elapsed.TotalSeconds >= 1
        ? watch.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.CurrentCulture) + " s"
        : watch.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.CurrentCulture) + " ms";

    private static string Format(string format, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(format), arguments);
}
