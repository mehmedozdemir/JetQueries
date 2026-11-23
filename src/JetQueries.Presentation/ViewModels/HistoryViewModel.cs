using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Jobs;
using JetQueries.Core.Queries;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.Services;

namespace JetQueries.Presentation.ViewModels;

/// <summary>One run in the history list.</summary>
public sealed class HistoryEntryViewModel(QueryHistoryEntry entry)
{
    public QueryHistoryEntry Entry { get; } = entry;

    /// <summary>Local date and time of the run.</summary>
    public string When { get; } = entry.ExecutedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    /// <summary>The first line that says something, shortened.</summary>
    public string Summary { get; } = Summarize(entry.Sql);

    /// <summary>Connection, then rows and time, or how it ended.</summary>
    public string Detail { get; } = entry.Outcome switch
    {
        QueryOutcome.Failed => $"{entry.ConnectionName} · {Strings.HistoryFailed}",
        QueryOutcome.Canceled => $"{entry.ConnectionName} · {Strings.HistoryCanceled}",
        _ when entry.RowCount is { } rows =>
            $"{entry.ConnectionName} · {string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.HistoryRows), rows)} · {Milliseconds(entry.Duration)}",
        _ => $"{entry.ConnectionName} · {Milliseconds(entry.Duration)}",
    };

    public bool IsFailed => Entry.Outcome == QueryOutcome.Failed;

    /// <summary>The whole text and, for a failure, the database's message.</summary>
    public string ToolTip => Entry.Error is null ? Entry.Sql : Entry.Error + "\n\n" + Entry.Sql;

    private static string Milliseconds(TimeSpan duration) =>
        duration.TotalSeconds >= 1
            ? duration.TotalSeconds.ToString("0.00", CultureInfo.CurrentCulture) + " s"
            : duration.TotalMilliseconds.ToString("0", CultureInfo.CurrentCulture) + " ms";

    private static string Summarize(string sql)
    {
        foreach (var line in sql.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                return trimmed.Length > 140 ? string.Concat(trimmed[..140], "…") : trimmed.ToString();
            }
        }

        return sql.Trim();
    }
}

/// <summary>View model of the history panel: the queries that were run, newest first, with search.</summary>
public partial class HistoryViewModel : ObservableObject
{
    private const int PageSize = 200;

    private readonly IQueryHistory _history;
    private readonly IJobQueue _jobs;
    private readonly IDialogService _dialogs;
    private readonly DocumentsViewModel _documents;
    private readonly IUiDispatcher _ui;
    private int _version;

    public HistoryViewModel(IQueryHistory history, IJobQueue jobs, IDialogService dialogs, IUiDispatcher ui, DocumentsViewModel documents)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(ui);
        _history = history;
        _jobs = jobs;
        _dialogs = dialogs;
        _documents = documents;
        _ui = ui;
        SearchText = string.Empty;

        // Raised on whatever thread recorded the run.
        history.Changed += (_, _) => ui.Post(() => _ = LoadAsync());
    }

    public ObservableCollection<HistoryEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedCommand), nameof(DeleteSelectedCommand))]
    public partial HistoryEntryViewModel? Selected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearCommand))]
    public partial bool IsEmpty { get; set; } = true;

    /// <summary>Reads the newest entries that match the search text.</summary>
    public async Task LoadAsync()
    {
        // Typing fires a load per key; only the answer to the latest request is shown.
        var version = Interlocked.Increment(ref _version);
        var search = SearchText;
        IReadOnlyList<QueryHistoryEntry> entries;
        try
        {
            entries = await _jobs.Enqueue("Load history", context => _history.SearchAsync(search, PageSize, context.CancellationToken), JobPriority.Low).Completion;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Applied on the UI thread, one answer at a time; an answer overtaken by a newer request is dropped.
        _ui.Post(() =>
        {
            if (version != Volatile.Read(ref _version))
            {
                return;
            }

            var selectedId = Selected?.Entry.Id;
            Entries.Clear();
            foreach (var entry in entries)
            {
                Entries.Add(new HistoryEntryViewModel(entry));
            }

            Selected = Entries.FirstOrDefault(e => e.Entry.Id == selectedId);
            IsEmpty = Entries.Count == 0 && search.Length == 0;
        });
    }

    partial void OnSearchTextChanged(string value) => _ = LoadAsync();

    private bool HasSelection() => Selected is not null;

    /// <summary>Opens the selected run's text in a new query tab, on its connection if that is open.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenSelected()
    {
        if (Selected is { } selected)
        {
            _documents.NewQueryCommand.Execute(null);
            var document = _documents.Selected!;
            document.Text = selected.Entry.Sql;
            document.SelectSession(selected.Entry.ConnectionId);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteSelectedAsync()
    {
        if (Selected is { } selected)
        {
            await _jobs.Enqueue("Delete history entry", context => _history.DeleteAsync(selected.Entry.Id, context.CancellationToken)).Completion;
        }
    }

    private bool HasEntries() => !IsEmpty;

    [RelayCommand(CanExecute = nameof(HasEntries))]
    private async Task ClearAsync()
    {
        if (await _dialogs.ConfirmAsync(Strings.HistoryClearConfirmTitle, Strings.HistoryClearConfirmMessage))
        {
            await _jobs.Enqueue("Clear history", context => _history.ClearAsync(context.CancellationToken)).Completion;
        }
    }
}
