using System.Globalization;
using JetQueries.Core.Queries;
using JetQueries.Provider.Redis;
using JetQueries.Sdk;
using Testcontainers.Redis;

namespace JetQueries.Provider.Redis.Tests;

/// <summary>Reading console text and classifying it, without a server.</summary>
public sealed class RedisConsoleTextTests
{
    [Fact]
    public void Lines_are_commands_with_quoted_words_and_comments()
    {
        var commands = RedisCommandParser.Parse("# a comment\nSET \"user:1 name\" 'İstanbul \"merkez\"'\r\n\n  HSET h f \"a\\tb\\x41\"\n// another");

        Assert.Equal(2, commands.Count);
        Assert.Equal(["SET", "user:1 name", "İstanbul \"merkez\""], commands[0].Words);
        Assert.Equal(2, commands[0].Line);
        Assert.Equal(["HSET", "h", "f", "a\tbA"], commands[1].Words);
        Assert.Throws<FormatException>(() => RedisCommandParser.Parse("GET \"open"));
    }

    [Fact]
    public void Reads_writes_and_destructive_commands_are_told_apart()
    {
        var provider = new RedisProvider();

        var reads = provider.AnalyzeRisk("GET a\nHGETALL h\nscan 0 MATCH user:*\nCONFIG GET maxmemory");
        Assert.False(reads.WritesData);
        Assert.Empty(reads.DestructiveActions);
        Assert.True(provider.AnalyzeRisk("SET a 1").WritesData);
        Assert.True(provider.AnalyzeRisk("CONFIG SET maxmemory 1mb").WritesData);
        Assert.True(provider.AnalyzeRisk("SOMEMODULE.CMD x").WritesData);
        var risk = provider.AnalyzeRisk("DEL a\nflushall\nDEL b");
        Assert.Equal(["DEL", "FLUSHALL"], risk.DestructiveActions);

        // The host uses it instead of reading the text as SQL.
        Assert.Equal(["FLUSHDB"], StatementGuard.Analyze("FLUSHDB", provider).DestructiveActions);
        Assert.Equal("\"user 1\"", provider.QuoteIdentifier("user 1"));
        Assert.Equal("user:1", provider.QuoteIdentifier("user:1"));
    }

    [Fact]
    public async Task Completion_offers_commands_at_the_start_and_sub_commands_after_a_container()
    {
        var service = new RedisProvider().LanguageService!;

        var first = await service.GetCompletionsAsync(new SqlDocument("hge"), 3, TestContext.Current.CancellationToken);
        var hget = Assert.Single(first, static c => c.Label == "HGETALL");
        Assert.Equal(new TextSpan(0, 3), hget.ReplaceSpan);
        Assert.Equal("Every field and value of a hash.", hget.Documentation);

        var second = await service.GetCompletionsAsync(new SqlDocument("GET a\nCONFIG "), 13, TestContext.Current.CancellationToken);
        Assert.Contains(second, static c => c.Label == "GET");
        Assert.Contains(second, static c => c.Label == "SET");

        var tokens = service.TokenizeLine("SET \"k 1\" 42 EX 10", 0).Tokens;
        Assert.Equal([SqlTokenKind.Keyword, SqlTokenKind.StringLiteral, SqlTokenKind.NumberLiteral, SqlTokenKind.Keyword, SqlTokenKind.NumberLiteral], tokens.Select(static t => t.Kind));
    }
}

/// <summary>A Redis container for the integration tests; opt-in like the other databases.</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    public const string OptInVariable = "JETQUERIES_REDIS_TESTS";
    public const string Image = "redis:7-alpine";

    private RedisContainer? _container;

    public string? SkipReason { get; private set; }

    public ConnectionProfile Profile { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(OptInVariable), "Y", StringComparison.OrdinalIgnoreCase))
        {
            SkipReason = $"Set {OptInVariable}=Y to start a Redis container and run the integration tests.";
            return;
        }

        try
        {
            _container = new RedisBuilder(Image).Build();
            await _container.StartAsync();
        }
#pragma warning disable CA1031 // Any failure to start Docker means "skip".
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SkipReason = $"Docker is not available: {ex.Message}";
            return;
        }

        Profile = new ConnectionProfile(Guid.NewGuid(), "container", RedisProvider.ProviderId, new Dictionary<string, string>
        {
            ["host"] = _container.Hostname,
            ["port"] = _container.GetMappedPublicPort(6379).ToString(CultureInfo.InvariantCulture),
            ["database"] = "0",
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>The console and the key browser against a real Redis.</summary>
public sealed class RedisIntegrationTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_console_runs_commands_line_by_line_and_shows_their_answers_as_rows()
    {
        await using var connection = await OpenAsync();
        var events = await RunAsync(connection, "SELECT 3\nFLUSHDB\nSET \"şehir:1\" İstanbul\nHSET user:1 name Ayşe age 42\nHGETALL user:1\nRPUSH list a b c\nLRANGE list 0 -1\nINCR counter\nGET nothing\nINFO server");

        Assert.Contains(events, static e => e is QueryMessage { Text: "OK — db3" });
        var sets = Split(events);
        Assert.Equal("OK", sets[0].Rows[0][0]);
        Assert.Equal(["field", "value"], sets[3].Columns.Select(static c => c.Name));
        Assert.Contains(sets[3].Rows, static r => r is ["name", "Ayşe"]);
        Assert.Equal([1L, "a"], sets[5].Rows[0]);
        Assert.Equal(1L, sets[6].Rows[0][0]);
        Assert.Null(sets[7].Rows[0][0]);
        Assert.Contains(sets[8].Rows, static r => r is ["Server", "redis_version", _]);

        // The window stays in db3.
        Assert.Equal("db3", await ((IDatabaseContext)connection).GetCurrentAsync(Token));
        var exists = Split(await RunAsync(connection, "EXISTS \"şehir:1\""));
        Assert.Equal(1L, exists[0].Rows[0][0]);

        // KEYS is refused with the reason; a server error names its line.
        var keys = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(connection, "KEYS *"));
        Assert.Contains("SCAN", keys.Message, StringComparison.Ordinal);
        var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(connection, "GET a\nHGET \"şehir:1\" x"));
        Assert.StartsWith("Line 2:", wrong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keys_are_listed_with_scan_and_read_by_their_type()
    {
        await using var connection = await OpenAsync();
        await RunAsync(connection, "SELECT 5\nFLUSHDB");
        var lines = string.Join('\n', Enumerable.Range(1, 120).Select(static i => $"SET user:{i}:name u{i}"));
        await RunAsync(connection, lines + "\nHSET user:1 name Ayşe city İstanbul\nRPUSH queue a b c b\nSADD tags x y\nZADD board 10 ali 20 veli\nXADD events * kind login user 1\nSET temp 1 EX 600");
        var store = (IKeyValueStore)connection.Metadata;

        var spaces = await store.GetKeySpacesAsync(Token);
        Assert.Contains(new KeySpace(5, 126), spaces);

        // SCAN in steps until the cursor ends.
        var keys = new List<string>();
        string? cursor = null;
        do
        {
            var page = await store.ScanAsync(5, "user:*", cursor, 50, Token);
            keys.AddRange(page.Keys);
            cursor = page.Cursor;
        }
        while (cursor is not null);
        Assert.Equal(121, keys.Distinct().Count());

        var hash = (await store.GetAsync(5, "user:1", 100, Token))!;
        Assert.Equal(KeyValueType.Hash, hash.Type);
        Assert.Contains(new KeyValueItem("city", "İstanbul"), hash.Items);
        Assert.Null(hash.TimeToLive);
        Assert.True(hash.MemoryBytes > 0);

        var list = (await store.GetAsync(5, "queue", 2, Token))!;
        Assert.Equal(4, list.Length);
        Assert.Equal([new KeyValueItem("0", "a"), new KeyValueItem("1", "b")], list.Items);
        Assert.True(list.IsTruncated);

        var board = (await store.GetAsync(5, "board", 10, Token))!;
        Assert.Equal(new KeyValueItem(null, "veli", 20), board.Items[1]);
        var events = (await store.GetAsync(5, "events", 10, Token))!;
        Assert.Equal("kind=login\nuser=1", Assert.Single(events.Items).Value);
        Assert.InRange((await store.GetAsync(5, "temp", 1, Token))!.TimeToLive!.Value.TotalSeconds, 590, 600);
        Assert.Null(await store.GetAsync(5, "missing", 1, Token));
    }

    [Fact]
    public async Task Keys_and_items_are_changed_one_at_a_time()
    {
        await using var connection = await OpenAsync();
        await RunAsync(connection, "SELECT 6\nFLUSHDB\nRPUSH queue a b c b\nHSET h f1 v1\nSADD s x\nZADD z 1 m\nSET text old EX 100");
        var store = (IKeyValueStore)connection.Metadata;

        // A list element by index (the second "b", not the first).
        Assert.True(await store.ChangeAsync(6, new KeyChange("queue", KeyChangeKind.RemoveItem, KeyValueType.List) { OldItem = new("3", "b") }, Token));
        Assert.Equal(["a", "b", "c"], (await store.GetAsync(6, "queue", 10, Token))!.Items.Select(static i => i.Value));
        Assert.True(await store.ChangeAsync(6, new KeyChange("queue", KeyChangeKind.ReplaceItem, KeyValueType.List) { OldItem = new("0", "a"), Item = new("0", "A") }, Token));
        Assert.False(await store.ChangeAsync(6, new KeyChange("queue", KeyChangeKind.RemoveItem, KeyValueType.List) { OldItem = new("1", "changed meanwhile") }, Token));

        // A hash field renamed, a set member and a score changed.
        Assert.True(await store.ChangeAsync(6, new KeyChange("h", KeyChangeKind.ReplaceItem, KeyValueType.Hash) { OldItem = new("f1", "v1"), Item = new("f2", "v2") }, Token));
        Assert.Equal([new KeyValueItem("f2", "v2")], (await store.GetAsync(6, "h", 10, Token))!.Items);
        Assert.True(await store.ChangeAsync(6, new KeyChange("s", KeyChangeKind.ReplaceItem, KeyValueType.Set) { OldItem = new(null, "x"), Item = new(null, "y") }, Token));
        Assert.True(await store.ChangeAsync(6, new KeyChange("z", KeyChangeKind.AddItem, KeyValueType.SortedSet) { Item = new(null, "m", 5) }, Token));
        Assert.Equal(5, (await store.GetAsync(6, "z", 10, Token))!.Items[0].Score);

        // A new key by its first item; a string keeps its time to live when set.
        Assert.True(await store.ChangeAsync(6, new KeyChange("stream", KeyChangeKind.AddItem, KeyValueType.Stream) { Item = new(null, "a=1\nb=2") }, Token));
        Assert.True(await store.ChangeAsync(6, new KeyChange("text", KeyChangeKind.SetText) { Text = "yeni değer" }, Token));
        var text = (await store.GetAsync(6, "text", 1, Token))!;
        Assert.Equal("yeni değer", text.Text);
        Assert.NotNull(text.TimeToLive);

        Assert.True(await store.ChangeAsync(6, new KeyChange("text", KeyChangeKind.Expire), Token));
        Assert.Null((await store.GetAsync(6, "text", 1, Token))!.TimeToLive);
        Assert.False(await store.ChangeAsync(6, new KeyChange("text", KeyChangeKind.Rename) { NewKey = "h" }, Token));
        Assert.True(await store.ChangeAsync(6, new KeyChange("text", KeyChangeKind.Rename) { NewKey = "text2" }, Token));
        Assert.True(await store.ChangeAsync(6, new KeyChange("text2", KeyChangeKind.Delete), Token));
        Assert.Null(await store.GetAsync(6, "text2", 1, Token));
    }

    [Fact]
    public async Task A_binary_value_is_shown_in_hexadecimal()
    {
        await using var connection = await OpenAsync();
        await RunAsync(connection, "SELECT 7\nSET bin \"\\xff\\xfe\\x00\"");
        var value = await ((IKeyValueStore)connection.Metadata).GetAsync(7, "bin", 1, Token);
        Assert.Equal("0xFFFE00", value!.Text);
    }

    private async Task<IDatabaseConnection> OpenAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        return await new RedisProvider().OpenConnectionAsync(fixture.Profile, null, Token);
    }

    private static async Task<List<QueryEvent>> RunAsync(IDatabaseConnection connection, string text)
    {
        var events = new List<QueryEvent>();
        await foreach (var e in connection.Executor.ExecuteAsync(new QueryRequest(text), Token))
        {
            events.Add(e);
        }

        return events;
    }

    private static List<(IReadOnlyList<ResultColumn> Columns, List<object?[]> Rows)> Split(List<QueryEvent> events)
    {
        var sets = new List<(IReadOnlyList<ResultColumn>, List<object?[]>)>();
        foreach (var e in events)
        {
            switch (e)
            {
                case ResultSetStarted started:
                    sets.Add((started.Columns, []));
                    break;
                case RowBatch batch:
                    sets[^1].Item2.AddRange(batch.Rows);
                    break;
            }
        }

        return sets;
    }
}

/// <summary>The provider as the application loads it: an extension in a load context of its own.</summary>
public sealed class RedisExtensionTests
{
    [Fact]
    public async Task The_provider_loads_as_an_extension()
    {
        var providerDirectory = typeof(RedisExtensionTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(static a => a.Key == "ProviderDirectory").Value!;
        var options = new JetQueries.Core.Extensions.ExtensionHostOptions();
        options.Directories.Add(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(providerDirectory)))!);
        var host = new JetQueries.Core.Extensions.ExtensionHost(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<JetQueries.Core.Extensions.ExtensionHost>.Instance);

        var results = await host.LoadAsync(TestContext.Current.CancellationToken);

        var loaded = Assert.Single(results, static r => r.Manifest?.Id == "jetqueries.redis");
        Assert.True(loaded.State == JetQueries.Core.Extensions.ExtensionState.Loaded, loaded.Error);
        var provider = host.FindDatabaseProvider(RedisProvider.ProviderId)!;
        Assert.NotSame(System.Runtime.Loader.AssemblyLoadContext.Default, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(provider.GetType().Assembly));
        Assert.Equal("Redis", provider.DisplayName);
    }
}
