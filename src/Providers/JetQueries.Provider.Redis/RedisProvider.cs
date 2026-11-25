using System.Globalization;
using JetQueries.Sdk;
using StackExchange.Redis;

namespace JetQueries.Provider.Redis;

/// <summary>Entry point of the Redis extension.</summary>
public sealed class RedisExtension : IExtension
{
    /// <inheritdoc />
    public void Initialize(IExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterDatabaseProvider(new RedisProvider());
    }
}

/// <summary>The setting keys of a Redis connection.</summary>
public static class RedisConnectionSettings
{
    /// <summary>The server.</summary>
    public const string Host = "host";

    /// <summary>Its port (6379).</summary>
    public const string Port = "port";

    /// <summary>The logical database a window starts in.</summary>
    public const string Database = "database";

    /// <summary>none, password (requirepass) or acl (user and password).</summary>
    public const string Authentication = "auth";

    /// <summary>TLS.</summary>
    public const string Tls = "tls";
}

/// <summary>
/// Database provider for Redis, on StackExchange.Redis: the explorer lists logical databases and keys (with
/// <c>SCAN</c>) through <see cref="IKeyValueStore"/>, a query tab is the command console.
/// </summary>
public sealed class RedisProvider : IDatabaseProvider, IConnectionSettingsProvider, IProviderAppearance, IStatementRiskAnalyzer
{
    /// <summary>The provider identifier stored in connection profiles.</summary>
    public const string ProviderId = "redis";

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public string DisplayName => "Redis";

    /// <inheritdoc />
    public ProviderCapabilities Capabilities => ProviderCapabilities.MultipleDatabases;

    /// <inheritdoc />
    public ISqlLanguageService? LanguageService { get; } = new RedisLanguageService();

    /// <summary>The product's logo: Simple Icons 9.0.0 (CC0-1.0), for recognition only; the trademark is its owner's.</summary>
    public string IconPathData => "M10.5 2.661l.54.997-1.797.644 2.409.218.748 1.246.467-1.121 2.077-.208-1.61-.613.426-1.017-1.578.519zm6.905 2.077L13.76 6.182l3.292 1.298.353-.146 3.293-1.298zm-10.51.312a2.97 1.153 0 0 0-2.97 1.152 2.97 1.153 0 0 0 2.97 1.153 2.97 1.153 0 0 0 2.97-1.153 2.97 1.153 0 0 0-2.97-1.152zM24 6.805s-8.983 4.278-10.395 4.953c-1.226.561-1.901.561-3.261.094C8.318 11.022 0 7.241 0 7.241v1.038c0 .24.332.499.966.8 1.277.613 8.34 3.677 9.45 4.206 1.112.53 1.9.54 3.313-.197 1.412-.738 8.049-3.905 9.326-4.57.654-.342.945-.602.945-.84zm-10.042.602L8.39 8.26l3.884 1.61zM24 10.637s-8.983 4.279-10.395 4.954c-1.226.56-1.901.56-3.261.093C8.318 14.854 0 11.074 0 11.074v1.038c0 .238.332.498.966.8 1.277.612 8.34 3.676 9.45 4.205 1.112.53 1.9.54 3.313-.197 1.412-.737 8.049-3.905 9.326-4.57.654-.332.945-.602.945-.84zm0 3.842l-10.395 4.954c-1.226.56-1.901.56-3.261.094C8.318 18.696 0 14.916 0 14.916v1.038c0 .239.332.499.966.8 1.277.613 8.34 3.676 9.45 4.206 1.112.53 1.9.54 3.313-.198 1.412-.737 8.049-3.904 9.326-4.569.654-.343.945-.613.945-.841z";

    /// <inheritdoc />
    public string AccentColor => "#DC382D";

    /// <inheritdoc />
    public string Description => "Key-value store";

    /// <inheritdoc />
    public IReadOnlyList<ConnectionSettingDescriptor> ConnectionSettings { get; } =
    [
        new(RedisConnectionSettings.Host, "Host", ConnectionSettingKind.Text) { IsRequired = true, DefaultValue = "localhost" },
        new(RedisConnectionSettings.Port, "Port", ConnectionSettingKind.Number) { DefaultValue = "6379" },
        new(RedisConnectionSettings.Database, "Database", ConnectionSettingKind.Number) { DefaultValue = "0", Hint = "0–15 on a default server" },
        new(RedisConnectionSettings.Authentication, "Authentication", ConnectionSettingKind.Choice)
        {
            DefaultValue = "none",
            Options = [new("none", "None"), new("password", "Password"), new("acl", "User and password (ACL)")],
        },
        new(RedisConnectionSettings.Tls, "Use TLS", ConnectionSettingKind.Boolean) { DefaultValue = "false" },
    ];

    /// <inheritdoc />
    public bool RequiresCredential(IReadOnlyDictionary<string, string> settings) => Authentication(settings) != "none";

    /// <inheritdoc />
    public NetworkEndpoint? GetEndpoint(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.TryGetValue(RedisConnectionSettings.Host, out var host) && host.Trim().Length > 0 ? new NetworkEndpoint(host.Trim(), Port(settings)) : null;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string>? WithEndpoint(IReadOnlyDictionary<string, string> settings, NetworkEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(endpoint);
        return new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase)
        {
            [RedisConnectionSettings.Host] = endpoint.Host,
            [RedisConnectionSettings.Port] = endpoint.Port.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return identifier.Length > 0 && identifier.All(static c => c > ' ' && c != '"' && c != '\'' && c != '\\' && c < 127)
            ? identifier
            : "\"" + identifier.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal) + "\"";
    }

    /// <inheritdoc />
    public StatementRiskInfo AnalyzeRisk(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var writes = false;
        var destructive = new List<string>();
        IReadOnlyList<RedisCommandLine> commands;
        try
        {
            commands = RedisCommandParser.Parse(text);
        }
        catch (FormatException)
        {
            // It will not run anyway; reading it as writing keeps a read-only connection safe.
            return new StatementRiskInfo(true, []);
        }

        foreach (var command in commands)
        {
            var effect = RedisCommands.EffectOf(command.Words);
            writes |= effect is RedisCommandEffect.Writes or RedisCommandEffect.Destroys;
            if (effect == RedisCommandEffect.Destroys && RedisCommands.NameOf(command.Words) is var name && !destructive.Contains(name))
            {
                destructive.Add(name);
            }
        }

        return new StatementRiskInfo(writes, destructive);
    }

    /// <inheritdoc />
    public async Task<IDatabaseConnection> OpenConnectionAsync(ConnectionProfile profile, ConnectionCredential? credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var settings = profile.Settings;
        var endpoint = GetEndpoint(settings) ?? throw new ArgumentException("The Redis connection has no host.", nameof(profile));
        var options = new ConfigurationOptions
        {
            EndPoints = { { endpoint.Host, endpoint.Port } },
            AbortOnConnectFail = true,
            ConnectTimeout = 10_000,
            SyncTimeout = 30_000,
            AsyncTimeout = 30_000,

            // The console is the user's own tool: INFO, CONFIG, CLIENT and FLUSHDB are theirs to run (on production
            // they are confirmed first, on a read-only connection refused).
            AllowAdmin = true,
            ClientName = "JetQueries",
            Ssl = settings.TryGetValue(RedisConnectionSettings.Tls, out var tls) && bool.TryParse(tls, out var useTls) && useTls,
            DefaultDatabase = Database(settings),
        };
        switch (Authentication(settings))
        {
            case "password":
                options.Password = credential?.Password;
                break;
            case "acl":
                options.User = credential?.UserName;
                options.Password = credential?.Password;
                break;
        }

        try
        {
            var multiplexer = await ConnectionMultiplexer.ConnectAsync(options).WaitAsync(cancellationToken).ConfigureAwait(false);
            return new RedisConnection(multiplexer, options.DefaultDatabase ?? 0);
        }
        catch (RedisConnectionException ex)
        {
            // The driver's message lists its whole configuration; the user needs the reason only.
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"Could not connect to Redis at {endpoint.Host}:{endpoint.Port}: {ex.FailureType}") +
                (ex.InnerException is { } inner ? " — " + inner.Message : string.Empty),
                ex);
        }
    }

    private static string Authentication(IReadOnlyDictionary<string, string> settings) =>
        settings.TryGetValue(RedisConnectionSettings.Authentication, out var auth) && auth is "password" or "acl" ? auth : "none";

    private static int Port(IReadOnlyDictionary<string, string> settings) =>
        settings.TryGetValue(RedisConnectionSettings.Port, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536 ? port : 6379;

    private static int Database(IReadOnlyDictionary<string, string> settings) =>
        settings.TryGetValue(RedisConnectionSettings.Database, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var database) && database >= 0 ? database : 0;
}
