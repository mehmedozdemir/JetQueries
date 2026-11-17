using JetQueries.Core.Connections;
using JetQueries.Core.Extensions;
using JetQueries.Core.Jobs;
using JetQueries.Desktop.Services;
using JetQueries.Desktop.ViewModels;
using JetQueries.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Desktop.Tests;

/// <summary>Real Core services around a scripted provider: everything the connection view models talk to.</summary>
internal sealed class TestEnvironment : IAsyncDisposable
{
    public TestEnvironment()
    {
        Manager = new ConnectionManager(new FakeExtensions(Provider), Credentials, Probe);
    }

    public FakeProvider Provider { get; } = new();

    public FakeProbe Probe { get; } = new();

    public FakeDialogs Dialogs { get; } = new();

    public JsonConnectionStore Store { get; } = new(new ConnectionStoreOptions(), NullLogger<JsonConnectionStore>.Instance);

    public SessionCredentialStore Credentials { get; } = new();

    public ConnectionManager Manager { get; }

    public JobQueue Jobs { get; } = new();

    public ConnectionEditorViewModel Editor(ConnectionDefinition? existing = null) =>
        new(existing, [Provider], Store, Credentials, Manager, Jobs);

    public ConnectionsViewModel Connections() => new(Store, Manager, Credentials, Jobs, Dialogs);

    public static ConnectionDefinition Definition(string name, string folder = "", ConnectionEnvironment environment = ConnectionEnvironment.Unspecified) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = "fake",
        Settings = new Dictionary<string, string> { ["server"] = "sql01" },
        Folder = folder,
        Environment = environment,
        UserName = "app",
        SavePassword = true,
    };

    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync();
        await Jobs.DisposeAsync();
        Store.Dispose();
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public bool EditResult { get; set; }

    public string? Password { get; set; }

    public bool Confirm { get; set; }

    public List<string> Asked { get; } = [];

    public Task<bool> EditConnectionAsync(ConnectionDefinition? existing)
    {
        Asked.Add("edit:" + (existing?.Name ?? "new"));
        return Task.FromResult(EditResult);
    }

    public Task<string?> PromptPasswordAsync(string connectionName)
    {
        Asked.Add("password:" + connectionName);
        return Task.FromResult(Password);
    }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        Asked.Add("confirm:" + message);
        return Task.FromResult(Confirm);
    }
}

internal sealed class FakeProbe : IReachabilityProbe
{
    public string? Answer { get; set; }

    public Task<string?> ProbeAsync(NetworkEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(Answer);
}

internal sealed class FakeExtensions(IDatabaseProvider provider) : IExtensionHost
{
    public IReadOnlyList<ExtensionLoadResult> Extensions => [];

    public IReadOnlyCollection<IDatabaseProvider> DatabaseProviders => [provider];

    public Task<IReadOnlyList<ExtensionLoadResult>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Extensions);

    public IDatabaseProvider? FindDatabaseProvider(string id) => id == provider.Id ? provider : null;
}

/// <summary>A provider with SQL Server-like settings that connects to nothing.</summary>
internal sealed class FakeProvider : IDatabaseProvider, IConnectionSettingsProvider
{
    public string? OpenError { get; set; }

    public (string? User, string? Password)? LastCredential { get; private set; }

    public string Id => "fake";

    public string DisplayName => "SQL Server";

    public ProviderCapabilities Capabilities => ProviderCapabilities.None;

    public ISqlLanguageService? LanguageService => null;

    public IReadOnlyList<ConnectionSettingDescriptor> ConnectionSettings { get; } =
    [
        new("server", "Server", ConnectionSettingKind.Text) { IsRequired = true, Hint = "host, host,port or host\\instance" },
        new("database", "Database", ConnectionSettingKind.Text),
        new("authentication", "Authentication", ConnectionSettingKind.Choice)
        {
            DefaultValue = "sql",
            Options = [new("sql", "SQL Server login"), new("windows", "Windows (integrated)")],
        },
        new("trustServerCertificate", "Trust server certificate", ConnectionSettingKind.Boolean) { DefaultValue = "false", Hint = "needed for self-signed certificates" },
        new("connectTimeoutSeconds", "Connect timeout (s)", ConnectionSettingKind.Number) { DefaultValue = "15" },
    ];

    public string QuoteIdentifier(string identifier) => identifier;

    public bool RequiresCredential(IReadOnlyDictionary<string, string> settings) => settings.GetValueOrDefault("authentication", "sql") != "windows";

    public NetworkEndpoint? GetEndpoint(IReadOnlyDictionary<string, string> settings) => new("sql01", 1433);

    public Task<IDatabaseConnection> OpenConnectionAsync(ConnectionProfile profile, ConnectionCredential? credential, CancellationToken cancellationToken)
    {
        LastCredential = credential is null ? null : (credential.UserName, credential.Password);
        return OpenError is null
            ? Task.FromResult<IDatabaseConnection>(new FakeConnection())
            : Task.FromException<IDatabaseConnection>(new InvalidOperationException(OpenError));
    }

    private sealed class FakeConnection : IDatabaseConnection
    {
        public IQueryExecutor Executor => throw new NotSupportedException();

        public IMetadataProvider Metadata => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
