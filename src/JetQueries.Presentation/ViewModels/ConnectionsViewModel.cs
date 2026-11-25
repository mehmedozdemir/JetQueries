using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Connections;
using JetQueries.Core.Jobs;
using JetQueries.Core.Metadata;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.Services;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>A row of the connection tree: a folder or a connection.</summary>
public abstract partial class ExplorerNodeViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>A table or view: its data can be selected and scripted (INSERT, UPDATE, DELETE).</summary>
    public virtual bool HasData => false;

    /// <summary>The object's definition (CREATE …) can be scripted.</summary>
    public virtual bool HasDefinition => false;

    /// <summary>Whether an ALTER / CREATE OR REPLACE script can be written for the node.</summary>
    public virtual bool CanAlter => false;

    /// <summary>Whether the node can be pinned under the connection's favourites.</summary>
    public virtual bool CanBeFavorite => false;

    /// <summary>Whether the node is pinned.</summary>
    public virtual bool IsFavorite => false;

    public ObservableCollection<ExplorerNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    /// <summary>False while the explorer's filter hides the node.</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    /// <summary>Picked with Ctrl+click, to be moved together with others.</summary>
    [ObservableProperty]
    public partial bool IsMarked { get; set; }

    /// <summary>A dragged connection or folder would land here (drawn highlighted).</summary>
    [ObservableProperty]
    public partial bool IsDropTarget { get; set; }

    /// <summary>Whether the explorer's "DBA" menu has something for the node (dashboard, monitor, analysis).</summary>
    public virtual bool HasDbaTools => false;

    /// <summary>Called when the node is expanded or collapsed, by the user or by code.</summary>
    protected virtual void OnExpanded(bool expanded)
    {
    }

    partial void OnIsExpandedChanged(bool value) => OnExpanded(value);
}

/// <summary>A new query tab wanted on a connection, in a database (or schema) of it if given.</summary>
/// <param name="ConnectionId">The saved connection.</param>
/// <param name="Database">The database (Oracle: schema) to start in; null for the connection's default.</param>
public sealed record NewQueryRequest(Guid ConnectionId, string? Database)
{
    /// <summary>The text the tab starts with (a script written by the explorer); empty for a new query.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The tab's title; null for the next "Query N".</summary>
    public string? Title { get; init; }

    /// <summary>Run the text at once (Select first rows).</summary>
    public bool Run { get; init; }
}

/// <summary>A folder grouping connections.</summary>
public sealed class FolderNodeViewModel(string name, string path) : ExplorerNodeViewModel(name)
{
    /// <summary>The folder's full path ("Customer A/Test").</summary>
    public string Path { get; } = path;
}

/// <summary>A saved connection and its live state.</summary>
public sealed partial class ConnectionNodeViewModel(ConnectionDefinition definition) : LazyNodeViewModel(definition.Name, expandable: false)
{
    private ExplorerContext? _context;

    public ConnectionDefinition Definition { get; } = definition;

    /// <summary>The mark of the connection's database type; the default until the providers are known.</summary>
    [ObservableProperty]
    public partial ProviderMark Mark { get; set; } = ProviderMark.Default;

    public override bool CanLoad => _context is not null;

    /// <summary>The connection is open: show its objects underneath.</summary>
    /// <summary>What the nodes underneath load with; null while closed.</summary>
    public ExplorerContext? Context => _context;

    public void Attach(ExplorerContext context)
    {
        _context = context;
        IsConnected = true;
        Reset(expandable: true);
        IsExpanded = false;
        IsExpanded = true;
    }

    /// <summary>The connection is closed: nothing underneath any more.</summary>
    public void Detach()
    {
        _context = null;
        IsConnected = false;
        IsExpanded = false;
        Reset(expandable: false);
    }

    protected override Task<IReadOnlyList<ExplorerNodeViewModel>> LoadChildrenAsync(bool refresh) =>
        _context is null ? Task.FromResult<IReadOnlyList<ExplorerNodeViewModel>>([]) : ExplorerTree.LoadRootAsync(_context, refresh);

    public bool IsReadOnly => Definition.IsReadOnly;

    public bool IsDevelopment => Definition.Environment == ConnectionEnvironment.Development;

    public bool IsTest => Definition.Environment == ConnectionEnvironment.Test;

    public bool IsProduction => Definition.Environment == ConnectionEnvironment.Production;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMonitor), nameof(CanAnalyze), nameof(CanDashboard), nameof(CanHealthCheck), nameof(HasDbaTools))]
    public partial bool IsConnected { get; set; }

    /// <summary>Open, on a provider with a health check: it can be run.</summary>
    public bool CanHealthCheck => IsConnected && _context?.Session.SupportsHealthCheck == true;

    public override bool HasDbaTools => CanDashboard || CanMonitor || CanAnalyze || CanHealthCheck;

    /// <summary>Open, on a provider that reports the server's load: the dashboard can be opened.</summary>
    public bool CanDashboard => IsConnected && _context?.Session.SupportsDashboard == true;

    /// <summary>Open, on a provider that reports heavy queries and index advice: the performance analysis can be opened.</summary>
    public bool CanAnalyze => IsConnected && _context?.Session.SupportsPerformance == true;

    /// <summary>Open, on a provider that reports the server's sessions: the activity monitor can be opened.</summary>
    public bool CanMonitor => IsConnected && _context?.Session.SupportsActivity == true;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Why the last connection attempt failed; empty when there is nothing to report.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; } = string.Empty;

    public bool HasError => Error.Length > 0;
}

/// <summary>View model of the connection tree in the explorer panel.</summary>
public partial class ConnectionsViewModel(
    IConnectionStore store,
    IConnectionManager manager,
    ICredentialStore credentials,
    IMetadataService metadata,
    IJobQueue jobs,
    IDialogService dialogs,
    FavoriteObjects? favorites = null,
    ConnectionFolders? folders = null) : ObservableObject
{
    /// <summary>The folder a new folder or a drop goes into: a folder itself, a connection's folder, or the top.</summary>
    private static string FolderOf(ExplorerNodeViewModel? node) => node switch
    {
        FolderNodeViewModel folder => folder.Path,
        ConnectionNodeViewModel connection => FolderPath.Normalize(connection.Definition.Folder),
        _ => string.Empty,
    };

    /// <summary>Asks for a name and adds an (empty) folder under the selected folder, or at the top.</summary>
    [RelayCommand]
    private async Task NewFolderAsync(ExplorerNodeViewModel? node)
    {
        var parent = FolderOf(node ?? Selected);
        var name = await AskFolderNameAsync(Strings.FolderNewTitle, string.Empty);
        if (name is null)
        {
            return;
        }

        var path = FolderPath.Combine(parent, name);
        folders?.Add(path);
        await LoadAsync(select: path);
    }

    /// <summary>Renames a folder; its connections and subfolders follow.</summary>
    [RelayCommand]
    private async Task RenameFolderAsync(ExplorerNodeViewModel? node)
    {
        if ((node ?? Selected) is not FolderNodeViewModel folder || await AskFolderNameAsync(Strings.FolderRenameTitle, folder.Name) is not { } name ||
            string.Equals(name, folder.Name, StringComparison.Ordinal))
        {
            return;
        }

        var target = FolderPath.Combine(FolderPath.Parent(folder.Path), name);
        await MoveFolderAsync(folder.Path, target);
        await LoadAsync(select: target);
    }

    /// <summary>Removes a folder; what was inside moves up to the folder's parent (nothing is deleted).</summary>
    [RelayCommand]
    private async Task DeleteFolderAsync(ExplorerNodeViewModel? node)
    {
        if ((node ?? Selected) is not FolderNodeViewModel folder ||
            !await dialogs.ConfirmAsync(Strings.FolderDeleteTitle, Format(Strings.FolderDeleteMessage, folder.Name)))
        {
            return;
        }

        var parent = FolderPath.Parent(folder.Path);
        var definitions = await jobs.Enqueue("Load connections", context => store.GetAllAsync(context.CancellationToken), JobPriority.High).Completion;
        foreach (var definition in definitions.Where(d => FolderPath.IsSameOrUnder(FolderPath.Normalize(d.Folder), folder.Path)))
        {
            var rest = FolderPath.Normalize(definition.Folder)[folder.Path.Length..].TrimStart('/');
            await SaveFolderAsync(definition, rest.Length == 0 ? parent : FolderPath.Combine(parent, rest));
        }

        folders?.Move(folder.Path, parent);
        folders?.Remove(folder.Path);
        await LoadAsync();
    }

    /// <summary>Whether <paramref name="dragged"/> may be dropped on <paramref name="target"/> (null: the top level).</summary>
    public static bool CanMove(ExplorerNodeViewModel? dragged, ExplorerNodeViewModel? target)
    {
        var into = FolderOf(target);
        return dragged switch
        {
            ConnectionNodeViewModel connection => !string.Equals(FolderPath.Normalize(connection.Definition.Folder), into, StringComparison.OrdinalIgnoreCase),

            // A folder goes anywhere but into itself or below itself, and not where it already is.
            FolderNodeViewModel folder => !FolderPath.IsSameOrUnder(into, folder.Path) &&
                                          !string.Equals(FolderPath.Parent(folder.Path), into, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>The connections picked with Ctrl+click.</summary>
    public IReadOnlyList<ConnectionNodeViewModel> MarkedConnections() =>
        [.. AllNodes(Roots).OfType<ConnectionNodeViewModel>().Where(static c => c.IsMarked)];

    /// <summary>Forgets the Ctrl+click picks.</summary>
    public void ClearMarks()
    {
        foreach (var node in AllNodes(Roots).Where(static n => n.IsMarked))
        {
            node.IsMarked = false;
        }
    }

    /// <summary>Where a drop on <paramref name="target"/> lands, as the drag caption says it ("the top level" for null).</summary>
    public static string DropCaption(int count, ExplorerNodeViewModel? target)
    {
        var folder = FolderOf(target);
        var where = folder.Length == 0 ? Strings.FolderTopLevel : folder;
        return count > 1 ? Format2(Strings.MoveManyTo, count, where) : Format(Strings.MoveTo, where);
    }

    /// <summary>Moves several connections (picked with Ctrl+click) into one folder.</summary>
    public async Task MoveAsync(IReadOnlyList<ExplorerNodeViewModel> dragged, ExplorerNodeViewModel? target)
    {
        ArgumentNullException.ThrowIfNull(dragged);
        if (dragged.Count == 1)
        {
            await MoveAsync(dragged[0], target);
            return;
        }

        var into = FolderOf(target);
        foreach (var connection in dragged.OfType<ConnectionNodeViewModel>().Where(c => CanMove(c, target)))
        {
            await SaveFolderAsync(connection.Definition, into);
        }

        await LoadAsync();
    }

    private static IEnumerable<ExplorerNodeViewModel> AllNodes(IEnumerable<ExplorerNodeViewModel> nodes) =>
        nodes.SelectMany(static n => n is FolderNodeViewModel folder ? (IEnumerable<ExplorerNodeViewModel>)[folder, .. AllNodes(folder.Children)] : [n]);

    private static string Format2(string format, object? first, object? second) =>
        string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(format), first, second);

    /// <summary>Moves a connection or a folder onto a folder (or a connection's folder, or the top level).</summary>
    public async Task MoveAsync(ExplorerNodeViewModel dragged, ExplorerNodeViewModel? target)
    {
        ArgumentNullException.ThrowIfNull(dragged);
        if (!CanMove(dragged, target))
        {
            return;
        }

        var into = FolderOf(target);
        switch (dragged)
        {
            case ConnectionNodeViewModel connection:
                await SaveFolderAsync(connection.Definition, into);
                await LoadAsync();
                break;
            case FolderNodeViewModel folder:
                var moved = FolderPath.Combine(into, folder.Name);
                await MoveFolderAsync(folder.Path, moved);
                await LoadAsync(select: moved);
                break;
            default:
                break;
        }
    }

    private async Task MoveFolderAsync(string from, string to)
    {
        var definitions = await jobs.Enqueue("Load connections", context => store.GetAllAsync(context.CancellationToken), JobPriority.High).Completion;
        foreach (var definition in definitions.Where(d => FolderPath.IsSameOrUnder(FolderPath.Normalize(d.Folder), from)))
        {
            await SaveFolderAsync(definition, to + FolderPath.Normalize(definition.Folder)[from.Length..]);
        }

        folders?.Move(from, to);
        folders?.Add(to);
    }

    private Task SaveFolderAsync(ConnectionDefinition definition, string folder)
    {
        var moved = definition with { Folder = folder };
        return jobs.Enqueue("Save connection", context => store.SaveAsync(moved, context.CancellationToken), JobPriority.High).Completion;
    }

    private async Task<string?> AskFolderNameAsync(string title, string initial)
    {
        var name = await dialogs.PromptTextAsync(title, Strings.FolderNameMessage, initial);
        name = name?.Trim();
        return string.IsNullOrEmpty(name) || name.Contains('/', StringComparison.Ordinal) ? null : name;
    }

    private static string Format(string format, object? argument) =>
        string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(format), argument);

    /// <summary>Pins the selected object under its connection's favourites, or unpins it.</summary>
    [RelayCommand]
    private void ToggleFavorite(ExplorerNodeViewModel? node)
    {
        if ((node ?? Selected) is not DatabaseObjectNodeViewModel { CanBeFavorite: true } objectNode || favorites is null)
        {
            return;
        }

        var connectionId = objectNode.Session.Definition.Id;
        favorites.Toggle(connectionId, objectNode.Target);
        objectNode.RefreshFavorite();
        _ = RefreshFavoritesAsync(connectionId);
    }

    // The connection's Favourites group appears, changes or goes with its first or last object.
    private async Task RefreshFavoritesAsync(Guid connectionId)
    {
        if (FindConnection(Roots, connectionId) is not { IsConnected: true } connection || favorites is null)
        {
            return;
        }

        var group = connection.Children.OfType<FavoritesNodeViewModel>().FirstOrDefault();
        var any = favorites.Of(connectionId).Count > 0;
        if (group is not null && !any)
        {
            connection.Children.Remove(group);
        }
        else if (group is not null)
        {
            await group.ReloadAsync();
        }
        else if (any && connection.Context is { } context && connection.Children.FirstOrDefault() is not MessageNodeViewModel)
        {
            var added = new FavoritesNodeViewModel(context);
            connection.Children.Insert(0, added);
            await added.LoadAsync();
        }

        foreach (var pinned in Flatten(connection.Children).OfType<DatabaseObjectNodeViewModel>())
        {
            pinned.RefreshFavorite();
        }

        static IEnumerable<ExplorerNodeViewModel> Flatten(IEnumerable<ExplorerNodeViewModel> nodes) =>
            nodes.SelectMany(static n => (IEnumerable<ExplorerNodeViewModel>)[n, .. Flatten(n.Children)]);
    }

    private static ConnectionNodeViewModel? FindConnection(IEnumerable<ExplorerNodeViewModel> nodes, Guid id)
    {
        foreach (var node in nodes)
        {
            if (node is ConnectionNodeViewModel connection && connection.Definition.Id == id)
            {
                return connection;
            }

            if (node is FolderNodeViewModel folder && FindConnection(folder.Children, id) is { } inside)
            {
                return inside;
            }
        }

        return null;
    }

    private Dictionary<string, ProviderMark> _marks = new(StringComparer.Ordinal);

    public ObservableCollection<ExplorerNodeViewModel> Roots { get; } = [];

    /// <summary>The loaded providers: connections show the mark of their database type. Call on the UI thread.</summary>
    public void SetProviders(IEnumerable<IDatabaseProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _marks = providers.ToDictionary(static p => p.Id, ProviderMark.For, StringComparer.Ordinal);
        foreach (var node in Flatten(Roots).OfType<ConnectionNodeViewModel>())
        {
            node.Mark = MarkOf(node.Definition);
        }

        static IEnumerable<ExplorerNodeViewModel> Flatten(IEnumerable<ExplorerNodeViewModel> nodes) =>
            nodes.SelectMany(static n => n is FolderNodeViewModel folder ? Flatten(folder.Children) : [n]);
    }

    private ProviderMark MarkOf(ConnectionDefinition definition) => _marks.GetValueOrDefault(definition.ProviderId) ?? ProviderMark.Default;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeleteCommand), nameof(ToggleConnectionCommand), nameof(ReloadSelectedCommand))]
    [NotifyPropertyChangedFor(nameof(ToggleConnectionText))]
    public partial ExplorerNodeViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public string ToggleConnectionText => Selected is ConnectionNodeViewModel { IsConnected: true } ? Strings.ConnectionDisconnect : Strings.ConnectionConnect;

    /// <summary>
    /// Text that names must contain to be shown (ignoring case, by the user's culture). A node is shown when it
    /// matches or something loaded under it does; under a match everything is shown. Empty shows everything.
    /// </summary>
    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    /// <summary>Raised when a node should be selected and scrolled into view (Go to object); its parents are expanded already.</summary>
    public event EventHandler<ExplorerNodeViewModel>? RevealRequested;

    /// <summary>Selects <paramref name="node"/> and asks the view to show it.</summary>
    public void Reveal(ExplorerNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);

        // The folders it is in open, so that it can be seen.
        foreach (var folder in AllFolders(Roots).Where(f => Holds(f, node)))
        {
            folder.IsExpanded = true;
        }

        Selected = node;
        RevealRequested?.Invoke(this, node);
    }

    /// <summary>Raised when a new query tab is wanted on a connection and, if known, a database (or schema) of it.</summary>
    public event EventHandler<NewQueryRequest>? NewQueryRequested;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    /// <summary>Applies <see cref="FilterText"/> to the whole tree; also run whenever nodes load.</summary>
    public void ApplyFilter()
    {
        var filter = FilterText.Trim();
        foreach (var root in Roots)
        {
            Filter(root, filter, ancestorMatched: false);
        }
    }

    /// <summary>
    /// Expands every folder and every node whose children are already loaded; nothing new is read from a
    /// database (a closed connection stays closed, an unopened table stays as it is).
    /// </summary>
    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var root in Roots)
        {
            Expand(root);
        }

        static void Expand(ExplorerNodeViewModel node)
        {
            if (node is MessageNodeViewModel || (node is LazyNodeViewModel lazy && !lazy.IsLoaded) ||
                !node.Children.Any(static c => c is not MessageNodeViewModel))
            {
                return;
            }

            node.IsExpanded = true;
            foreach (var child in node.Children)
            {
                Expand(child);
            }
        }
    }

    /// <summary>Collapses every node, down to the deepest, so that the tree shows its top level only.</summary>
    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var root in Roots)
        {
            Collapse(root);
        }

        static void Collapse(ExplorerNodeViewModel node)
        {
            foreach (var child in node.Children)
            {
                Collapse(child);
            }

            node.IsExpanded = false;
        }
    }

    private readonly HashSet<LazyNodeViewModel> _watched = [];

    // Whether the tree has been built once (the first build starts with every folder closed).
    private bool _loaded;

    private static bool Holds(ExplorerNodeViewModel parent, ExplorerNodeViewModel node) =>
        parent.Children.Any(c => ReferenceEquals(c, node) || Holds(c, node));

    private bool Filter(ExplorerNodeViewModel node, string filter, bool ancestorMatched)
    {
        // Children loaded later are filtered when they arrive.
        if (node is LazyNodeViewModel lazy && _watched.Add(lazy))
        {
            lazy.ChildrenLoaded += (_, _) => ApplyFilter();
        }

        var matched = filter.Length == 0 || ancestorMatched ||
                      (node is not MessageNodeViewModel && CultureInfo.CurrentCulture.CompareInfo.IndexOf(node.Name, filter, CompareOptions.IgnoreCase) >= 0);
        var anyChild = false;
        foreach (var child in node.Children)
        {
            anyChild |= Filter(child, filter, matched);
        }

        node.IsVisible = matched || anyChild;
        return node.IsVisible;
    }

    private static bool CanOpenQuery(ExplorerNodeViewModel? node) => node switch
    {
        ConnectionNodeViewModel connection => connection.IsConnected,
        DatabaseObjectNodeViewModel or CategoryNodeViewModel => true,
        _ => false,
    };

    /// <summary>"New query" on a node: a connected connection, or a database, schema or object under one.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenQuery))]
    private void NewQuery(ExplorerNodeViewModel? node)
    {
        var request = node switch
        {
            ConnectionNodeViewModel { IsConnected: true } connection => new NewQueryRequest(connection.Definition.Id, null),
            DatabaseObjectNodeViewModel item => new NewQueryRequest(item.Session.Definition.Id, DatabaseOf(item.Session, item.Target)),
            CategoryNodeViewModel { Parent: { } parent } category => new NewQueryRequest(category.Session.Definition.Id, DatabaseOf(category.Session, parent)),
            CategoryNodeViewModel category => new NewQueryRequest(category.Session.Definition.Id, null),
            _ => null,
        };
        if (request is not null)
        {
            NewQueryRequested?.Invoke(this, request);
        }
    }

    /// <summary>Opens a tab with the first rows of a table or view and runs it (SSMS's "Select Top 1000 Rows").</summary>
    [RelayCommand]
    private Task SelectRowsAsync(ExplorerNodeViewModel? node) => ScriptAsync(node, ObjectScriptKind.SelectRows);

    /// <summary>Opens a tab with a SELECT of every column.</summary>
    [RelayCommand]
    private Task ScriptSelectAsync(ExplorerNodeViewModel? node) => ScriptAsync(node, ObjectScriptKind.Select);

    /// <summary>Opens a tab with an INSERT template.</summary>
    [RelayCommand]
    private Task ScriptInsertAsync(ExplorerNodeViewModel? node) => ScriptAsync(node, ObjectScriptKind.Insert);

    /// <summary>Opens a tab with an UPDATE template by primary key.</summary>
    [RelayCommand]
    private Task ScriptUpdateAsync(ExplorerNodeViewModel? node) => ScriptAsync(node, ObjectScriptKind.Update);

    /// <summary>Opens a tab with a DELETE template by primary key.</summary>
    [RelayCommand]
    private Task ScriptDeleteAsync(ExplorerNodeViewModel? node) => ScriptAsync(node, ObjectScriptKind.Delete);

    /// <summary>Opens a tab with the object's definition.</summary>
    [RelayCommand]
    private Task ScriptCreateAsync(ExplorerNodeViewModel? node) => ScriptAsync(node, ObjectScriptKind.Create);

    /// <summary>Opens a tab with the definition in its ALTER / CREATE OR REPLACE form.</summary>
    [RelayCommand]
    private Task ScriptAlterAsync(ExplorerNodeViewModel? node) => ScriptAsync(node, ObjectScriptKind.Alter);

    /// <summary>Opens a tab with a DROP of the object (not run).</summary>
    [RelayCommand]
    private Task ScriptDropAsync(ExplorerNodeViewModel? node) => ScriptAsync(node, ObjectScriptKind.Drop);

    /// <summary>The Import data dialog of a table: a CSV file into its rows.</summary>
    [RelayCommand]
    private async Task ImportDataAsync(ExplorerNodeViewModel? node)
    {
        if ((node ?? Selected) is not DatabaseObjectNodeViewModel { Target: var target, Session: var session } || !ImportDataViewModel.CanImport(session, target))
        {
            return;
        }

        var import = new ImportDataViewModel(session, target, metadata, jobs, dialogs);
        _ = import.LoadAsync();
        await dialogs.ImportDataAsync(import);
    }

    /// <summary>The Execute dialog of a procedure or function; its script opens (or runs) in a query tab.</summary>
    [RelayCommand]
    private Task ExecuteRoutineAsync(ExplorerNodeViewModel? node) =>
        (node ?? Selected) is DatabaseObjectNodeViewModel { Target: var target, Session: var session } ? ExecuteRoutineAsync(session, target) : Task.CompletedTask;

    /// <summary>The Execute dialog of a procedure or function of an open connection (also from the details view).</summary>
    public async Task ExecuteRoutineAsync(ConnectionSession session, DatabaseObject target)
    {
        if (!ExecuteRoutineViewModel.CanExecute(session, target))
        {
            return;
        }

        var routine = new ExecuteRoutineViewModel(session, target, metadata, jobs);
        _ = routine.LoadAsync();
        var choice = await dialogs.ExecuteRoutineAsync(routine);
        if (choice != RoutineExecutionChoice.Cancel)
        {
            NewQueryRequested?.Invoke(this, routine.Request(choice == RoutineExecutionChoice.Run));
        }
    }

    /// <summary>Asks for the details view of an object (columns, keys, indexes, DDL…).</summary>
    [RelayCommand]
    private void OpenDetails(ExplorerNodeViewModel? node)
    {
        if ((node ?? Selected) is DatabaseObjectNodeViewModel { Target: var target, Session: var session } && ObjectScripts.HasDefinition(target.Kind))
        {
            DetailsRequested?.Invoke(this, new ObjectDetailsRequest(session.Definition.Id, target));
        }
    }

    /// <summary>Raised when an object's details view should open.</summary>
    public event EventHandler<ObjectDetailsRequest>? DetailsRequested;

    /// <summary>A key of a key-value connection is to be shown in a tab.</summary>
    public event EventHandler<KeyValueViewModel>? KeyRequested;

    /// <summary>The performance analysis of an open connection (and, from a database node, that database) is to be shown in a tab.</summary>
    public event EventHandler<PerformanceViewModel>? PerformanceRequested;

    /// <summary>Opens the performance analysis of a connection, or of a database under one.</summary>
    [RelayCommand]
    private void OpenPerformance(ExplorerNodeViewModel? node)
    {
        switch (node ?? Selected)
        {
            case ConnectionNodeViewModel { CanAnalyze: true, Context.Session: var session }:
                PerformanceRequested?.Invoke(this, new PerformanceViewModel(session, jobs));
                break;
            case DatabaseObjectNodeViewModel { CanAnalyze: true, Session: var session, Target: var database }:
                PerformanceRequested?.Invoke(this, new PerformanceViewModel(session, jobs, database.Name.Name));
                break;
            default:
                break;
        }
    }

    /// <summary>A database's detail screen is to be shown in a tab.</summary>
    public event EventHandler<DatabaseDetailViewModel>? DatabaseDetailRequested;

    /// <summary>Opens the detail screen of the chosen database (its menu item shows only on a database node that can).</summary>
    [RelayCommand]
    private void OpenDatabaseDetail(ExplorerNodeViewModel? node)
    {
        if ((node ?? Selected) is DatabaseObjectNodeViewModel { CanDetail: true, Session: var session, Target: var database })
        {
            RequestDatabaseDetail(session, database.Name.Name);
        }
    }

    /// <summary>Asks for the detail screen of <paramref name="database"/> on <paramref name="session"/> (DBA menu, query tab).</summary>
    public void RequestDatabaseDetail(ConnectionSession session, string database)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.SupportsDatabaseDetails && !string.IsNullOrEmpty(database))
        {
            DatabaseDetailRequested?.Invoke(this, new DatabaseDetailViewModel(session, jobs, database));
        }
    }

    /// <summary>The health check of an open connection is to be shown in a tab.</summary>
    public event EventHandler<HealthCheckViewModel>? HealthRequested;

    /// <summary>Opens the health check of the chosen open connection (its menu item shows only then).</summary>
    [RelayCommand]
    private void OpenHealth(ExplorerNodeViewModel? node)
    {
        if ((node ?? Selected) is ConnectionNodeViewModel { CanHealthCheck: true, Context.Session: var session })
        {
            HealthRequested?.Invoke(this, new HealthCheckViewModel(session, jobs));
        }
    }

    /// <summary>The server dashboard of an open connection is to be shown in a tab.</summary>
    public event EventHandler<DashboardViewModel>? DashboardRequested;

    /// <summary>Opens the server dashboard of the chosen open connection (its menu item shows only then).</summary>
    [RelayCommand]
    private void OpenDashboard(ExplorerNodeViewModel? node)
    {
        if ((node ?? Selected) is ConnectionNodeViewModel { CanDashboard: true, Context.Session: var session })
        {
            DashboardRequested?.Invoke(this, new DashboardViewModel(session, jobs));
        }
    }

    /// <summary>The activity monitor of an open connection is to be shown in a tab.</summary>
    public event EventHandler<ActivityMonitorViewModel>? ActivityRequested;

    /// <summary>Opens the activity monitor (sessions, blocking) of the chosen open connection (its menu item shows only then).</summary>
    [RelayCommand]
    private void OpenActivity(ExplorerNodeViewModel? node)
    {
        if ((node ?? Selected) is ConnectionNodeViewModel { CanMonitor: true, Context.Session: var session })
        {
            ActivityRequested?.Invoke(this, new ActivityMonitorViewModel(session, jobs, dialogs));
        }
    }

    /// <summary>Opens a key in a tab (double-click or Enter on it).</summary>
    [RelayCommand]
    private void OpenKey(ExplorerNodeViewModel? node)
    {
        if ((node ?? Selected) is KeyNodeViewModel key)
        {
            KeyRequested?.Invoke(this, new KeyValueViewModel(key.Session, key.Database, key.Key, jobs, dialogs));
        }
    }

    /// <summary>Lists only the keys of a database matching a pattern (<c>user:*</c>); an empty one lists all again.</summary>
    [RelayCommand]
    private async Task FilterKeysAsync(ExplorerNodeViewModel? node)
    {
        if ((node ?? Selected) is KeySpaceNodeViewModel space &&
            await dialogs.PromptTextAsync(Strings.KeyFilter, Strings.KeyFilterPrompt, space.Pattern == "*" ? string.Empty : space.Pattern) is { } pattern)
        {
            await space.FilterAsync(pattern);
        }
    }

    /// <summary>The last problem writing a script (shown at the top of the explorer); null when none.</summary>
    [ObservableProperty]
    public partial string? ScriptError { get; set; }

    /// <summary>Hides the script problem.</summary>
    [RelayCommand]
    private void DismissScriptError() => ScriptError = null;

    private async Task ScriptAsync(ExplorerNodeViewModel? node, ObjectScriptKind kind)
    {
        node ??= Selected;
        if (node is not DatabaseObjectNodeViewModel { Target: var target, Session: var session })
        {
            return;
        }

        ScriptError = null;
        string? text;
        try
        {
            text = await jobs.Enqueue("Write script", context => ObjectScripts.BuildAsync(session, metadata, target, kind, context.CancellationToken), JobPriority.High)
                .Completion;
        }
        catch (OperationCanceledException)
        {
            return;
        }
#pragma warning disable CA1031 // The server's reason is shown; nothing else is disturbed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ScriptError = ex.Message;
            return;
        }

        if (text is null)
        {
            ScriptError = string.Format(System.Globalization.CultureInfo.CurrentCulture, System.Text.CompositeFormat.Parse(Strings.DefinitionUnavailable), target.Name);
            return;
        }

        NewQueryRequested?.Invoke(this, new NewQueryRequest(session.Definition.Id, DatabaseOf(session, target))
        {
            Text = text,
            Title = target.Name.Name,
            Run = kind == ObjectScriptKind.SelectRows,
        });
    }

    /// <summary>The database a query on <paramref name="target"/> should start in: its database, or for Oracle its schema.</summary>
    internal static string? DatabaseOf(ConnectionSession session, DatabaseObject target)
    {
        if (session.Provider.Capabilities.HasFlag(ProviderCapabilities.MultipleDatabases))
        {
            return target.Kind == DatabaseObjectKinds.Database ? target.Name.Name : target.Name.Database;
        }

        // A provider whose query windows switch schemas (Oracle): the schema of the node.
        return session.Connection is IDatabaseContext { Kind: DatabaseContextKind.Schema }
            ? target.Kind == DatabaseObjectKinds.Schema ? target.Name.Name : target.Name.Schema
            : null;
    }

    /// <summary>Reads the saved connections and rebuilds the tree, keeping live state where the connection still exists.</summary>
    public Task LoadAsync() => LoadAsync(select: null);

    /// <param name="select">A folder path to select afterwards (a new, renamed or moved folder).</param>
    private async Task LoadAsync(string? select)
    {
        var job = jobs.Enqueue("Load connections", context => store.GetAllAsync(context.CancellationToken), JobPriority.High);
        var definitions = await job.Completion;
        if (folders is not null)
        {
            await folders.LoadAsync();
        }

        var open = manager.Sessions.ToDictionary(static s => s.Definition.Id);
        var selectedId = (Selected as ConnectionNodeViewModel)?.Definition.Id;
        var selectedFolder = select ?? (Selected as FolderNodeViewModel)?.Path;

        // At start-up every folder is closed; later, collapsed folders stay collapsed through a rebuild and new
        // ones (just made or moved to) open.
        var firstLoad = !_loaded;
        _loaded = true;
        var collapsed = AllFolders(Roots).Where(static f => !f.IsExpanded).Select(static f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Roots.Clear();
        ExplorerNodeViewModel? reselect = null;
        ObservableCollection<ExplorerNodeViewModel> FolderChildren(string path)
        {
            var siblings = Roots;
            var current = string.Empty;
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                current = FolderPath.Combine(current, part);
                var folder = siblings.OfType<FolderNodeViewModel>().FirstOrDefault(f => string.Equals(f.Name, part, StringComparison.OrdinalIgnoreCase));
                if (folder is null)
                {
                    folder = new FolderNodeViewModel(part, current) { IsExpanded = !firstLoad && !collapsed.Contains(current) };

                    // Folders stay above connections at every level, in name order.
                    var folderCount = siblings.Count(static n => n is FolderNodeViewModel);
                    var index = siblings.Take(folderCount).Count(f => StringComparer.CurrentCultureIgnoreCase.Compare(f.Name, part) < 0);
                    siblings.Insert(index, folder);
                }

                if (string.Equals(current, selectedFolder, StringComparison.OrdinalIgnoreCase))
                {
                    reselect = folder;
                }

                siblings = folder.Children;
            }

            return siblings;
        }

        foreach (var path in folders?.Paths ?? [])
        {
            FolderChildren(path);
        }

        foreach (var definition in definitions)
        {
            var siblings = FolderChildren(definition.Folder);

            var node = new ConnectionNodeViewModel(definition) { Mark = MarkOf(definition) };
            siblings.Add(node);
            if (open.TryGetValue(definition.Id, out var session))
            {
                node.Attach(new ExplorerContext(session, metadata, jobs, favorites));
            }

            if (definition.Id == selectedId)
            {
                reselect = node;
            }
        }

        Selected = reselect;
        IsEmpty = definitions.Count == 0 && Roots.Count == 0;
        ApplyFilter();
        if (select is not null && reselect is not null)
        {
            RevealRequested?.Invoke(this, reselect);
        }
    }

    private static IEnumerable<FolderNodeViewModel> AllFolders(IEnumerable<ExplorerNodeViewModel> nodes) =>
        nodes.OfType<FolderNodeViewModel>().SelectMany(static f => (IEnumerable<FolderNodeViewModel>)[f, .. AllFolders(f.Children)]);

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    /// <summary>Import connections from DBeaver and SSMS; the list is read again after an import.</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        if (await dialogs.ImportConnectionsAsync())
        {
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        if (await dialogs.EditConnectionAsync(null))
        {
            await LoadAsync();
        }
    }

    private bool HasConnectionSelected() => Selected is ConnectionNodeViewModel { IsBusy: false };

    /// <summary>Delete key: the selected connection, or the selected folder (each asks first).</summary>
    [RelayCommand]
    private Task DeleteSelectedAsync() => Selected switch
    {
        FolderNodeViewModel folder => DeleteFolderAsync(folder),
        ConnectionNodeViewModel when DeleteCommand.CanExecute(null) => DeleteAsync(),
        _ => Task.CompletedTask,
    };

    /// <summary>F2: edits the selected connection, or renames the selected folder.</summary>
    [RelayCommand]
    private Task EditSelectedAsync() => Selected switch
    {
        FolderNodeViewModel folder => RenameFolderAsync(folder),
        ConnectionNodeViewModel when EditCommand.CanExecute(null) => EditCommand.ExecuteAsync(null),
        _ => Task.CompletedTask,
    };

    [RelayCommand(CanExecute = nameof(HasConnectionSelected))]
    private async Task EditAsync()
    {
        if (Selected is ConnectionNodeViewModel node && await dialogs.EditConnectionAsync(node.Definition))
        {
            await LoadAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasConnectionSelected))]
    private async Task DeleteAsync()
    {
        if (Selected is not ConnectionNodeViewModel node)
        {
            return;
        }

        var message = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.DeleteConfirmMessage), node.Name);
        if (!await dialogs.ConfirmAsync(Strings.DeleteConfirmTitle, message))
        {
            return;
        }

        var id = node.Definition.Id;
        var job = jobs.Enqueue(
            "Delete connection",
            async context =>
            {
                await manager.CloseAsync(id).ConfigureAwait(false);
                await store.DeleteAsync(id, context.CancellationToken).ConfigureAwait(false);
                await credentials.DeletePasswordAsync(id, context.CancellationToken).ConfigureAwait(false);
                await credentials.DeletePasswordAsync(SshTunnel.SecretId(id), context.CancellationToken).ConfigureAwait(false);
            },
            JobPriority.High);
        await job.Completion;
        await LoadAsync();
    }

    /// <summary>Connects the selected connection, or disconnects it when it is connected.</summary>
    [RelayCommand(CanExecute = nameof(HasConnectionSelected))]
    private async Task ToggleConnectionAsync()
    {
        if (Selected is not ConnectionNodeViewModel node)
        {
            return;
        }

        node.IsBusy = true;
        node.Error = string.Empty;
        RefreshCommands();
        try
        {
            if (node.IsConnected)
            {
                await jobs.Enqueue("Disconnect", _ => manager.CloseAsync(node.Definition.Id), JobPriority.High).Completion;
                node.Detach();
                return;
            }

            var attempt = await OpenAsync(node.Definition, null);
            if (attempt.Outcome == ConnectionOutcome.PasswordRequired)
            {
                // Asked for each time, or the stored entry is gone: prompt, and use the answer for this session only.
                var password = await dialogs.PromptPasswordAsync(node.Name);
                if (password is null)
                {
                    return;
                }

                attempt = await OpenAsync(node.Definition, password);
            }

            if (attempt.Outcome == ConnectionOutcome.SshPasswordRequired)
            {
                // The SSH password is not stored: asked now, used for this session only.
                var sshPassword = await dialogs.PromptPasswordAsync(string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.SshPasswordPrompt), node.Name));
                if (sshPassword is null)
                {
                    return;
                }

                attempt = await OpenAsync(node.Definition, null, sshPassword);
                if (attempt.Outcome == ConnectionOutcome.PasswordRequired && await dialogs.PromptPasswordAsync(node.Name) is { } password)
                {
                    attempt = await OpenAsync(node.Definition, password, sshPassword);
                }
            }

            if (attempt is { Outcome: ConnectionOutcome.Succeeded, Session: { } session })
            {
                node.Attach(new ExplorerContext(session, metadata, jobs, favorites));
            }
            else
            {
                node.Error = ConnectionEditorViewModel.Describe(attempt);
            }
        }
        catch (OperationCanceledException)
        {
            node.Error = Strings.OutcomeCanceled;
        }
        finally
        {
            node.IsBusy = false;
            RefreshCommands();
        }
    }

    private bool CanReloadSelected() => Selected is LazyNodeViewModel { CanLoad: true, IsLoading: false };

    /// <summary>Reloads the selected node's children from the database, bypassing the cache.</summary>
    [RelayCommand(CanExecute = nameof(CanReloadSelected))]
    private async Task ReloadSelectedAsync()
    {
        if (Selected is LazyNodeViewModel node)
        {
            await node.LoadAsync(refresh: true);
            node.IsExpanded = true;
        }
    }

    private Task<ConnectionAttempt> OpenAsync(ConnectionDefinition definition, string? password, string? sshSecret = null) =>
        jobs.Enqueue("Connect", context => manager.OpenAsync(definition, password, sshSecret, context.CancellationToken), JobPriority.High).Completion;

    private void RefreshCommands()
    {
        EditCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ToggleConnectionCommand.NotifyCanExecuteChanged();
        ReloadSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ToggleConnectionText));
    }
}
