using JetQueries.Core.Jobs;
using JetQueries.Core.Settings;

namespace JetQueries.Presentation.ViewModels;

/// <summary>How the explorer groups connections at its top level.</summary>
public enum ExplorerGrouping
{
    /// <summary>The user's folders (the default).</summary>
    Folders,

    /// <summary>By environment (development, test, production, unmarked), folders inside each.</summary>
    Environment,

    /// <summary>By database type (SQL Server, Oracle…), folders inside each.</summary>
    DatabaseType,
}

/// <summary>The explorer's own state (the <c>folders</c> settings section): empty folders, colours, grouping.</summary>
public sealed record ConnectionFoldersState
{
    public const string Section = "folders";

    /// <summary>Folder paths ("Customer A/Test") that exist even without a connection inside.</summary>
    public List<string> Paths { get; init; } = [];

    /// <summary>A colour (#RRGGBB) of a folder, by path.</summary>
    public Dictionary<string, string> Colors { get; init; } = [];

    /// <summary>How the top level is grouped.</summary>
    public ExplorerGrouping Grouping { get; init; }

    /// <summary>The groups the user closed ("env:Production", "type:oracle"); every other group is open.</summary>
    public List<string> CollapsedGroups { get; init; } = [];
}

/// <summary>
/// Folders of the connection tree. A connection keeps its folder path itself (<c>Folder</c>); folders created
/// before anything is moved into them are kept here, so that a new folder does not vanish at once.
/// </summary>
public sealed class ConnectionFolders(ISettingsStore? store, IJobQueue jobs)
{
    private readonly object _gate = new();
    private List<string> _paths = [];
    private Dictionary<string, string> _colors = new(StringComparer.OrdinalIgnoreCase);
    private ExplorerGrouping _grouping;
    private HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private Task? _loading;

    /// <summary>Reads the stored folders once; later calls wait for the same read.</summary>
    public Task LoadAsync()
    {
        lock (_gate)
        {
            return _loading ??= ReadAsync();
        }
    }

    /// <summary>Every stored folder path.</summary>
    public IReadOnlyList<string> Paths
    {
        get
        {
            lock (_gate)
            {
                return [.. _paths];
            }
        }
    }

    /// <summary>How the top level is grouped (kept between sessions).</summary>
    public ExplorerGrouping Grouping
    {
        get
        {
            lock (_gate)
            {
                return _grouping;
            }
        }

        set => Change(_ => _grouping = value);
    }

    /// <summary>Whether a group was closed by the user (groups are open otherwise).</summary>
    public bool IsCollapsed(string group)
    {
        lock (_gate)
        {
            return _collapsed.Contains(group);
        }
    }

    /// <summary>Remembers a group closed or opened.</summary>
    public void SetCollapsed(string group, bool collapsed)
    {
        lock (_gate)
        {
            if (_collapsed.Contains(group) == collapsed)
            {
                return;
            }
        }

        Change(_ =>
        {
            if (collapsed)
            {
                _collapsed.Add(group);
            }
            else
            {
                _collapsed.Remove(group);
            }
        });
    }

    /// <summary>The colour of a folder (#RRGGBB); null when it has none.</summary>
    public string? ColorOf(string path)
    {
        lock (_gate)
        {
            return _colors.GetValueOrDefault(path);
        }
    }

    /// <summary>Gives a folder a colour, or takes it away (null).</summary>
    public void SetColor(string path, string? color) => Change(_ =>
    {
        if (color is null)
        {
            _colors.Remove(path);
        }
        else
        {
            _colors[path] = color;
        }
    });

    public void Add(string path) => Change(paths =>
    {
        if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(path);
        }
    });

    /// <summary>A folder and everything under it moves from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public void Move(string from, string to) => Change(paths =>
    {
        for (var i = 0; i < paths.Count; i++)
        {
            if (FolderPath.IsSameOrUnder(paths[i], from))
            {
                paths[i] = to + paths[i][from.Length..];
            }
        }

        foreach (var (path, color) in _colors.Where(c => FolderPath.IsSameOrUnder(c.Key, from)).ToList())
        {
            _colors.Remove(path);
            _colors[to + path[from.Length..]] = color;
        }
    });

    /// <summary>A folder and everything under it is forgotten.</summary>
    public void Remove(string path) => Change(paths =>
    {
        paths.RemoveAll(p => FolderPath.IsSameOrUnder(p, path));
        foreach (var colored in _colors.Keys.Where(k => FolderPath.IsSameOrUnder(k, path)).ToList())
        {
            _colors.Remove(colored);
        }
    });

    private void Change(Action<List<string>> change)
    {
        ConnectionFoldersState snapshot;
        lock (_gate)
        {
            change(_paths);
            snapshot = new ConnectionFoldersState
            {
                Paths = [.. _paths], Colors = new(_colors, StringComparer.OrdinalIgnoreCase), Grouping = _grouping, CollapsedGroups = [.. _collapsed.Order(StringComparer.Ordinal)],
            };
        }

        if (store is not null)
        {
            jobs.Enqueue("Save folders", context => store.SaveAsync(ConnectionFoldersState.Section, snapshot, context.CancellationToken), JobPriority.Low);
        }
    }

    private async Task ReadAsync()
    {
        if (store is null)
        {
            return;
        }

        try
        {
            var state = await jobs.Enqueue(
                "Read folders",
                context => store.LoadAsync(ConnectionFoldersState.Section, new ConnectionFoldersState(), context.CancellationToken),
                JobPriority.High).Completion.ConfigureAwait(false);
            lock (_gate)
            {
                _paths = [.. state.Paths.Select(FolderPath.Normalize).Where(static p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
                _colors = new Dictionary<string, string>(
                    (state.Colors ?? []).Where(static c => FolderColors.IsColor(c.Value)).Select(static c => KeyValuePair.Create(FolderPath.Normalize(c.Key), c.Value))
                        .DistinctBy(static c => c.Key, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
                _grouping = Enum.IsDefined(state.Grouping) ? state.Grouping : ExplorerGrouping.Folders;
                _collapsed = new HashSet<string>(state.CollapsedGroups ?? [], StringComparer.Ordinal);
            }
        }
#pragma warning disable CA1031 // Folders that cannot be read start empty; connections keep their own folder.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}

/// <summary>Folder paths: names joined by '/', compared without regard to case (Turkish-safe, ordinal).</summary>
public static class FolderPath
{
    /// <summary>Trimmed parts joined by '/'; empty for the top level.</summary>
    public static string Normalize(string? path) =>
        string.Join('/', (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>The path of a child folder.</summary>
    public static string Combine(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

    /// <summary>The parent of a path; empty at the top.</summary>
    public static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    public static bool IsSameOrUnder(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ||
        (folder.Length > 0 && path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase));
}

/// <summary>The colours a folder can be given (the palette of the explorer's menu).</summary>
public static class FolderColors
{
    /// <summary>Name (a resource key's end) and colour, readable in both themes.</summary>
    public static IReadOnlyList<(string Name, string Color)> Palette { get; } =
    [
        ("Red", "#E5484D"), ("Orange", "#F08A24"), ("Yellow", "#D9A400"), ("Green", "#30A46C"), ("Teal", "#12A594"),
        ("Blue", "#3B82F6"), ("Purple", "#8E4EC6"), ("Pink", "#D6409F"), ("Gray", "#8B8D98"),
    ];

    /// <summary>Whether a text is a colour as stored (#RRGGBB).</summary>
    public static bool IsColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept(HexDigits) < 0;

    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");
}
