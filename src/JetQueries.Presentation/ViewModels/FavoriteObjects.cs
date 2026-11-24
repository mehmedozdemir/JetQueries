using JetQueries.Core.Jobs;
using JetQueries.Core.Settings;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>An object pinned in the explorer, as it is stored (the <c>favorites</c> settings section).</summary>
public sealed record FavoriteObject(string? Database, string? Schema, string Name, DatabaseObjectKinds Kind)
{
    public DatabaseObject ToObject() => new(new DatabaseObjectName(Database, Schema, Name), Kind);

    public static FavoriteObject From(DatabaseObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new(target.Name.Database, target.Name.Schema, target.Name.Name, target.Kind);
    }
}

/// <summary>The stored favourites: per connection id, in the order they were added.</summary>
public sealed record FavoriteObjectsState
{
    public const string Section = "favorites";

    public Dictionary<Guid, List<FavoriteObject>> Connections { get; init; } = [];
}

/// <summary>
/// Objects the user pinned in the explorer, per connection. They are shown under "Favourites" at the top of the
/// connection, without reading the catalog. Kept in the user's settings (names only, nothing secret).
/// </summary>
public sealed class FavoriteObjects(ISettingsStore? store, IJobQueue jobs)
{
    private readonly object _gate = new();
    private Dictionary<Guid, List<FavoriteObject>> _items = [];
    private Task? _loading;

    /// <summary>Raised after a favourite was added or removed (argument: the connection).</summary>
    public event EventHandler<Guid>? Changed;

    /// <summary>Reads the stored favourites once; later calls wait for the same read.</summary>
    public Task LoadAsync()
    {
        lock (_gate)
        {
            return _loading ??= ReadAsync();
        }
    }

    /// <summary>The favourites of a connection, in order.</summary>
    public IReadOnlyList<DatabaseObject> Of(Guid connectionId)
    {
        lock (_gate)
        {
            return _items.TryGetValue(connectionId, out var list) ? [.. list.Select(static f => f.ToObject())] : [];
        }
    }

    public bool Contains(Guid connectionId, DatabaseObject target)
    {
        var item = FavoriteObject.From(target);
        lock (_gate)
        {
            return _items.TryGetValue(connectionId, out var list) && list.Contains(item);
        }
    }

    /// <summary>Adds the object, or removes it when it is a favourite already; saved in the background.</summary>
    /// <returns>Whether it is a favourite now.</returns>
    public bool Toggle(Guid connectionId, DatabaseObject target)
    {
        var item = FavoriteObject.From(target);
        bool added;
        FavoriteObjectsState snapshot;
        lock (_gate)
        {
            if (!_items.TryGetValue(connectionId, out var list))
            {
                _items[connectionId] = list = [];
            }

            added = !list.Remove(item);
            if (added)
            {
                list.Add(item);
            }
            else if (list.Count == 0)
            {
                _items.Remove(connectionId);
            }

            snapshot = new FavoriteObjectsState { Connections = _items.ToDictionary(static p => p.Key, static p => p.Value.ToList()) };
        }

        if (store is not null)
        {
            jobs.Enqueue("Save favourites", context => store.SaveAsync(FavoriteObjectsState.Section, snapshot, context.CancellationToken), JobPriority.Low);
        }

        Changed?.Invoke(this, connectionId);
        return added;
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
                "Read favourites",
                context => store.LoadAsync(FavoriteObjectsState.Section, new FavoriteObjectsState(), context.CancellationToken),
                JobPriority.High).Completion.ConfigureAwait(false);
            lock (_gate)
            {
                _items = state.Connections.ToDictionary(static p => p.Key, static p => p.Value.Where(static f => !string.IsNullOrEmpty(f.Name)).ToList());
            }
        }
#pragma warning disable CA1031 // Favourites that cannot be read start empty; nothing else depends on them.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
