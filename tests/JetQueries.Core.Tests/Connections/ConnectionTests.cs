using JetQueries.Core.Connections;
using JetQueries.Core.Extensions;
using JetQueries.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Core.Tests.Connections;

public sealed class JsonConnectionStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Definitions_round_trip_and_are_ordered_by_folder_and_name()
    {
        var path = NewPath();
        var production = Sample("Üretim Kartlar") with
        {
            Folder = "Müşteri A/Prod",
            Environment = ConnectionEnvironment.Production,
            IsReadOnly = true,
            UserName = "app_reader",
            SavePassword = true,
        };
        using (var store = Create(path))
        {
            await store.SaveAsync(Sample("zeta"), Token);
            await store.SaveAsync(production, Token);
            await store.SaveAsync(Sample("Alpha"), Token);
        }

        using var reopened = Create(path);
        var all = await reopened.GetAllAsync(Token);

        Assert.Equal(["Alpha", "zeta", "Üretim Kartlar"], all.Select(static c => c.Name));
        var loaded = all[2];
        Assert.Equal(production with { Settings = loaded.Settings }, loaded);
        Assert.Equal(production.Settings, loaded.Settings);
    }

    [Fact]
    public async Task Save_replaces_by_id_and_delete_removes()
    {
        var path = NewPath();
        using var store = Create(path);
        var first = Sample("one");
        var second = Sample("two");
        await store.SaveAsync(first, Token);
        await store.SaveAsync(second, Token);

        await store.SaveAsync(first with { Name = "renamed" }, Token);
        await store.DeleteAsync(second.Id, Token);
        await store.DeleteAsync(Guid.NewGuid(), Token);

        Assert.Equal("renamed", Assert.Single(await store.GetAllAsync(Token)).Name);
        using var reopened = Create(path);
        Assert.Equal("renamed", Assert.Single(await reopened.GetAllAsync(Token)).Name);
    }

    [Fact]
    public async Task The_file_has_no_place_for_a_password()
    {
        var path = NewPath();
        using var store = Create(path);
        await store.SaveAsync(Sample("x") with { UserName = "sa", SavePassword = true }, Token);

        var json = await File.ReadAllTextAsync(path, Token);

        Assert.DoesNotContain("\"password\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"savePassword\": true", json, StringComparison.Ordinal);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task An_unreadable_file_is_set_aside_not_overwritten()
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ this is not json", Token);
        using var store = Create(path);

        Assert.Empty(await store.GetAllAsync(Token));
        await store.SaveAsync(Sample("new"), Token);

        Assert.Equal("{ this is not json", await File.ReadAllTextAsync(path + ".unreadable", Token));
        Assert.Single(await store.GetAllAsync(Token));
    }

    [Fact]
    public async Task Without_a_path_definitions_live_in_memory()
    {
        using var store = Create(null);
        await store.SaveAsync(Sample("x"), Token);

        Assert.Single(await store.GetAllAsync(Token));
    }

    internal static ConnectionDefinition Sample(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = "fake",
        Settings = new Dictionary<string, string> { ["server"] = "sql01,1433", ["database"] = "Kartlar" },
    };

    private static JsonConnectionStore Create(string? path) =>
        new(new ConnectionStoreOptions { FilePath = path }, NullLogger<JsonConnectionStore>.Instance);

    private static string NewPath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "connection-store-tests", Guid.NewGuid().ToString("N"));
        return Path.Combine(directory, "connections.json");
    }
}

public sealed class CredentialStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Windows_credential_manager_stores_reads_replaces_and_deletes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Credential Manager only exists on Windows.");

        // A prefix of its own keeps the test away from real JetQueries entries; everything is deleted again.
        var store = new WindowsCredentialStore("JetQueries.Tests/connection/");
        var id = Guid.NewGuid();
        try
        {
            Assert.True(store.IsPersistent);
            Assert.Null(await store.GetPasswordAsync(id, Token));

            await store.SetPasswordAsync(id, "p@ss wörd ığüşöç \"';", Token);
            Assert.Equal("p@ss wörd ığüşöç \"';", await store.GetPasswordAsync(id, Token));

            await store.SetPasswordAsync(id, string.Empty, Token);
            Assert.Equal(string.Empty, await store.GetPasswordAsync(id, Token));
        }
        finally
        {
            await store.DeletePasswordAsync(id, Token);
        }

        Assert.Null(await store.GetPasswordAsync(id, Token));
        await store.DeletePasswordAsync(id, Token);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetPasswordAsync(id, new string('x', 2000), Token));
    }

    [Fact]
    public async Task Session_store_forgets_nothing_until_asked_and_is_not_persistent()
    {
        var store = new SessionCredentialStore();
        var id = Guid.NewGuid();

        await store.SetPasswordAsync(id, "secret", Token);

        Assert.False(store.IsPersistent);
        Assert.Equal("secret", await store.GetPasswordAsync(id, Token));
        await store.DeletePasswordAsync(id, Token);
        Assert.Null(await store.GetPasswordAsync(id, Token));
    }
}

public sealed class ConnectionManagerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_missing_provider_is_reported()
    {
        var manager = Create(new FakeProvider(), out _, out _);

        var attempt = await manager.TestAsync(JsonConnectionStoreTests.Sample("x") with { ProviderId = "oracle" }, "p", Token);

        Assert.Equal(ConnectionOutcome.ProviderMissing, attempt.Outcome);
        Assert.Equal("oracle", attempt.Detail);
    }

    [Fact]
    public async Task A_password_is_required_unless_given_stored_or_not_needed()
    {
        var provider = new FakeProvider();
        var manager = Create(provider, out var credentials, out _);
        var definition = JsonConnectionStoreTests.Sample("x") with { UserName = "app" };

        Assert.Equal(ConnectionOutcome.PasswordRequired, (await manager.TestAsync(definition, null, Token)).Outcome);
        Assert.Equal(0, provider.Opened);

        Assert.Equal(ConnectionOutcome.Succeeded, (await manager.TestAsync(definition, "typed", Token)).Outcome);
        Assert.Equal(("app", "typed"), provider.LastCredential);

        await credentials.SetPasswordAsync(definition.Id, "stored", Token);
        Assert.Equal(ConnectionOutcome.Succeeded, (await manager.TestAsync(definition, null, Token)).Outcome);
        Assert.Equal(("app", "stored"), provider.LastCredential);

        provider.NeedsCredential = false;
        await credentials.DeletePasswordAsync(definition.Id, Token);
        Assert.Equal(ConnectionOutcome.Succeeded, (await manager.TestAsync(definition, null, Token)).Outcome);
        Assert.Null(provider.LastCredential);
    }

    [Fact]
    public async Task An_unreachable_address_is_reported_without_calling_the_driver()
    {
        var provider = new FakeProvider();
        var manager = Create(provider, out _, out var probe);
        probe.Answer = "No answer from sql01:1433 within 4 s.";

        var attempt = await manager.TestAsync(JsonConnectionStoreTests.Sample("x"), "p", Token);

        Assert.Equal(ConnectionOutcome.Unreachable, attempt.Outcome);
        Assert.Equal(probe.Answer, attempt.Detail);
        Assert.Equal(new NetworkEndpoint("sql01", 1433), probe.Asked);
        Assert.Equal(0, provider.Opened);
    }

    [Fact]
    public async Task A_driver_error_is_reported_with_its_message_only()
    {
        var provider = new FakeProvider { OpenError = new InvalidOperationException("Login failed for user 'app'.") };
        var manager = Create(provider, out _, out _);

        var attempt = await manager.TestAsync(JsonConnectionStoreTests.Sample("x"), "s3cret", Token);

        Assert.Equal(ConnectionOutcome.Failed, attempt.Outcome);
        Assert.Equal("Login failed for user 'app'.", attempt.Detail);
        Assert.DoesNotContain("s3cret", attempt.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_is_not_swallowed()
    {
        var provider = new FakeProvider { OpenError = new OperationCanceledException() };
        var manager = Create(provider, out _, out _);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.TestAsync(JsonConnectionStoreTests.Sample("x"), "p", Token));
    }

    [Fact]
    public async Task Test_disconnects_again_and_open_keeps_one_session_per_connection()
    {
        var provider = new FakeProvider();
        var manager = Create(provider, out _, out _);
        var definition = JsonConnectionStoreTests.Sample("x");
        var changes = 0;
        manager.SessionsChanged += (_, _) => changes++;

        await manager.TestAsync(definition, "p", Token);
        Assert.Equal(1, provider.Disposed);
        Assert.Empty(manager.Sessions);

        var first = await manager.OpenAsync(definition, "p", Token);
        var second = await manager.OpenAsync(definition, "p", Token);

        Assert.Equal(ConnectionOutcome.Succeeded, first.Outcome);
        Assert.Same(first.Session, second.Session);
        Assert.Same(first.Session, Assert.Single(manager.Sessions));
        Assert.Equal(2, provider.Opened);
        Assert.Equal(1, changes);

        await manager.CloseAsync(definition.Id);
        await manager.CloseAsync(definition.Id);

        Assert.Empty(manager.Sessions);
        Assert.Equal(2, provider.Disposed);
        Assert.Equal(2, changes);
    }

    private static ConnectionManager Create(FakeProvider provider, out SessionCredentialStore credentials, out FakeProbe probe)
    {
        credentials = new SessionCredentialStore();
        probe = new FakeProbe();
        return new ConnectionManager(new FakeExtensions(provider), credentials, probe);
    }

    private sealed class FakeProbe : IReachabilityProbe
    {
        public string? Answer { get; set; }

        public NetworkEndpoint? Asked { get; private set; }

        public Task<string?> ProbeAsync(NetworkEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Asked = endpoint;
            return Task.FromResult(Answer);
        }
    }

    private sealed class FakeExtensions(IDatabaseProvider provider) : IExtensionHost
    {
        public IReadOnlyList<ExtensionLoadResult> Extensions => [];

        public IReadOnlyCollection<IDatabaseProvider> DatabaseProviders => [provider];

        public Task<IReadOnlyList<ExtensionLoadResult>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Extensions);

        public IDatabaseProvider? FindDatabaseProvider(string id) => id == provider.Id ? provider : null;
    }

    private sealed class FakeProvider : IDatabaseProvider, IConnectionSettingsProvider
    {
        public bool NeedsCredential { get; set; } = true;

        public Exception? OpenError { get; set; }

        public int Opened { get; private set; }

        public int Disposed { get; private set; }

        public (string? User, string? Password)? LastCredential { get; private set; }

        public string Id => "fake";

        public string DisplayName => "Fake";

        public ProviderCapabilities Capabilities => ProviderCapabilities.None;

        public ISqlLanguageService? LanguageService => null;

        public IReadOnlyList<ConnectionSettingDescriptor> ConnectionSettings => [];

        public string QuoteIdentifier(string identifier) => identifier;

        public bool RequiresCredential(IReadOnlyDictionary<string, string> settings) => NeedsCredential;

        public NetworkEndpoint? GetEndpoint(IReadOnlyDictionary<string, string> settings) => new("sql01", 1433);

        public Task<IDatabaseConnection> OpenConnectionAsync(ConnectionProfile profile, ConnectionCredential? credential, CancellationToken cancellationToken)
        {
            if (OpenError is not null)
            {
                return Task.FromException<IDatabaseConnection>(OpenError);
            }

            Opened++;
            LastCredential = credential is null ? null : (credential.UserName, credential.Password);
            return Task.FromResult<IDatabaseConnection>(new FakeConnection(this));
        }

        private sealed class FakeConnection(FakeProvider owner) : IDatabaseConnection
        {
            public IQueryExecutor Executor => throw new NotSupportedException();

            public IMetadataProvider Metadata => throw new NotSupportedException();

            public ValueTask DisposeAsync()
            {
                owner.Disposed++;
                return ValueTask.CompletedTask;
            }
        }
    }
}
