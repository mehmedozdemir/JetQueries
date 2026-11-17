using System.Diagnostics;
using System.Runtime.CompilerServices;
using JetQueries.Core.Metadata;
using JetQueries.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Core.Tests.Metadata;

public sealed class SqliteMetadataCacheTests
{
    private static readonly Guid Connection = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherConnection = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DatabaseObject Schema = new(new DatabaseObjectName("db", null, "dbo"), DatabaseObjectKinds.Schema);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_miss_returns_null()
    {
        await using var cache = Create(NewPath());

        Assert.Null(await cache.GetObjectsAsync(Connection, null, DatabaseObjectKinds.Database, Token));
        Assert.Null(await cache.GetColumnsAsync(Connection, new DatabaseObjectName(null, "dbo", "T"), Token));
    }

    [Fact]
    public async Task Objects_round_trip_through_the_file()
    {
        var path = NewPath();
        var clock = new ManualTime(DateTimeOffset.Parse("2026-11-17T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var tables = new[]
        {
            new DatabaseObject(new DatabaseObjectName("db", "dbo", "Müşteri İşlemleri"), DatabaseObjectKinds.Table),
            new DatabaseObject(new DatabaseObjectName("db", "dbo", "it's \"quoted\""), DatabaseObjectKinds.View),
        };

        await using (var first = Create(path, clock))
        {
            await first.SetObjectsAsync(Connection, Schema, DatabaseObjectKinds.Table | DatabaseObjectKinds.View, tables, Token);
        }

        // A new instance has an empty memory layer: this answer can only come from the file.
        await using var second = Create(path, clock);
        var cached = await second.GetObjectsAsync(Connection, Schema, DatabaseObjectKinds.Table | DatabaseObjectKinds.View, Token);

        Assert.NotNull(cached);
        Assert.Equal(tables, cached.Value);
        Assert.Equal(clock.GetUtcNow(), cached.LoadedAt);
    }

    [Fact]
    public async Task Columns_round_trip_through_the_file()
    {
        var path = NewPath();
        var table = new DatabaseObjectName("db", "dbo", "Cards");
        var columns = new[] { new ColumnInfo("Id", "bigint", false, 1, true), new ColumnInfo("Note", "nvarchar(max)", true, 2) };

        await using (var first = Create(path))
        {
            await first.SetColumnsAsync(Connection, table, columns, Token);
        }

        await using var second = Create(path);
        Assert.Equal(columns, (await second.GetColumnsAsync(Connection, table, Token))!.Value);
    }

    [Fact]
    public async Task Entries_are_separate_per_connection_parent_kinds_and_name()
    {
        await using var cache = Create(NewPath());
        var one = new[] { new DatabaseObject(new DatabaseObjectName("db", "dbo", "A"), DatabaseObjectKinds.Table) };
        await cache.SetObjectsAsync(Connection, Schema, DatabaseObjectKinds.Table, one, Token);

        Assert.Null(await cache.GetObjectsAsync(OtherConnection, Schema, DatabaseObjectKinds.Table, Token));
        Assert.Null(await cache.GetObjectsAsync(Connection, null, DatabaseObjectKinds.Table, Token));
        Assert.Null(await cache.GetObjectsAsync(Connection, Schema, DatabaseObjectKinds.View, Token));
        Assert.Null(await cache.GetObjectsAsync(Connection, Schema with { Name = new DatabaseObjectName("db", null, "DBO") }, DatabaseObjectKinds.Table, Token));

        // A missing schema and an empty schema are different names.
        var columns = new[] { new ColumnInfo("c", "int", true, 1) };
        await cache.SetColumnsAsync(Connection, new DatabaseObjectName(null, null, "T"), columns, Token);
        Assert.Null(await cache.GetColumnsAsync(Connection, new DatabaseObjectName(null, string.Empty, "T"), Token));
    }

    [Fact]
    public async Task A_later_set_replaces_the_entry()
    {
        var path = NewPath();
        var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(20_000));
        await using (var cache = Create(path, clock))
        {
            await cache.SetObjectsAsync(Connection, null, DatabaseObjectKinds.Database, [Schema], Token);
            clock.Advance(TimeSpan.FromMinutes(5));
            await cache.SetObjectsAsync(Connection, null, DatabaseObjectKinds.Database, [], Token);
        }

        await using var reopened = Create(path, clock);
        var cached = await reopened.GetObjectsAsync(Connection, null, DatabaseObjectKinds.Database, Token);
        Assert.Empty(cached!.Value);
        Assert.Equal(clock.GetUtcNow(), cached.LoadedAt);
    }

    [Fact]
    public async Task Invalidate_forgets_one_connection_in_memory_and_on_disk()
    {
        var path = NewPath();
        await using (var cache = Create(path))
        {
            await cache.SetObjectsAsync(Connection, null, DatabaseObjectKinds.Database, [Schema], Token);
            await cache.SetObjectsAsync(OtherConnection, null, DatabaseObjectKinds.Database, [Schema], Token);

            await cache.InvalidateAsync(Connection, Token);

            Assert.Null(await cache.GetObjectsAsync(Connection, null, DatabaseObjectKinds.Database, Token));
            Assert.NotNull(await cache.GetObjectsAsync(OtherConnection, null, DatabaseObjectKinds.Database, Token));
        }

        await using var reopened = Create(path);
        Assert.Null(await reopened.GetObjectsAsync(Connection, null, DatabaseObjectKinds.Database, Token));
        Assert.NotNull(await reopened.GetObjectsAsync(OtherConnection, null, DatabaseObjectKinds.Database, Token));
    }

    [Fact]
    public async Task A_file_that_is_not_a_database_is_replaced()
    {
        var path = NewPath();
        await File.WriteAllTextAsync(path, new string('x', 4096), Token);

        await using (var cache = Create(path))
        {
            await cache.SetObjectsAsync(Connection, null, DatabaseObjectKinds.Database, [Schema], Token);
        }

        await using var reopened = Create(path);
        Assert.NotNull(await reopened.GetObjectsAsync(Connection, null, DatabaseObjectKinds.Database, Token));
    }

    [Fact]
    public async Task An_unusable_path_falls_back_to_memory()
    {
        // The "file" is an existing directory: it can be neither opened nor replaced.
        var directory = Path.Combine(AppContext.BaseDirectory, "metadata-cache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "child"));
        await using var cache = Create(directory);

        await cache.SetObjectsAsync(Connection, null, DatabaseObjectKinds.Database, [Schema], Token);

        Assert.NotNull(await cache.GetObjectsAsync(Connection, null, DatabaseObjectKinds.Database, Token));
    }

    [Fact]
    public async Task Without_a_path_the_cache_is_memory_only()
    {
        await using var cache = Create(null);

        await cache.SetObjectsAsync(Connection, null, DatabaseObjectKinds.Database, [Schema], Token);

        Assert.Single((await cache.GetObjectsAsync(Connection, null, DatabaseObjectKinds.Database, Token))!.Value);
    }

    [Fact]
    public async Task Concurrent_readers_and_writers_do_not_interfere()
    {
        await using var cache = Create(NewPath());

        await Task.WhenAll(Enumerable.Range(0, 64).Select(async i =>
        {
            var table = new DatabaseObjectName("db", "dbo", $"T{i % 8}");
            await cache.SetColumnsAsync(Connection, table, [new ColumnInfo("c", "int", true, 1)], Token);
            Assert.NotNull(await cache.GetColumnsAsync(Connection, table, Token));
        }));
    }

    [Fact]
    public async Task Warm_lookups_fit_the_intellisense_budget()
    {
        await using var cache = Create(NewPath());
        var tables = Enumerable.Range(0, 5_000)
            .Select(static i => new DatabaseObject(new DatabaseObjectName("db", "dbo", $"Table{i}"), DatabaseObjectKinds.Table)).ToArray();
        await cache.SetObjectsAsync(Connection, Schema, DatabaseObjectKinds.Table, tables, Token);
        await cache.GetObjectsAsync(Connection, Schema, DatabaseObjectKinds.Table, Token);

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 1_000; i++)
        {
            Assert.Equal(5_000, (await cache.GetObjectsAsync(Connection, Schema, DatabaseObjectKinds.Table, Token))!.Value.Count);
        }

        // Budget: IntelliSense in under 100 ms with a warm cache. A lookup must be a tiny part of that.
        Assert.True(watch.Elapsed.TotalMilliseconds / 1_000 < 1, $"warm lookup took {watch.Elapsed.TotalMilliseconds / 1_000:F3} ms on average");
    }

    [Fact]
    public async Task Read_through_loads_once_and_reloads_when_stale()
    {
        var clock = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(20_000));
        await using var cache = Create(NewPath(), clock);
        var source = new CountingProvider();
        var maxAge = TimeSpan.FromMinutes(10);

        var first = await cache.GetOrLoadObjectsAsync(Connection, source, Schema, DatabaseObjectKinds.Table, maxAge, clock, Token);
        var second = await cache.GetOrLoadObjectsAsync(Connection, source, Schema, DatabaseObjectKinds.Table, maxAge, clock, Token);
        Assert.Equal(1, source.ObjectCalls);
        Assert.Equal(first.Value, second.Value);

        clock.Advance(TimeSpan.FromMinutes(11));
        await cache.GetOrLoadObjectsAsync(Connection, source, Schema, DatabaseObjectKinds.Table, maxAge, clock, Token);
        Assert.Equal(2, source.ObjectCalls);

        // Zero forces a reload, as "Refresh" does.
        await cache.GetOrLoadObjectsAsync(Connection, source, Schema, DatabaseObjectKinds.Table, TimeSpan.Zero, clock, Token);
        Assert.Equal(3, source.ObjectCalls);

        var table = new DatabaseObjectName("db", "dbo", "T");
        await cache.GetOrLoadColumnsAsync(Connection, source, table, maxAge, clock, Token);
        await cache.GetOrLoadColumnsAsync(Connection, source, table, maxAge, clock, Token);
        Assert.Equal(1, source.ColumnCalls);
    }

    private static SqliteMetadataCache Create(string? path, TimeProvider? clock = null) =>
        new(new MetadataCacheOptions { DatabasePath = path }, clock ?? TimeProvider.System, NullLogger<SqliteMetadataCache>.Instance);

    private static string NewPath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "metadata-cache-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db");
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CountingProvider : IMetadataProvider
    {
        public int ObjectCalls { get; private set; }

        public int ColumnCalls { get; private set; }

        public async IAsyncEnumerable<DatabaseObject> GetObjectsAsync(
            DatabaseObject? parent, DatabaseObjectKinds kinds, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ObjectCalls++;
            await Task.Yield();
            yield return new DatabaseObject(new DatabaseObjectName("db", "dbo", "A"), DatabaseObjectKinds.Table);
            yield return new DatabaseObject(new DatabaseObjectName("db", "dbo", "B"), DatabaseObjectKinds.Table);
        }

        public Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DatabaseObjectName tableOrView, CancellationToken cancellationToken)
        {
            ColumnCalls++;
            return Task.FromResult<IReadOnlyList<ColumnInfo>>([new ColumnInfo("Id", "int", false, 1, true)]);
        }

        public Task<string?> GetDefinitionAsync(DatabaseObject target, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
