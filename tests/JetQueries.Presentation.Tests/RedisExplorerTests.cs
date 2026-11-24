using System.Globalization;
using JetQueries.Core.Connections;
using JetQueries.Core.Queries;
using JetQueries.Provider.Redis;
using JetQueries.Sdk;
using Testcontainers.Redis;

namespace JetQueries.Presentation.Tests;

/// <summary>A Redis container for the explorer tests (opt-in: JETQUERIES_REDIS_TESTS=Y).</summary>
public sealed class RedisExplorerFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public string? SkipReason { get; private set; }

    public Dictionary<string, string> Settings { get; } = [];

    public async ValueTask InitializeAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("JETQUERIES_REDIS_TESTS"), "Y", StringComparison.OrdinalIgnoreCase))
        {
            SkipReason = "Set JETQUERIES_REDIS_TESTS=Y to start a Redis container and run the explorer tests.";
            return;
        }

        try
        {
            _container = new RedisBuilder("redis:7-alpine").Build();
            await _container.StartAsync();
        }
#pragma warning disable CA1031 // Any failure to start Docker means "skip".
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SkipReason = $"Docker is not available: {ex.Message}";
            return;
        }

        Settings["host"] = _container.Hostname;
        Settings["port"] = _container.GetMappedPublicPort(6379).ToString(CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>The explorer's keys and the key tab, on a real Redis.</summary>
public sealed class RedisExplorerTests(RedisExplorerFixture fixture) : IClassFixture<RedisExplorerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Keys_are_grouped_by_their_separator()
    {
        var keys = new[] { "user:1:name", "user:1:mail", "user:2:name", "user:1", "session:abc", "plain", "trailing:" };

        var nodes = KeyValueTree.Group(new ExplorerContext(null!, null!, null!), 0, keys, string.Empty, ':');

        Assert.Equal(["session (1)", "user (4)", "plain", "trailing:"], nodes.Select(static n => n.Name));
        var user = Assert.IsType<KeyFolderNodeViewModel>(nodes[1]);
        user.IsExpanded = true;
        Assert.Equal(["1 (2)", "2 (1)", "1"], user.Children.Select(static n => n.Name));
        Assert.Equal("user:1", Assert.IsType<KeyNodeViewModel>(user.Children[2]).Key);
    }

    [Fact]
    public async Task The_explorer_lists_databases_and_keys_and_a_key_opens_with_its_value()
    {
        await using var env = new TestEnvironment();
        var (manager, session) = await OpenAsync(env);
        await using var _ = manager;
        await RunAsync(session, "SELECT 2\nFLUSHDB\nHSET user:1 name Ayşe city İstanbul\nSET user:2:name Mehmet\nRPUSH queue a b\nSET config:mode \"{\\\"a\\\":1}\"");

        var context = new ExplorerContext(session, env.Metadata, env.Jobs);
        var roots = await KeyValueTree.LoadRootAsync(context);
        var db2 = Assert.Single(roots!.OfType<KeySpaceNodeViewModel>(), static n => n.Database == 2);
        Assert.Equal(4, db2.KeyCount);
        Assert.True(session.IsKeyValue);

        await db2.LoadAsync();
        Assert.Equal(["config (1)", "user (2)", "queue"], db2.Children.Select(static n => n.Name));

        // A pattern narrows the database.
        await db2.FilterAsync("user:*");
        Assert.Equal(KeysFilteredText("user:*"), db2.Children[0].Name);
        Assert.Equal("user (2)", db2.Children[1].Name);

        // The key tab: a hash, edited one field at a time.
        var key = new KeyValueViewModel(session, 2, "user:1", env.Jobs, env.Dialogs);
        await key.RefreshAsync();
        Assert.Equal("hash", key.TypeText);
        Assert.Equal(2, key.Items.Count);
        key.SelectedItem = key.Items.Single(static i => i.Name == "city");
        Assert.Equal("İstanbul", key.EditValue);
        key.EditValue = "Ankara";
        await key.SaveItemCommand.ExecuteAsync(null);
        Assert.False(key.Failed, key.Status);
        Assert.Contains(key.Items, static i => i is { Name: "city", Preview: "Ankara" });
        key.EditName = "phone";
        key.EditValue = "0532";
        await key.AddItemCommand.ExecuteAsync(null);
        Assert.Equal(3, key.Items.Count);

        // Time to live, rename (asks the name), delete (asks first).
        env.Dialogs.TextAnswers.Enqueue("600");
        await key.SetTimeToLiveCommand.ExecuteAsync(null);
        Assert.NotEqual(Strings.KeyTtl + " " + Strings.KeyNoTtl, key.TtlText);
        env.Dialogs.TextAnswers.Enqueue("user:1:profile");
        await key.RenameCommand.ExecuteAsync(null);
        Assert.Equal("user:1:profile", key.Key);
        env.Dialogs.Confirm = true;
        await key.DeleteCommand.ExecuteAsync(null);
        Assert.False(key.Exists);

        // A string holding JSON is indented on request and saved.
        var text = new KeyValueViewModel(session, 2, "config:mode", env.Jobs, env.Dialogs);
        await text.RefreshAsync();
        Assert.True(text.IsText);
        text.FormatJsonCommand.Execute(null);
        Assert.Contains("\n", text.Text, StringComparison.Ordinal);
        await text.SaveTextCommand.ExecuteAsync(null);
        Assert.Equal(Strings.KeySaved, text.Status);
    }

    [Fact]
    public async Task A_read_only_connection_refuses_writes_in_the_console_and_the_key_tab()
    {
        await using var env = new TestEnvironment();
        var (manager, session) = await OpenAsync(env, readOnly: true);
        await using var _ = manager;

        await Assert.ThrowsAsync<ReadOnlyConnectionException>(() => RunAsync(session, "SET x 1"));
        await RunAsync(session, "GET x\nSCAN 0");

        var key = new KeyValueViewModel(session, 0, "x", env.Jobs, env.Dialogs);
        Assert.False(key.SaveTextCommand.CanExecute(null));
        Assert.True(key.IsReadOnly);
    }

    private static string KeysFilteredText(string pattern) => string.Format(CultureInfo.CurrentCulture, System.Text.CompositeFormat.Parse(Strings.KeysFiltered), pattern);

    private async Task<(ConnectionManager Manager, ConnectionSession Session)> OpenAsync(TestEnvironment env, bool readOnly = false)
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        var manager = new ConnectionManager(new FakeExtensions(new RedisProvider()), new SessionCredentialStore(), env.Probe);
        var definition = new ConnectionDefinition { Id = Guid.NewGuid(), Name = "redis", ProviderId = RedisProvider.ProviderId, Settings = fixture.Settings, IsReadOnly = readOnly };
        var attempt = await manager.OpenAsync(definition, null, Token);
        Assert.Equal(ConnectionOutcome.Succeeded, attempt.Outcome);
        return (manager, attempt.Session!);
    }

    private static async Task RunAsync(ConnectionSession session, string text)
    {
        await foreach (var _ in session.ExecuteAsync(new QueryRequest(text), Token))
        {
        }
    }
}
