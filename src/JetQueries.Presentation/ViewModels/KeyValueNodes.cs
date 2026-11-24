using System.Globalization;
using JetQueries.Core.Connections;
using JetQueries.Presentation.Resources;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>
/// The explorer of a key-value connection (Redis): logical databases, then keys grouped by their separator
/// (<c>user:1:name</c> under <c>user</c> › <c>1</c>), as RedisInsight shows them. Keys are listed with SCAN up to
/// <see cref="MaxKeys"/>; a pattern narrows a database (<c>user:*</c>).
/// </summary>
internal static class KeyValueTree
{
    /// <summary>The most keys a database node lists.</summary>
    public static int MaxKeys { get; set; } = 5000;

    /// <summary>The databases of the connection; null when its metadata is not a key-value store.</summary>
    public static async Task<IReadOnlyList<ExplorerNodeViewModel>?> LoadRootAsync(ExplorerContext context)
    {
        var spaces = await context.Jobs.Enqueue(
            "Load key spaces",
            job => context.Session.UseMetadataAsync(
                (metadata, token) => metadata is IKeyValueStore store ? store.GetKeySpacesAsync(token) : Task.FromResult<IReadOnlyList<KeySpace>>(null!),
                job.CancellationToken)).Completion;
        return spaces?.Select(s => (ExplorerNodeViewModel)new KeySpaceNodeViewModel(context, s)).ToList();
    }

    /// <summary>Nodes for keys, grouped by <paramref name="separator"/> below <paramref name="prefix"/>.</summary>
    public static List<ExplorerNodeViewModel> Group(ExplorerContext context, int database, IReadOnlyList<string> keys, string prefix, char separator)
    {
        var nodes = new List<ExplorerNodeViewModel>();
        var folders = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var leaves = new List<string>();
        foreach (var key in keys)
        {
            var rest = key[prefix.Length..];
            var at = rest.IndexOf(separator, StringComparison.Ordinal);
            if (at > 0 && at < rest.Length - 1)
            {
                var name = rest[..at];
                if (!folders.TryGetValue(name, out var inside))
                {
                    folders[name] = inside = [];
                }

                inside.Add(key);
            }
            else
            {
                leaves.Add(key);
            }
        }

        foreach (var (name, inside) in folders)
        {
            var folderPrefix = prefix + name + separator;
            nodes.Add(new KeyFolderNodeViewModel(name, inside.Count, () => Group(context, database, inside, folderPrefix, separator)));
        }

        nodes.AddRange(leaves.Order(StringComparer.Ordinal).Select(key => new KeyNodeViewModel(context.Session, database, key, key[prefix.Length..])));
        return nodes;
    }
}

/// <summary>A logical database of a key-value connection: "db0 (1 234)".</summary>
public sealed class KeySpaceNodeViewModel : LazyNodeViewModel
{
    private readonly ExplorerContext _context;

    public KeySpaceNodeViewModel(ExplorerContext context, KeySpace space)
        : base(string.Create(CultureInfo.CurrentCulture, $"db{space.Database} ({space.KeyCount:N0})"), expandable: true)
    {
        _context = context;
        Database = space.Database;
        KeyCount = space.KeyCount;
    }

    public int Database { get; }

    public long KeyCount { get; }

    /// <summary>The open connection.</summary>
    public ConnectionSession Session => _context.Session;

    /// <summary>The keys listed: <c>*</c> for all, or a pattern such as <c>user:*</c>.</summary>
    public string Pattern { get; private set; } = "*";

    /// <summary>Lists the keys matching <paramref name="pattern"/> instead.</summary>
    public Task FilterAsync(string pattern)
    {
        Pattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
        Reset(expandable: true);
        return LoadThenExpandAsync();
    }

    // Loaded first: expanding an unloaded node would start a load of its own.
    private async Task LoadThenExpandAsync()
    {
        await LoadAsync(refresh: true);
        IsExpanded = true;
    }

    protected override async Task<IReadOnlyList<ExplorerNodeViewModel>> LoadChildrenAsync(bool refresh)
    {
        var database = Database;
        var pattern = Pattern;
        var max = KeyValueTree.MaxKeys;
        var (keys, more, separator) = await _context.Jobs.Enqueue(
            "Scan keys",
            job => _context.Session.UseMetadataAsync(
                async (metadata, token) =>
                {
                    var store = (IKeyValueStore)metadata;
                    var found = new HashSet<string>(StringComparer.Ordinal);
                    string? cursor = null;
                    do
                    {
                        var page = await store.ScanAsync(database, pattern, cursor, 1000, token).ConfigureAwait(false);
                        found.UnionWith(page.Keys);
                        cursor = page.Cursor;
                    }
                    while (cursor is not null && found.Count < max);
                    return (found.Take(max).ToList(), cursor is not null || found.Count > max, store.Separator);
                },
                job.CancellationToken)).Completion;

        var nodes = KeyValueTree.Group(_context, database, keys, string.Empty, separator);
        if (pattern != "*")
        {
            nodes.Insert(0, new MessageNodeViewModel(string.Format(CultureInfo.CurrentCulture, System.Text.CompositeFormat.Parse(Strings.KeysFiltered), pattern)));
        }

        if (more)
        {
            nodes.Add(new MessageNodeViewModel(string.Format(CultureInfo.CurrentCulture, System.Text.CompositeFormat.Parse(Strings.KeysMore), max)));
        }

        return nodes;
    }
}

/// <summary>A group of keys sharing a prefix: "user (120)".</summary>
public sealed class KeyFolderNodeViewModel(string name, int count, Func<List<ExplorerNodeViewModel>> children)
    : LazyNodeViewModel(string.Create(CultureInfo.CurrentCulture, $"{name} ({count:N0})"), expandable: true)
{
    protected override Task<IReadOnlyList<ExplorerNodeViewModel>> LoadChildrenAsync(bool refresh) => Task.FromResult<IReadOnlyList<ExplorerNodeViewModel>>(children());
}

/// <summary>A key; opening it shows its value in a tab.</summary>
public sealed class KeyNodeViewModel(ConnectionSession session, int database, string key, string name) : ExplorerNodeViewModel(name)
{
    public ConnectionSession Session { get; } = session;

    public int Database { get; } = database;

    public string Key { get; } = key;
}
