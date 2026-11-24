using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Connections;
using JetQueries.Core.Jobs;
using JetQueries.Core.Library;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.Services;

namespace JetQueries.Presentation.ViewModels;

/// <summary>One query of the library, as listed.</summary>
public sealed class SavedQueryItemViewModel(SavedQuery query, string? connectionName)
{
    public SavedQuery Query { get; } = query;

    public string Name => Query.Name;

    /// <summary>The folder it is listed under.</summary>
    public string Folder => Query.Folder.Length == 0 ? Strings.SavedQueriesNoFolder : Query.Folder;

    /// <summary>The first line of its text.</summary>
    public string Summary { get; } = HistoryEntryViewModel.Summarize(query.Sql);

    /// <summary>"On Kartlar · Sales · 12.01.2026": where it runs and when it was saved.</summary>
    public string Detail { get; } = string.Join(
        " · ",
        new[] { connectionName, query.Database, query.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) }.Where(static p => !string.IsNullOrEmpty(p)));

    /// <summary>The text has placeholders asked for when it runs.</summary>
    public bool HasParameters { get; } = QueryPlaceholders.Find(query.Sql).Count > 0;

    public string ToolTip => Query.Description.Length == 0 ? Query.Sql : Query.Description + "\n\n" + Query.Sql;
}

/// <summary>The Save to library dialog: name, folder, description, and whether it keeps its connection.</summary>
public sealed partial class SavedQueryEditViewModel : ObservableObject
{
    public SavedQueryEditViewModel(string name, string folder, string description, IEnumerable<string> folders, string? connectionName, bool keepConnection)
    {
        Name = name;
        Folder = folder;
        Description = description;
        Folders = [.. folders.Where(static f => f.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase)];
        ConnectionName = connectionName;
        KeepConnection = keepConnection && connectionName is not null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Folder { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    /// <summary>The folders already used, to choose from.</summary>
    public IReadOnlyList<string> Folders { get; }

    /// <summary>The connection of the query window; null when it is on none.</summary>
    public string? ConnectionName { get; }

    /// <summary>Opening the query starts it on that connection.</summary>
    [ObservableProperty]
    public partial bool KeepConnection { get; set; }

    public bool CanKeepConnection => ConnectionName is not null;

    /// <summary>A name was given.</summary>
    public bool IsValid => Name.Trim().Length > 0;
}

/// <summary>
/// The library of saved queries (ANALIZ: query library): saved from a query window with a name, a folder and an
/// optional connection, found again by any word of their name, folder, description or text, opened or run in a new
/// tab. Their placeholders (<c>:name</c>, <c>${name}</c>) are asked for when they run.
/// </summary>
public sealed partial class SavedQueriesViewModel : ObservableObject
{
    private readonly ISavedQueryStore _store;
    private readonly IConnectionStore _connections;
    private readonly IJobQueue _jobs;
    private readonly IDialogService _dialogs;
    private readonly DocumentsViewModel _documents;
    private readonly TimeProvider _time;
    private IReadOnlyList<SavedQuery> _all = [];
    private Dictionary<Guid, string> _connectionNames = [];

    public SavedQueriesViewModel(
        ISavedQueryStore store, IConnectionStore connections, IJobQueue jobs, IDialogService dialogs, DocumentsViewModel documents, TimeProvider? time = null)
    {
        _store = store;
        _connections = connections;
        _jobs = jobs;
        _dialogs = dialogs;
        _documents = documents;
        _time = time ?? TimeProvider.System;
        SearchText = string.Empty;
        Status = string.Empty;
        documents.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DocumentsViewModel.Selected))
            {
                RefreshCommands();
            }
        };
    }

    /// <summary>The queries that match the search, by folder and name.</summary>
    public ObservableCollection<SavedQueryItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedCommand), nameof(RunSelectedCommand), nameof(DeleteSelectedCommand), nameof(EditSelectedCommand))]
    public partial SavedQueryItemViewModel? Selected { get; set; }

    /// <summary>The library has no query yet.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    /// <summary>The search matches none.</summary>
    [ObservableProperty]
    public partial bool IsFilteredEmpty { get; set; }

    /// <summary>What was last done ("Saved to the library").</summary>
    [ObservableProperty]
    public partial string Status { get; set; }

    partial void OnSearchTextChanged(string value) => Filter();

    /// <summary>Reads the library (and the connection names it shows).</summary>
    public async Task LoadAsync()
    {
        var (queries, connections) = await _jobs.Enqueue(
            "Load saved queries",
            async context => (await _store.GetAllAsync(context.CancellationToken).ConfigureAwait(false), await _connections.GetAllAsync(context.CancellationToken).ConfigureAwait(false)),
            JobPriority.Low).Completion;
        _all = queries;
        _connectionNames = connections.ToDictionary(static c => c.Id, static c => c.Name);
        Filter();
    }

    private void Filter()
    {
        var selected = Selected?.Query.Id;
        var words = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        Items.Clear();
        foreach (var query in _all)
        {
            var text = query.Name + "\n" + query.Folder + "\n" + query.Description + "\n" + query.Sql;

            // Every word somewhere, ignoring case the Turkish way too (istanbul finds İstanbul).
            if (words.All(w => compare.IndexOf(text, w, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0))
            {
                Items.Add(new SavedQueryItemViewModel(query, query.ConnectionId is { } id ? _connectionNames.GetValueOrDefault(id) : null));
            }
        }

        IsEmpty = _all.Count == 0;
        IsFilteredEmpty = !IsEmpty && Items.Count == 0;
        Selected = Items.FirstOrDefault(i => i.Query.Id == selected) ?? Items.FirstOrDefault();
    }

    private bool HasSelected() => Selected is not null;

    /// <summary>Opens the selected query in a new tab, on its connection when it has one.</summary>
    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void OpenSelected() => Open(Selected!.Query);

    /// <summary>Opens the selected query and runs it (its parameters are asked for first).</summary>
    [RelayCommand(CanExecute = nameof(HasSelected))]
    private async Task RunSelectedAsync()
    {
        var document = Open(Selected!.Query);
        if (document.RunCommand.CanExecute(null))
        {
            await document.RunCommand.ExecuteAsync(null);
        }
    }

    private QueryDocumentViewModel Open(SavedQuery query)
    {
        var document = _documents.Open(query.Name, query.Sql, query.ConnectionId ?? _documents.PreferredConnectionId, query.Database);
        document.SavedQueryId = query.Id;
        return document;
    }

    /// <summary>Changes the name, folder or description of the selected query.</summary>
    [RelayCommand(CanExecute = nameof(HasSelected))]
    private async Task EditSelectedAsync()
    {
        var query = Selected!.Query;
        var connectionName = query.ConnectionId is { } id ? _connectionNames.GetValueOrDefault(id) : null;
        var edit = new SavedQueryEditViewModel(query.Name, query.Folder, query.Description, _all.Select(static q => q.Folder), connectionName, query.ConnectionId is not null);
        if (!await _dialogs.EditSavedQueryAsync(edit) || !edit.IsValid)
        {
            return;
        }

        await SaveAsync(query with
        {
            Name = edit.Name.Trim(),
            Folder = NormalizeFolder(edit.Folder),
            Description = edit.Description.Trim(),
            ConnectionId = edit.KeepConnection ? query.ConnectionId : null,
            Database = edit.KeepConnection ? query.Database : null,
            UpdatedAt = _time.GetUtcNow(),
        });
    }

    /// <summary>Removes the selected query from the library (after asking).</summary>
    [RelayCommand(CanExecute = nameof(HasSelected))]
    private async Task DeleteSelectedAsync()
    {
        var query = Selected!.Query;
        if (!await _dialogs.ConfirmAsync(Strings.SavedQueryDeleteTitle, string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.SavedQueryDeleteMessage), query.Name)))
        {
            return;
        }

        await _jobs.Enqueue("Delete saved query", context => _store.DeleteAsync(query.Id, context.CancellationToken), JobPriority.Normal).Completion;
        _all = [.. _all.Where(q => q.Id != query.Id)];
        Filter();
    }

    private bool CanSave() => _documents.Selected is not null;

    /// <summary>
    /// Saves the selected query window to the library: a window opened from the library updates its query; another
    /// asks for a name and folder first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveCurrentAsync() => _documents.Selected is { } document ? SaveDocumentAsync(document, asNew: false) : Task.CompletedTask;

    /// <summary>Saves the selected query window as a new query of the library (always asks for a name).</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveCurrentAsNewAsync() => _documents.Selected is { } document ? SaveDocumentAsync(document, asNew: true) : Task.CompletedTask;

    /// <summary>Saves a query window to the library.</summary>
    public async Task<SavedQuery?> SaveDocumentAsync(QueryDocumentViewModel document, bool asNew)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (_all.Count == 0)
        {
            await LoadAsync();
        }

        var now = _time.GetUtcNow();
        var session = document.SelectedSession?.Session;
        var existing = !asNew && document.SavedQueryId is { } id ? _all.FirstOrDefault(q => q.Id == id) : null;
        SavedQuery saved;
        if (existing is not null)
        {
            saved = existing with
            {
                Sql = document.Text,
                ConnectionId = existing.ConnectionId is null ? null : session?.Definition.Id ?? existing.ConnectionId,
                Database = existing.ConnectionId is null ? existing.Database : document.SelectedDatabase ?? existing.Database,
                UpdatedAt = now,
            };
        }
        else
        {
            var name = document.Title.TrimEnd('*', ' ');
            var edit = new SavedQueryEditViewModel(name, string.Empty, string.Empty, _all.Select(static q => q.Folder), session?.Definition.Name, keepConnection: true);
            if (!await _dialogs.EditSavedQueryAsync(edit) || !edit.IsValid)
            {
                return null;
            }

            saved = new SavedQuery
            {
                Id = Guid.NewGuid(),
                Name = edit.Name.Trim(),
                Folder = NormalizeFolder(edit.Folder),
                Description = edit.Description.Trim(),
                Sql = document.Text,
                ConnectionId = edit.KeepConnection ? session?.Definition.Id : null,
                Database = edit.KeepConnection ? document.SelectedDatabase : null,
                CreatedAt = now,
                UpdatedAt = now,
            };
        }

        await SaveAsync(saved);
        document.SavedQueryId = saved.Id;
        document.Title = saved.Name;
        document.Status = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.SavedQuerySaved), saved.Name);
        return saved;
    }

    private async Task SaveAsync(SavedQuery query)
    {
        await _jobs.Enqueue("Save query to the library", context => _store.SaveAsync(query, context.CancellationToken), JobPriority.Normal).Completion;
        _all = [.. _all.Where(q => q.Id != query.Id).Append(query)
            .OrderBy(static q => q.Folder, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static q => q.Name, StringComparer.CurrentCultureIgnoreCase)];
        Status = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.SavedQuerySaved), query.Name);
        Filter();
        Selected = Items.FirstOrDefault(i => i.Query.Id == query.Id) ?? Selected;
    }

    // "Raporlar / Aylık " → "Raporlar/Aylık".
    private static string NormalizeFolder(string folder) =>
        string.Join('/', folder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>The documents changed (another tab selected): saving follows.</summary>
    public void RefreshCommands()
    {
        SaveCurrentCommand.NotifyCanExecuteChanged();
        SaveCurrentAsNewCommand.NotifyCanExecuteChanged();
    }
}
