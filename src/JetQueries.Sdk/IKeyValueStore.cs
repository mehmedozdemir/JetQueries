namespace JetQueries.Sdk;

/// <summary>
/// Optional for the <see cref="IDatabaseConnection.Metadata"/> of a key-value database (Redis): what the explorer and
/// the key view need instead of tables and columns. Keys are listed incrementally (<c>SCAN</c>), never all at once;
/// values are read by their type and edited one item at a time. Every member is asynchronous and cancelable.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0.</remarks>
public interface IKeyValueStore
{
    /// <summary>The character keys are grouped by in the explorer (Redis: <c>:</c>).</summary>
    char Separator { get; }

    /// <summary>The logical databases and how many keys each holds (empty ones only when they are the default).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<KeySpace>> GetKeySpacesAsync(CancellationToken cancellationToken);

    /// <summary>One step of an incremental key listing.</summary>
    /// <param name="database">The logical database.</param>
    /// <param name="pattern">A glob pattern (<c>user:*</c>); <c>*</c> for every key.</param>
    /// <param name="cursor">Where the previous step ended; null to start.</param>
    /// <param name="count">About how many keys to look at in this step.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<KeyScanPage> ScanAsync(int database, string pattern, string? cursor, int count, CancellationToken cancellationToken);

    /// <summary>A key's type, time to live, size and value (at most <paramref name="maxItems"/> items of a collection).</summary>
    /// <param name="database">The logical database.</param>
    /// <param name="key">The key.</param>
    /// <param name="maxItems">The most items of a list, hash, set, sorted set or stream to read.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The value; null when the key does not exist (any more).</returns>
    Task<KeyValue?> GetAsync(int database, string key, int maxItems, CancellationToken cancellationToken);

    /// <summary>Applies one change to a key (sets a string, adds or removes an item, renames, expires, deletes).</summary>
    /// <param name="database">The logical database.</param>
    /// <param name="change">The change.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether it was applied (false: the key or item was not there, or the new name is taken).</returns>
    Task<bool> ChangeAsync(int database, KeyChange change, CancellationToken cancellationToken);
}

/// <summary>A logical database.</summary>
/// <param name="Database">Its number.</param>
/// <param name="KeyCount">Keys in it.</param>
public sealed record KeySpace(int Database, long KeyCount);

/// <summary>A step of a key listing.</summary>
/// <param name="Keys">The keys found in this step (may be empty even when more follow).</param>
/// <param name="Cursor">Where the next step starts; null when the listing is complete.</param>
public sealed record KeyScanPage(IReadOnlyList<string> Keys, string? Cursor);

/// <summary>The type of a key's value.</summary>
public enum KeyValueType
{
    /// <summary>A type the host has no view for.</summary>
    Other,

    /// <summary>A string (text or bytes).</summary>
    Text,

    /// <summary>Fields and their values.</summary>
    Hash,

    /// <summary>Elements in order.</summary>
    List,

    /// <summary>Distinct members.</summary>
    Set,

    /// <summary>Distinct members with scores.</summary>
    SortedSet,

    /// <summary>Entries with ids, each a set of fields.</summary>
    Stream,
}

/// <summary>A key and its value.</summary>
/// <param name="Key">The key.</param>
/// <param name="Type">Its type.</param>
/// <param name="TimeToLive">When it expires from now; null when it does not.</param>
/// <param name="Length">Characters of a string, items of a collection.</param>
/// <param name="MemoryBytes">Memory it takes on the server, when known.</param>
public sealed record KeyValue(string Key, KeyValueType Type, TimeSpan? TimeToLive, long Length, long? MemoryBytes)
{
    /// <summary>The value of a string.</summary>
    public string? Text { get; init; }

    /// <summary>The items of a collection, in the server's order (at most the number asked for).</summary>
    public IReadOnlyList<KeyValueItem> Items { get; init; } = [];

    /// <summary>The collection has more items than were read.</summary>
    public bool IsTruncated => Items.Count < Length && Type is not (KeyValueType.Text or KeyValueType.Other);
}

/// <summary>An item of a collection.</summary>
/// <param name="Name">A hash's field, a stream entry's id, a list's index; null for a set or sorted set member.</param>
/// <param name="Value">The value or member (a stream entry's fields as <c>field=value</c> lines).</param>
/// <param name="Score">A sorted set member's score.</param>
public sealed record KeyValueItem(string? Name, string Value, double? Score = null);

/// <summary>What a <see cref="KeyChange"/> does.</summary>
public enum KeyChangeKind
{
    /// <summary>Sets a string's value (creating the key).</summary>
    SetText,

    /// <summary>Adds an item: a hash field (or sets it), a list's last element, a set or sorted set member, a stream entry.</summary>
    AddItem,

    /// <summary>Replaces <see cref="KeyChange.OldItem"/> with <see cref="KeyChange.Item"/> (a list element by index).</summary>
    ReplaceItem,

    /// <summary>Removes <see cref="KeyChange.OldItem"/>.</summary>
    RemoveItem,

    /// <summary>Renames the key to <see cref="KeyChange.NewKey"/>, unless that name is taken.</summary>
    Rename,

    /// <summary>Sets the time to live (<see cref="KeyChange.TimeToLive"/>; null to keep it for ever).</summary>
    Expire,

    /// <summary>Deletes the key.</summary>
    Delete,
}

/// <summary>One change to a key.</summary>
/// <param name="Key">The key.</param>
/// <param name="Kind">What to do.</param>
/// <param name="Type">The key's type (for adding to a key that does not exist yet).</param>
public sealed record KeyChange(string Key, KeyChangeKind Kind, KeyValueType Type = KeyValueType.Other)
{
    /// <summary>The new string (SetText).</summary>
    public string? Text { get; init; }

    /// <summary>The item added or the replacement.</summary>
    public KeyValueItem? Item { get; init; }

    /// <summary>The item replaced or removed.</summary>
    public KeyValueItem? OldItem { get; init; }

    /// <summary>The new name (Rename).</summary>
    public string? NewKey { get; init; }

    /// <summary>The time to live (Expire); null to persist.</summary>
    public TimeSpan? TimeToLive { get; init; }
}
