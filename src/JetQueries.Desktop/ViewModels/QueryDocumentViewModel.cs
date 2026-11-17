using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Connections;
using JetQueries.Core.Jobs;
using JetQueries.Core.Queries;
using JetQueries.DataGrid;
using JetQueries.Desktop.Resources;
using JetQueries.Desktop.Services;
using JetQueries.Sdk;

namespace JetQueries.Desktop.ViewModels;

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
public sealed class QueryResultSet : IGridRowSource, IResultTab
{
    private readonly List<object?[]> _rows = [];

    /// <param name="title">Caption of the tab.</param>
    /// <param name="columns">The columns of the result set.</param>
    /// <param name="sample">The first rows, used only to choose column widths; they are not added.</param>
    public QueryResultSet(string title, IReadOnlyList<ResultColumn> columns, IReadOnlyList<object?[]>? sample = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        Title = title;
        var gridColumns = new GridColumn[columns.Count];
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

            gridColumns[i] = new GridColumn(
                name,
                Math.Clamp((longest * 7.4) + 24, 64, 420),
                IsNumeric(columns[i].ClrType) ? GridTextAlignment.Right : GridTextAlignment.Left);
        }

        Columns = gridColumns;
    }

    public event EventHandler? RowCountChanged;

    public string Title { get; }

    public IReadOnlyList<GridColumn> Columns { get; }

    public int RowCount => _rows.Count;

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
    private readonly IJobQueue _jobs;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private JobHandle? _run;

    public QueryDocumentViewModel(string title, IConnectionManager manager, IJobQueue jobs, IDialogService dialogs, IUiDispatcher ui)
    {
        Title = title;
        _manager = manager;
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
    public partial SessionOption? SelectedSession { get; set; }

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
        var watch = Stopwatch.StartNew();
        long totalRows = 0;
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
                        var created = new QueryResultSet(Format(Strings.ResultTitle, ++resultNumber), pending!, sample);
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

                    await foreach (var item in session.ExecuteAsync(request, context.CancellationToken).ConfigureAwait(false))
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
        }
        catch (OperationCanceledException)
        {
            Status = Format(Strings.QueryCanceled, Elapsed(watch));
            AddMessage(Status, isError: false);
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
        }
        finally
        {
            _run = null;
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _run?.Cancel();

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
    private readonly IJobQueue _jobs;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private int _counter;

    public DocumentsViewModel(IConnectionManager manager, IJobQueue jobs, IDialogService dialogs, IUiDispatcher ui)
    {
        _manager = manager;
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
        var document = new QueryDocumentViewModel(title, _manager, _jobs, _dialogs, _ui);
        if (PreferredConnectionId is { } id)
        {
            document.SelectSession(id);
        }

        Documents.Add(document);
        Selected = document;
    }

    [RelayCommand]
    private void Close(QueryDocumentViewModel? document)
    {
        if (document is null)
        {
            return;
        }

        document.CancelCommand.Execute(null);
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
