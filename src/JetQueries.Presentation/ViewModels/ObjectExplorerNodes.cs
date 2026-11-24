using CommunityToolkit.Mvvm.ComponentModel;
using JetQueries.Core.Connections;
using JetQueries.Core.Jobs;
using JetQueries.Core.Metadata;
using JetQueries.Presentation.Resources;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>What the nodes under an open connection need to load their children.</summary>
/// <param name="Favorites">The pinned objects; none without.</param>
public sealed record ExplorerContext(ConnectionSession Session, IMetadataService Metadata, IJobQueue Jobs, FavoriteObjects? Favorites = null);

/// <summary>A row that only says something: "Loading…", "(empty)" or why loading failed.</summary>
public sealed class MessageNodeViewModel(string text, bool isError = false) : ExplorerNodeViewModel(text)
{
    public bool IsError { get; } = isError;
}

/// <summary>A column of a table or view.</summary>
public sealed class ColumnNodeViewModel(ColumnInfo column) : ExplorerNodeViewModel(column.Name)
{
    public string DataType { get; } = column.DataType;

    public bool IsPrimaryKey { get; } = column.IsPrimaryKey;

    public bool IsNullable { get; } = column.IsNullable;
}

/// <summary>
/// A node whose children come from the database the first time it is expanded. Until then it holds a single
/// "Loading…" row so that the tree shows an expander. Loading runs as a background job; a failure becomes a
/// row in the tree instead of an exception.
/// </summary>
public abstract partial class LazyNodeViewModel : ExplorerNodeViewModel
{
    private bool _loaded;

    protected LazyNodeViewModel(string name, bool expandable)
        : base(name)
    {
        IsExpanded = false;
        if (expandable)
        {
            Children.Add(new MessageNodeViewModel(Strings.ExplorerLoading));
        }
    }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>How many objects were loaded underneath (shown after a group's name); null before loading.</summary>
    [ObservableProperty]
    public partial int? LoadedCount { get; set; }

    /// <summary>Raised after the children were loaded or reloaded.</summary>
    public event EventHandler? ChildrenLoaded;

    /// <summary>Whether there is anything to load right now.</summary>
    public virtual bool CanLoad => true;

    /// <summary>Loads the children, or reloads them from the database when <paramref name="refresh"/> is set.</summary>
    public async Task LoadAsync(bool refresh = false)
    {
        if (IsLoading || !CanLoad)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var children = await LoadChildrenAsync(refresh);
            Children.Clear();
            foreach (var child in children)
            {
                Children.Add(child);
            }

            if (Children.Count == 0)
            {
                Children.Add(new MessageNodeViewModel(Strings.ExplorerEmpty));
            }

            _loaded = true;
            LoadedCount = Children.Count(static c => c is not MessageNodeViewModel);
            ChildrenLoaded?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // Disconnected or shut down while loading: the next expand tries again.
        }
#pragma warning disable CA1031 // Whatever the driver throws belongs in the tree, next to what failed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Children.Clear();
            Children.Add(new MessageNodeViewModel(ex.Message, isError: true));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Forgets the children, so that the next expand loads again.</summary>
    protected void Reset(bool expandable)
    {
        _loaded = false;
        Children.Clear();
        if (expandable)
        {
            Children.Add(new MessageNodeViewModel(Strings.ExplorerLoading));
        }
    }

    protected abstract Task<IReadOnlyList<ExplorerNodeViewModel>> LoadChildrenAsync(bool refresh);

    protected override void OnExpanded(bool expanded)
    {
        if (expanded && !_loaded)
        {
            // Fire and forget: LoadAsync reports every failure inside the tree.
            _ = LoadAsync();
        }
    }
}

/// <summary>A database, schema, table, view, routine or other catalog object.</summary>
public sealed class DatabaseObjectNodeViewModel(ExplorerContext context, DatabaseObject databaseObject)
    : LazyNodeViewModel(databaseObject.Name.Name, ExplorerTree.HasChildren(databaseObject.Kind))
{
    public DatabaseObject Target { get; } = databaseObject;

    /// <inheritdoc />
    public override bool HasData => ObjectScripts.HasData(Target.Kind);

    /// <inheritdoc />
    public override bool HasDefinition => ObjectScripts.HasDefinition(Target.Kind);

    /// <inheritdoc />
    public override bool CanAlter => ObjectScripts.CanAlter(Target.Kind);

    /// <summary>Whether the object can be pinned (anything with a definition: tables, views, routines…).</summary>
    public override bool CanBeFavorite => HasDefinition && context.Favorites is not null;

    /// <summary>Whether the object is pinned under the connection's favourites.</summary>
    public override bool IsFavorite => context.Favorites?.Contains(context.Session.Definition.Id, Target) == true;

    /// <summary>Tells the view that <see cref="IsFavorite"/> changed.</summary>
    public void RefreshFavorite() => OnPropertyChanged(nameof(IsFavorite));

    /// <summary>The open connection the object belongs to.</summary>
    public ConnectionSession Session => context.Session;

    public bool IsContainer => Target.Kind is DatabaseObjectKinds.Database or DatabaseObjectKinds.Schema;

    /// <summary>A table the provider can fill from a file (the Import data dialog).</summary>
    public bool CanImportData => ImportDataViewModel.CanImport(context.Session, Target);

    /// <summary>A procedure or function the provider can write a call for (the Execute dialog).</summary>
    public bool CanExecuteRoutine => ExecuteRoutineViewModel.CanExecute(context.Session, Target);

    protected override Task<IReadOnlyList<ExplorerNodeViewModel>> LoadChildrenAsync(bool refresh) => Target.Kind switch
    {
        DatabaseObjectKinds.Database when context.Session.Provider.Capabilities.HasFlag(ProviderCapabilities.Schemas) =>
            ExplorerTree.LoadObjectsAsync(context, Target, DatabaseObjectKinds.Schema, refresh),
        DatabaseObjectKinds.Database or DatabaseObjectKinds.Schema =>
            Task.FromResult(ExplorerTree.Categories(context, Target)),
        DatabaseObjectKinds.Table or DatabaseObjectKinds.View => ExplorerTree.LoadColumnsAsync(context, Target.Name, refresh, Target.Name.Database),
        _ => Task.FromResult<IReadOnlyList<ExplorerNodeViewModel>>([]),
    };
}

/// <summary>The objects pinned on a connection, at the top of it (read from the settings, not the catalog).</summary>
public sealed class FavoritesNodeViewModel(ExplorerContext context) : LazyNodeViewModel(Strings.ExplorerFavorites, expandable: true)
{
    /// <summary>Reads the pinned objects again (after one was added or removed).</summary>
    public Task ReloadAsync() => LoadAsync(refresh: true);

    protected override Task<IReadOnlyList<ExplorerNodeViewModel>> LoadChildrenAsync(bool refresh) =>
        Task.FromResult<IReadOnlyList<ExplorerNodeViewModel>>(
            [.. (context.Favorites?.Of(context.Session.Definition.Id) ?? []).Select(o => new DatabaseObjectNodeViewModel(context, o))]);
}

/// <summary>A group such as "Tables" or "Views" under a schema or database.</summary>
public sealed class CategoryNodeViewModel(ExplorerContext context, string title, DatabaseObject? parent, DatabaseObjectKinds kind)
    : LazyNodeViewModel(title, expandable: true)
{
    public DatabaseObjectKinds Kind { get; } = kind;

    /// <summary>The database or schema the group is under; null at the top of a connection.</summary>
    public DatabaseObject? Parent => parent;

    /// <summary>The open connection the group belongs to.</summary>
    public ConnectionSession Session => context.Session;

    protected override Task<IReadOnlyList<ExplorerNodeViewModel>> LoadChildrenAsync(bool refresh) =>
        ExplorerTree.LoadObjectsAsync(context, parent, Kind, refresh);
}

/// <summary>How the object tree is shaped. Nothing here knows a database type: it follows the provider's capabilities.</summary>
internal static class ExplorerTree
{
    public static bool HasChildren(DatabaseObjectKinds kind) =>
        kind is DatabaseObjectKinds.Database or DatabaseObjectKinds.Schema or DatabaseObjectKinds.Table or DatabaseObjectKinds.View;

    /// <summary>The first level under a connection: databases, schemas, or the object groups directly.</summary>
    public static async Task<IReadOnlyList<ExplorerNodeViewModel>> LoadRootAsync(ExplorerContext context, bool refresh)
    {
        var nodes = await LoadRootObjectsAsync(context, refresh).ConfigureAwait(true);
        if (context.Favorites is { } favorites)
        {
            await favorites.LoadAsync().ConfigureAwait(true);
            if (favorites.Of(context.Session.Definition.Id).Count > 0)
            {
                return [new FavoritesNodeViewModel(context), .. nodes];
            }
        }

        return nodes;
    }

    private static Task<IReadOnlyList<ExplorerNodeViewModel>> LoadRootObjectsAsync(ExplorerContext context, bool refresh)
    {
        var capabilities = context.Session.Provider.Capabilities;
        if (capabilities.HasFlag(ProviderCapabilities.MultipleDatabases))
        {
            return LoadObjectsAsync(context, null, DatabaseObjectKinds.Database, refresh);
        }

        return capabilities.HasFlag(ProviderCapabilities.Schemas)
            ? LoadObjectsAsync(context, null, DatabaseObjectKinds.Schema, refresh)
            : Task.FromResult(Categories(context, null));
    }

    public static IReadOnlyList<ExplorerNodeViewModel> Categories(ExplorerContext context, DatabaseObject? parent)
    {
        var capabilities = context.Session.Provider.Capabilities;
        var categories = new List<ExplorerNodeViewModel>
        {
            new CategoryNodeViewModel(context, Strings.CategoryTables, parent, DatabaseObjectKinds.Table),
            new CategoryNodeViewModel(context, Strings.CategoryViews, parent, DatabaseObjectKinds.View),
        };
        if (capabilities.HasFlag(ProviderCapabilities.Routines))
        {
            categories.Add(new CategoryNodeViewModel(context, Strings.CategoryProcedures, parent, DatabaseObjectKinds.Procedure));
            categories.Add(new CategoryNodeViewModel(context, Strings.CategoryFunctions, parent, DatabaseObjectKinds.Function));
        }

        if (capabilities.HasFlag(ProviderCapabilities.Packages))
        {
            categories.Add(new CategoryNodeViewModel(context, Strings.CategoryPackages, parent, DatabaseObjectKinds.Package));
        }

        if (capabilities.HasFlag(ProviderCapabilities.Sequences))
        {
            categories.Add(new CategoryNodeViewModel(context, Strings.CategorySequences, parent, DatabaseObjectKinds.Sequence));
        }

        if (capabilities.HasFlag(ProviderCapabilities.Synonyms))
        {
            categories.Add(new CategoryNodeViewModel(context, Strings.CategorySynonyms, parent, DatabaseObjectKinds.Synonym));
        }

        if (capabilities.HasFlag(ProviderCapabilities.UserTypes))
        {
            categories.Add(new CategoryNodeViewModel(context, Strings.CategoryTypes, parent, DatabaseObjectKinds.Type));
        }

        return categories;
    }

    public static async Task<IReadOnlyList<ExplorerNodeViewModel>> LoadObjectsAsync(
        ExplorerContext context, DatabaseObject? parent, DatabaseObjectKinds kinds, bool refresh)
    {
        // A provider whose metadata sees one database (PostgreSQL) reads under a database node on a connection to
        // it; what is found there carries the database, so details, scripts and query tabs go there too.
        var database = parent is { Kind: DatabaseObjectKinds.Database } ? parent.Name.Name : parent?.Name.Database;
        var perDatabase = context.Session.HasMetadataPerDatabase && database is not null;
        var job = context.Jobs.Enqueue(
            "Load objects",
            job => context.Metadata.GetObjectsAsync(context.Session, parent, kinds, refresh, job.CancellationToken, perDatabase ? database : null));
        var objects = await job.Completion;
        return [.. objects.Select(o => new DatabaseObjectNodeViewModel(
            context,
            perDatabase && o.Kind != DatabaseObjectKinds.Database && o.Name.Database is null ? o with { Name = o.Name with { Database = database } } : o))];
    }

    public static async Task<IReadOnlyList<ExplorerNodeViewModel>> LoadColumnsAsync(ExplorerContext context, DatabaseObjectName table, bool refresh, string? database = null)
    {
        var perDatabase = context.Session.HasMetadataPerDatabase && database is not null;
        var asked = perDatabase ? table with { Database = null } : table;
        var job = context.Jobs.Enqueue(
            "Load columns",
            job => context.Metadata.GetColumnsAsync(context.Session, asked, refresh, job.CancellationToken, perDatabase ? database : null));
        var columns = await job.Completion;
        return [.. columns.Select(static c => new ColumnNodeViewModel(c))];
    }
}
