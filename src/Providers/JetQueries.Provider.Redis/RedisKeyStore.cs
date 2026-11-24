using System.Globalization;
using System.Runtime.CompilerServices;
using JetQueries.Sdk;
using StackExchange.Redis;

namespace JetQueries.Provider.Redis;

/// <summary>
/// The key browser of a Redis connection: logical databases from <c>INFO keyspace</c>, keys with <c>SCAN</c> (never
/// <c>KEYS</c>), values by their type (at most the items asked for, read with ranges and <c>*SCAN</c>), and changes one
/// item at a time. Redis has no tables, so the catalog part of <see cref="IMetadataProvider"/> is empty.
/// </summary>
internal sealed class RedisKeyStore(RedisConnection connection) : IMetadataProvider, IKeyValueStore
{
    public char Separator => ':';

    public async IAsyncEnumerable<DatabaseObject> GetObjectsAsync(DatabaseObject? parent, DatabaseObjectKinds kinds, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DatabaseObjectName tableOrView, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ColumnInfo>>([]);

    public Task<string?> GetDefinitionAsync(DatabaseObject target, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public async Task<IReadOnlyList<KeySpace>> GetKeySpacesAsync(CancellationToken cancellationToken)
    {
        var info = await connection.Server.InfoAsync("keyspace").WaitAsync(cancellationToken).ConfigureAwait(false);
        var spaces = new Dictionary<int, long>();
        foreach (var pair in info.SelectMany(static g => g))
        {
            // db0:keys=12,expires=0,avg_ttl=0
            if (RedisConnection.Parse(pair.Key) is { } database)
            {
                var keys = pair.Value.Split(',').Select(static p => p.Split('=')).FirstOrDefault(static p => p is ["keys", _]);
                spaces[database] = keys is [_, var count] && long.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            }
        }

        // The window's own database is listed even when empty: that is where new keys go.
        spaces.TryAdd(connection.Database, 0);
        return [.. spaces.OrderBy(static s => s.Key).Select(static s => new KeySpace(s.Key, s.Value))];
    }

    public async Task<KeyScanPage> ScanAsync(int database, string pattern, string? cursor, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var result = await connection.Db(database)
            .ExecuteAsync("SCAN", cursor ?? "0", "MATCH", pattern.Length == 0 ? "*" : pattern, "COUNT", Math.Clamp(count, 10, 10_000))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var parts = (RedisResult[])result!;
        var next = parts[0].ToString();
        var keys = ((RedisResult[])parts[1]!).Select(static k => RedisText.Of((RedisValue)k)).ToList();
        return new KeyScanPage(keys, next == "0" ? null : next);
    }

    public async Task<KeyValue?> GetAsync(int database, string key, int maxItems, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var db = connection.Db(database);
        var type = await db.KeyTypeAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (type == RedisType.None)
        {
            return null;
        }

        var ttl = await db.KeyTimeToLiveAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false);
        long? memory = null;
        try
        {
            var usage = await db.ExecuteAsync("MEMORY", "USAGE", key).WaitAsync(cancellationToken).ConfigureAwait(false);
            memory = usage.IsNull ? null : (long)usage;
        }
        catch (RedisServerException)
        {
            // MEMORY may be renamed or not allowed.
        }

        var take = Math.Max(1, maxItems);
        switch (type)
        {
            case RedisType.String:
                var text = await db.StringGetAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false);
                return new KeyValue(key, KeyValueType.Text, ttl, text.IsNull ? 0 : text.Length(), memory) { Text = RedisText.Of(text) };
            case RedisType.Hash:
                var fields = new List<KeyValueItem>();
                await foreach (var entry in db.HashScanAsync(key, pageSize: Math.Min(take, 1000)).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    fields.Add(new KeyValueItem(RedisText.Of(entry.Name), RedisText.Of(entry.Value)));
                    if (fields.Count >= take)
                    {
                        break;
                    }
                }

                return new KeyValue(key, KeyValueType.Hash, ttl, await db.HashLengthAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false), memory) { Items = fields };
            case RedisType.List:
                var elements = await db.ListRangeAsync(key, 0, take - 1).WaitAsync(cancellationToken).ConfigureAwait(false);
                return new KeyValue(key, KeyValueType.List, ttl, await db.ListLengthAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false), memory)
                {
                    Items = [.. elements.Select(static (e, i) => new KeyValueItem(i.ToString(CultureInfo.InvariantCulture), RedisText.Of(e)))],
                };
            case RedisType.Set:
                var members = new List<KeyValueItem>();
                await foreach (var member in db.SetScanAsync(key, pageSize: Math.Min(take, 1000)).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    members.Add(new KeyValueItem(null, RedisText.Of(member)));
                    if (members.Count >= take)
                    {
                        break;
                    }
                }

                return new KeyValue(key, KeyValueType.Set, ttl, await db.SetLengthAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false), memory) { Items = members };
            case RedisType.SortedSet:
                var ranked = await db.SortedSetRangeByRankWithScoresAsync(key, 0, take - 1).WaitAsync(cancellationToken).ConfigureAwait(false);
                return new KeyValue(key, KeyValueType.SortedSet, ttl, await db.SortedSetLengthAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false), memory)
                {
                    Items = [.. ranked.Select(static e => new KeyValueItem(null, RedisText.Of(e.Element), e.Score))],
                };
            case RedisType.Stream:
                var entries = await db.StreamRangeAsync(key, count: take).WaitAsync(cancellationToken).ConfigureAwait(false);
                return new KeyValue(key, KeyValueType.Stream, ttl, await db.StreamLengthAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false), memory)
                {
                    Items = [.. entries.Select(static e => new KeyValueItem(e.Id.ToString(), string.Join('\n', e.Values.Select(static v => RedisText.Of(v.Name) + "=" + RedisText.Of(v.Value)))))],
                };
            default:
                return new KeyValue(key, KeyValueType.Other, ttl, 0, memory) { Text = type.ToString() };
        }
    }

    public async Task<bool> ChangeAsync(int database, KeyChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var db = connection.Db(database);
        RedisKey key = change.Key;
        var item = change.Item;
        var old = change.OldItem;
        Task<bool> work = change.Kind switch
        {
            KeyChangeKind.SetText => db.StringSetAsync(key, change.Text ?? string.Empty, expiry: null, keepTtl: true),
            KeyChangeKind.AddItem => AddAsync(db, key, change.Type, Required(item)),
            KeyChangeKind.ReplaceItem => ReplaceAsync(db, key, change.Type, Required(old), Required(item)),
            KeyChangeKind.RemoveItem => RemoveAsync(db, key, change.Type, Required(old)),
            KeyChangeKind.Rename => db.KeyRenameAsync(key, change.NewKey ?? throw new ArgumentException("A new name is needed.", nameof(change)), When.NotExists),
            KeyChangeKind.Expire when change.TimeToLive is { } ttl => db.KeyExpireAsync(key, ttl),
            KeyChangeKind.Expire => db.KeyPersistAsync(key),
            KeyChangeKind.Delete => db.KeyDeleteAsync(key),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static KeyValueItem Required(KeyValueItem? item) => item ?? throw new ArgumentException("The item is missing.", nameof(item));

    private static async Task<bool> AddAsync(IDatabase db, RedisKey key, KeyValueType type, KeyValueItem item)
    {
        switch (type)
        {
            case KeyValueType.Hash:
                await db.HashSetAsync(key, item.Name ?? throw new ArgumentException("A hash field needs a name.", nameof(item)), item.Value).ConfigureAwait(false);
                return true;
            case KeyValueType.List:
                return await db.ListRightPushAsync(key, item.Value).ConfigureAwait(false) > 0;
            case KeyValueType.Set:
                return await db.SetAddAsync(key, item.Value).ConfigureAwait(false);
            case KeyValueType.SortedSet:
                await db.SortedSetAddAsync(key, item.Value, item.Score ?? 0).ConfigureAwait(false);
                return true;
            case KeyValueType.Stream:
                var fields = item.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(static line => line.Split('=', 2))
                    .Select(static p => new NameValueEntry(p[0], p.Length > 1 ? p[1] : string.Empty))
                    .ToArray();
                if (fields.Length == 0)
                {
                    throw new ArgumentException("A stream entry needs field=value lines.", nameof(item));
                }

                await db.StreamAddAsync(key, fields).ConfigureAwait(false);
                return true;
            default:
                throw new NotSupportedException($"Items cannot be added to a {type}.");
        }
    }

    private static async Task<bool> ReplaceAsync(IDatabase db, RedisKey key, KeyValueType type, KeyValueItem old, KeyValueItem item)
    {
        switch (type)
        {
            case KeyValueType.List:
                var index = long.Parse(old.Name ?? "-1", NumberStyles.Integer, CultureInfo.InvariantCulture);
                await db.ListSetByIndexAsync(key, index, item.Value).ConfigureAwait(false);
                return true;
            case KeyValueType.Hash when old.Name == item.Name:
                await db.HashSetAsync(key, item.Name!, item.Value).ConfigureAwait(false);
                return true;
            case KeyValueType.Hash or KeyValueType.Set or KeyValueType.SortedSet:
                // A new name for a field or member: remove and add in one transaction.
                var transaction = db.CreateTransaction();
                _ = RemoveAsync(transaction, key, type, old);
                _ = AddAsync(transaction, key, type, item);
                return await transaction.ExecuteAsync().ConfigureAwait(false);
            default:
                throw new NotSupportedException($"Items of a {type} cannot be changed; remove and add them.");
        }
    }

    private static async Task<bool> RemoveAsync(IDatabaseAsync db, RedisKey key, KeyValueType type, KeyValueItem old)
    {
        switch (type)
        {
            case KeyValueType.Hash:
                return await db.HashDeleteAsync(key, old.Name ?? string.Empty).ConfigureAwait(false);
            case KeyValueType.Set:
                return await db.SetRemoveAsync(key, old.Value).ConfigureAwait(false);
            case KeyValueType.SortedSet:
                return await db.SortedSetRemoveAsync(key, old.Value).ConfigureAwait(false);
            case KeyValueType.Stream:
                return await db.StreamDeleteAsync(key, [old.Name ?? string.Empty]).ConfigureAwait(false) > 0;
            case KeyValueType.List when db is IDatabase single:
                // By index: the element is marked, then the mark removed, in one transaction (a value may repeat).
                var index = long.Parse(old.Name ?? "-1", NumberStyles.Integer, CultureInfo.InvariantCulture);
                var mark = "__jetqueries_removed_" + Guid.NewGuid().ToString("N");
                var transaction = single.CreateTransaction();
                transaction.AddCondition(Condition.ListIndexEqual(key, index, old.Value));
                _ = transaction.ListSetByIndexAsync(key, index, mark);
                _ = transaction.ListRemoveAsync(key, mark, 1);
                return await transaction.ExecuteAsync().ConfigureAwait(false);
            default:
                throw new NotSupportedException($"Items cannot be removed from a {type}.");
        }
    }

    private static Task<bool> AddAsync(ITransaction transaction, RedisKey key, KeyValueType type, KeyValueItem item) => type switch
    {
        KeyValueType.Hash => transaction.HashSetAsync(key, item.Name ?? string.Empty, item.Value),
        KeyValueType.Set => transaction.SetAddAsync(key, item.Value),
        KeyValueType.SortedSet => transaction.SortedSetAddAsync(key, item.Value, item.Score ?? 0),
        _ => throw new NotSupportedException($"Items of a {type} cannot be changed."),
    };
}
