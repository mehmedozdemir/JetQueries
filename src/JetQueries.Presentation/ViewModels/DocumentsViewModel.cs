using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Connections;
using JetQueries.Core.Jobs;
using JetQueries.Core.Metadata;
using JetQueries.Core.Queries;
using JetQueries.Core.Settings;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.Services;

namespace JetQueries.Presentation.ViewModels;

/// <summary>One query tab as the workspace remembers it.</summary>
public sealed record WorkspaceDocument
{
    public string Title { get; init; } = string.Empty;

    public string? FilePath { get; init; }

    /// <summary>The text as it was in the tab, saved or not.</summary>
    public string Text { get; init; } = string.Empty;

    public bool IsDirty { get; init; }

    public Guid? ConnectionId { get; init; }

    public string? Database { get; init; }
}

/// <summary>The open query tabs, saved while the application runs and restored at the next start.</summary>
public sealed record WorkspaceState
{
    /// <summary>The settings section (file) name.</summary>
    public const string Section = "workspace";

    public IReadOnlyList<WorkspaceDocument> Documents { get; init; } = [];

    public int SelectedIndex { get; init; }
}

/// <summary>View model of the document area: the open query tabs, their files, and the remembered workspace.</summary>
public partial class DocumentsViewModel : ObservableObject
{
    private static readonly TimeSpan WorkspaceDelay = TimeSpan.FromSeconds(1.5);

    private readonly IConnectionManager _manager;
    private readonly IMetadataService _metadata;
    private readonly IQueryHistory _history;
    private readonly IJobQueue _jobs;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly ISettingsStore? _workspace;
    private readonly IAppearanceService? _settings;
    private int _saveVersion;
    private bool _restoring;
    private int _counter;

    /// <param name="workspace">Where the open tabs are remembered; without one they are not.</param>
    /// <param name="settings">The user's settings (the fetch size of new tabs); defaults without.</param>
    public DocumentsViewModel(
        IConnectionManager manager, IMetadataService metadata, IQueryHistory history, IJobQueue jobs, IDialogService dialogs, IUiDispatcher ui,
        ISettingsStore? workspace = null, IAppearanceService? settings = null)
    {
        _settings = settings;
        _manager = manager;
        _metadata = metadata;
        _history = history;
        _jobs = jobs;
        _dialogs = dialogs;
        _ui = ui;
        _workspace = workspace;

        // Raised on whatever thread opened or closed the session.
        manager.SessionsChanged += (_, _) => ui.Post(RefreshSessions);
        Documents.CollectionChanged += (_, _) => ScheduleWorkspaceSave();
        NewQuery();
    }

    public ObservableCollection<QueryDocumentViewModel> Documents { get; } = [];

    [ObservableProperty]
    public partial QueryDocumentViewModel? Selected { get; set; }

    /// <summary>The connection a new query should start on, set by the explorer's selection.</summary>
    public Guid? PreferredConnectionId { get; set; }

    partial void OnSelectedChanged(QueryDocumentViewModel? value) => ScheduleWorkspaceSave();

    [RelayCommand]
    private void NewQuery() => OpenEmpty(PreferredConnectionId, null);

    /// <summary>Opens an empty query tab on a connection and, if given, a database of it (the explorer's New query).</summary>
    public QueryDocumentViewModel OpenEmpty(Guid? connectionId, string? database) => Open(NextTitle(), string.Empty, connectionId, database);

    /// <summary>
    /// Opens a tab with the given text. On a connection that is open the tab starts there; on one that is not,
    /// it waits for it (and is on no connection meanwhile).
    /// </summary>
    public QueryDocumentViewModel Open(string title, string text, Guid? connectionId, string? database = null)
    {
        var document = new QueryDocumentViewModel(title, _manager, _metadata, _history, _jobs, _dialogs, _ui, database)
        {
            Text = text,
            FetchSize = _settings?.Current.FetchSize ?? QueryDocumentViewModel.DefaultFetchSize,
        };
        if (_settings is { } settings)
        {
            document.FormatOptionsSource = () => settings.Current.FormatOptions();
        }
        document.MarkClean();
        if (connectionId is { } id)
        {
            if (document.Sessions.Any(s => s.Session.Definition.Id == id))
            {
                document.SelectSession(id);
            }
            else
            {
                document.SelectedSession = null;
                document.PendingConnectionId = id;
            }
        }

        // "Go to definition" in any tab opens the source in a new tab next to it.
        document.DefinitionOpened += (_, definition) => Open(definition.Title, definition.Text, definition.ConnectionId);
        document.PropertyChanged += OnDocumentChanged;
        Documents.Add(document);
        Selected = document;
        return document;
    }

    /// <summary>Opens an SQL file in a tab, or selects its tab if it is open already.</summary>
    /// <returns>The tab, or null if the file could not be read (the reason is shown in the selected tab).</returns>
    public async Task<QueryDocumentViewModel?> OpenFileAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        path = Path.GetFullPath(path);
        if (Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            Selected = open;
            return open;
        }

        (string Text, Encoding Encoding) file;
        try
        {
            file = await _jobs.Enqueue("Open file", context => SqlFile.ReadAsync(path, context.CancellationToken), JobPriority.High).Completion;
        }
#pragma warning disable CA1031 // The reason (no access, missing, locked) is shown; nothing else changes.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ShowStatus(Format(Strings.FileOpenFailed, Path.GetFileName(path), ex.Message));
            return null;
        }

        // An untouched empty tab (the one every start opens) gives way to the file.
        var replace = Documents is [{ FilePath: null, IsDirty: false, Text.Length: 0 } only] ? only : null;
        var document = Open(Path.GetFileName(path), file.Text, PreferredConnectionId);
        document.FilePath = path;
        document.FileEncoding = file.Encoding;
        document.MarkClean();
        if (replace is not null)
        {
            await CloseWithoutAskingAsync(replace);
        }

        return document;
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        if (await _dialogs.PickFileToOpenAsync() is { } path)
        {
            await OpenFileAsync(path);
        }
    }

    [RelayCommand]
    private Task SaveAsync() => Selected is { } document ? SaveAsync(document, saveAs: false) : Task.CompletedTask;

    [RelayCommand]
    private Task SaveAsAsync() => Selected is { } document ? SaveAsync(document, saveAs: true) : Task.CompletedTask;

    /// <summary>Saves a tab to its file, asking for one when it has none (or <paramref name="saveAs"/>).</summary>
    /// <returns>Whether it was saved.</returns>
    public async Task<bool> SaveAsync(QueryDocumentViewModel document, bool saveAs)
    {
        ArgumentNullException.ThrowIfNull(document);
        var path = document.FilePath;
        if (path is null || saveAs)
        {
            var suggested = document.FilePath is null ? document.Title + ".sql" : Path.GetFileName(document.FilePath);
            path = await _dialogs.PickFileToSaveAsync(suggested, document.FilePath);
            if (path is null)
            {
                return false;
            }
        }

        var text = document.Text;
        var encoding = document.FileEncoding;
        try
        {
            await _jobs.Enqueue("Save file", context => SqlFile.WriteAsync(path, text, encoding, context.CancellationToken), JobPriority.High).Completion;
        }
#pragma warning disable CA1031 // The reason is shown; the tab keeps its changes.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            document.Status = Format(Strings.FileSaveFailed, Path.GetFileName(path), ex.Message);
            return false;
        }

        document.FilePath = path;
        document.Title = Path.GetFileName(path);

        // Typing may have gone on while the file was written: only what was written counts as saved.
        if (document.Text == text)
        {
            document.MarkClean();
        }

        document.Status = Format(Strings.FileSaved, path);
        ScheduleWorkspaceSave();
        return true;
    }

    [RelayCommand]
    private async Task CloseAsync(QueryDocumentViewModel? document)
    {
        if (document is null)
        {
            return;
        }

        // An open transaction is not ended without asking.
        if (document.HasOpenTransaction && !await EndTransactionsAsync([document]))
        {
            return;
        }

        // A changed text is not thrown away without asking (an empty new query has nothing to lose).
        if (document.IsDirty && (document.FilePath is not null || document.Text.Length > 0))
        {
            switch (await _dialogs.AskSaveChangesAsync(document.Title))
            {
                case SaveChangesChoice.Cancel:
                    return;
                case SaveChangesChoice.Save when !await SaveAsync(document, saveAs: false):
                    return;
                default:
                    break;
            }
        }

        await CloseWithoutAskingAsync(document);
    }

    /// <summary>Closes every tab but <paramref name="keep"/>, asking about each changed one; stops at a Cancel.</summary>
    [RelayCommand]
    private async Task CloseOthersAsync(QueryDocumentViewModel? keep)
    {
        foreach (var document in Documents.Where(d => !ReferenceEquals(d, keep)).ToList())
        {
            await CloseAsync(document);
            if (Documents.Contains(document))
            {
                return;
            }
        }

        if (keep is not null)
        {
            Selected = keep;
        }
    }

    /// <summary>Closes every tab, asking about each changed one; stops at a Cancel.</summary>
    [RelayCommand]
    private Task CloseAllAsync() => CloseOthersAsync(null);

    /// <summary>
    /// Before the application closes: tabs with an open transaction are asked about once, together (Commit all,
    /// Roll back all, Cancel). False when the user canceled or a commit failed; the application then stays open.
    /// </summary>
    public async Task<bool> ResolveOpenTransactionsAsync()
    {
        var open = Documents.Where(static d => d.HasOpenTransaction).ToList();
        return open.Count == 0 || await EndTransactionsAsync(open);
    }

    private async Task<bool> EndTransactionsAsync(IReadOnlyList<QueryDocumentViewModel> documents)
    {
        var choice = await _dialogs.AskOpenTransactionAsync(string.Join(", ", documents.Select(static d => d.Title)));
        if (choice == TransactionChoice.Cancel)
        {
            return false;
        }

        foreach (var document in documents)
        {
            if (!await document.EndTransactionAsync(commit: choice == TransactionChoice.Commit))
            {
                Selected = document;
                return false;
            }
        }

        return true;
    }

    private async Task CloseWithoutAskingAsync(QueryDocumentViewModel document)
    {
        document.CancelCommand.Execute(null);
        document.PropertyChanged -= OnDocumentChanged;
        var index = Documents.IndexOf(document);
        Documents.Remove(document);
        if (Documents.Count == 0)
        {
            // As in SSMS and DBeaver the last tab really closes: the empty state offers a new query.
            Selected = null;
        }
        else if (Selected is null || ReferenceEquals(Selected, document))
        {
            Selected = Documents[Math.Clamp(index, 0, Documents.Count - 1)];
        }

        await document.CloseAsync();
    }

    /// <summary>
    /// Brings back the tabs of the last run: texts (saved or not), files (read again if they still exist and the
    /// tab had no unsaved changes), connections and databases (taken up again once those connections are open).
    /// </summary>
    public async Task RestoreAsync()
    {
        if (_workspace is null)
        {
            return;
        }

        WorkspaceState state;
        try
        {
            state = await _jobs.Enqueue(
                "Read workspace",
                context => _workspace.LoadAsync(WorkspaceState.Section, new WorkspaceState(), context.CancellationToken),
                JobPriority.High).Completion;
        }
#pragma warning disable CA1031 // A workspace that cannot be read starts empty; it is not worth stopping for.
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }

        if (state.Documents.Count == 0)
        {
            return;
        }

        _restoring = true;
        try
        {
            var initial = Documents.ToList();
            foreach (var saved in state.Documents)
            {
                var text = saved.Text;
                var dirty = saved.IsDirty;
                var encoding = (Encoding?)null;
                if (saved.FilePath is { } path && !saved.IsDirty)
                {
                    try
                    {
                        var file = await _jobs.Enqueue("Open file", context => SqlFile.ReadAsync(path, context.CancellationToken), JobPriority.High).Completion;
                        (text, encoding) = file;
                    }
#pragma warning disable CA1031 // The file is gone or locked: the remembered text is kept, as unsaved.
                    catch (Exception)
#pragma warning restore CA1031
                    {
                        dirty = true;
                    }
                }

                var document = Open(saved.Title, text, saved.ConnectionId, saved.Database);
                document.FilePath = saved.FilePath;
                document.FileEncoding = encoding ?? document.FileEncoding;
                if (dirty)
                {
                    document.IsDirty = true;
                }
                else
                {
                    document.MarkClean();
                }

                _counter = Math.Max(_counter, NumberOf(saved.Title));
            }

            // The empty tab of this start makes way for the restored ones.
            foreach (var document in initial.Where(static d => d is { FilePath: null, IsDirty: false, Text.Length: 0 }))
            {
                await CloseWithoutAskingAsync(document);
            }

            Selected = Documents.ElementAtOrDefault(Math.Clamp(state.SelectedIndex, 0, Documents.Count - 1)) ?? Selected;
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>Writes the workspace now (the application is closing).</summary>
    public async Task SaveWorkspaceAsync()
    {
        Interlocked.Increment(ref _saveVersion);
        if (_workspace is null)
        {
            return;
        }

        var state = Capture();
        await _jobs.Enqueue("Save workspace", context => _workspace.SaveAsync(WorkspaceState.Section, state, context.CancellationToken), JobPriority.High)
            .Completion.ConfigureAwait(false);
    }

    private WorkspaceState Capture() => new()
    {
        Documents = [.. Documents.Select(static d => new WorkspaceDocument
        {
            Title = d.Title,
            FilePath = d.FilePath,
            Text = d.Text,
            IsDirty = d.IsDirty,
            ConnectionId = d.SelectedSession?.Session.Definition.Id ?? d.PendingConnectionId,
            Database = d.SelectedDatabase ?? d.PendingDatabase,
        })],
        SelectedIndex = Selected is null ? 0 : Math.Max(0, Documents.IndexOf(Selected)),
    };

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QueryDocumentViewModel.Text) or nameof(QueryDocumentViewModel.FilePath) or nameof(QueryDocumentViewModel.SelectedSession)
            or nameof(QueryDocumentViewModel.SelectedDatabase) or nameof(QueryDocumentViewModel.IsDirty))
        {
            ScheduleWorkspaceSave();
        }
    }

    /// <summary>Saves the workspace once typing pauses, in the background.</summary>
    private void ScheduleWorkspaceSave()
    {
        if (_workspace is null || _restoring)
        {
            return;
        }

        _ = SaveLaterAsync(Interlocked.Increment(ref _saveVersion));
    }

    // Only the newest request writes: an older one finds a newer version number when its delay ends.
    private async Task SaveLaterAsync(int version)
    {
        await Task.Delay(WorkspaceDelay).ConfigureAwait(false);

        // The tabs are read on the UI thread; the file is written by a job.
        _ui.Post(() =>
        {
            if (version != Volatile.Read(ref _saveVersion))
            {
                return;
            }

            var state = Capture();
            _ = _jobs.Enqueue("Save workspace", context => _workspace!.SaveAsync(WorkspaceState.Section, state, context.CancellationToken), JobPriority.Low);
        });
    }

    private void ShowStatus(string text)
    {
        if (Selected is { } document)
        {
            document.Status = text;
        }
    }

    private string NextTitle() => Format(Strings.QueryTitle, ++_counter);

    /// <summary>The N of "Query N" in the current language, so new tabs do not repeat restored names.</summary>
    private static int NumberOf(string title)
    {
        var format = Strings.QueryTitle;
        var prefix = format[..Math.Max(0, format.IndexOf("{0}", StringComparison.Ordinal))];
        return title.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(title.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.CurrentCulture, out var n) ? n : 0;
    }

    private void RefreshSessions()
    {
        foreach (var document in Documents)
        {
            document.RefreshSessions();
        }
    }

    private static string Format(string format, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(format), arguments);
}

/// <summary>Reading and writing query files.</summary>
internal static class SqlFile
{
    /// <summary>Largest file opened in an editor tab.</summary>
    public const long MaxSize = 50L * 1024 * 1024;

    // Windows code pages (1254 for Turkish) are not built into .NET until registered.
    static SqlFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// Reads a file in its encoding: a byte order mark decides; without one UTF-8 is tried, and a file that is
    /// not valid UTF-8 is read in the system's ANSI code page (Windows-1254 on Turkish systems), as SSMS does.
    /// </summary>
    public static async Task<(string Text, Encoding Encoding)> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.Length > MaxSize)
        {
            throw new IOException(string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.FileTooLarge), MaxSize / (1024 * 1024)));
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        foreach (var bom in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            var preamble = bom.GetPreamble();
            if (bytes.AsSpan().StartsWith(preamble))
            {
                return (bom.GetString(bytes, preamble.Length, bytes.Length - preamble.Length), bom);
            }
        }

        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return (strict.GetString(bytes), new UTF8Encoding(false));
        }
        catch (DecoderFallbackException)
        {
            var ansi = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            return (ansi.GetString(bytes), ansi);
        }
    }

    /// <summary>Writes the text in the given encoding (with its byte order mark if it has one), replacing the file atomically.</summary>
    public static async Task WriteAsync(string path, string text, Encoding encoding, CancellationToken cancellationToken)
    {
        var temporary = path + ".jqtmp";
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text);
        var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
