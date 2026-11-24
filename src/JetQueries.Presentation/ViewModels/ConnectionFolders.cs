using JetQueries.Core.Jobs;
using JetQueries.Core.Settings;

namespace JetQueries.Presentation.ViewModels;

/// <summary>The stored empty folders of the connection tree (the <c>folders</c> settings section).</summary>
public sealed record ConnectionFoldersState
{
    public const string Section = "folders";

    /// <summary>Folder paths ("Customer A/Test") that exist even without a connection inside.</summary>
    public List<string> Paths { get; init; } = [];
}

/// <summary>
/// Folders of the connection tree. A connection keeps its folder path itself (<c>Folder</c>); folders created
/// before anything is moved into them are kept here, so that a new folder does not vanish at once.
/// </summary>
public sealed class ConnectionFolders(ISettingsStore? store, IJobQueue jobs)
{
    private readonly object _gate = new();
    private List<string> _paths = [];
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
    });

    /// <summary>A folder and everything under it is forgotten.</summary>
    public void Remove(string path) => Change(paths => paths.RemoveAll(p => FolderPath.IsSameOrUnder(p, path)));

    private void Change(Action<List<string>> change)
    {
        ConnectionFoldersState snapshot;
        lock (_gate)
        {
            change(_paths);
            snapshot = new ConnectionFoldersState { Paths = [.. _paths] };
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
