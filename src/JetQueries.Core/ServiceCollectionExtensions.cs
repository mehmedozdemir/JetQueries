using JetQueries.Core.Connections;
using JetQueries.Core.Extensions;
using JetQueries.Core.Jobs;
using JetQueries.Core.Metadata;
using JetQueries.Core.Queries;
using JetQueries.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JetQueries.Core;

/// <summary>Registers JetQueries core services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Adds the core services (job queue, extension host, metadata cache) as singletons.</summary>
    /// <param name="services">The service collection. Logging must be registered by the caller.</param>
    /// <param name="configureExtensions">Sets the directories the extension host searches; none by default.</param>
    /// <param name="configureMetadataCache">Sets the metadata cache file; memory-only by default.</param>
    /// <param name="configureConnectionStore">Sets the connection definitions file; memory-only by default.</param>
    /// <param name="configureQueryHistory">Sets the query history file and its limits; memory-only by default.</param>
    /// <param name="configureSettings">Sets the settings folder; memory-only by default.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddJetQueriesCore(
        this IServiceCollection services,
        Action<ExtensionHostOptions>? configureExtensions = null,
        Action<MetadataCacheOptions>? configureMetadataCache = null,
        Action<ConnectionStoreOptions>? configureConnectionStore = null,
        Action<QueryHistoryOptions>? configureQueryHistory = null,
        Action<SettingsStoreOptions>? configureSettings = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IJobQueue>(static _ => new JobQueue());

        var options = new ExtensionHostOptions();
        configureExtensions?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<IExtensionHost, ExtensionHost>();

        var cacheOptions = new MetadataCacheOptions();
        configureMetadataCache?.Invoke(cacheOptions);
        services.TryAddSingleton(cacheOptions);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMetadataCache, SqliteMetadataCache>();
        services.TryAddSingleton<IMetadataService, MetadataService>();

        var connectionOptions = new ConnectionStoreOptions();
        configureConnectionStore?.Invoke(connectionOptions);
        services.TryAddSingleton(connectionOptions);
        services.TryAddSingleton<IConnectionStore, JsonConnectionStore>();
        services.TryAddSingleton<ICredentialStore>(static _ =>
            OperatingSystem.IsWindows() ? new WindowsCredentialStore() : new SessionCredentialStore());
        services.TryAddSingleton<IReachabilityProbe, TcpReachabilityProbe>();
        // SSH server keys seen before live next to the connections (not secret).
        services.TryAddSingleton(new SshKnownHosts(
            connectionOptions.FilePath is { } file ? Path.Combine(Path.GetDirectoryName(file) ?? ".", "ssh_known_hosts.json") : null));
        services.TryAddSingleton<IConnectionManager, ConnectionManager>();

        var historyOptions = new QueryHistoryOptions();
        configureQueryHistory?.Invoke(historyOptions);
        services.TryAddSingleton(historyOptions);
        services.TryAddSingleton<IQueryHistory, SqliteQueryHistory>();

        var settingsOptions = new SettingsStoreOptions();
        configureSettings?.Invoke(settingsOptions);
        if (settingsOptions.Protector is null && OperatingSystem.IsWindows())
        {
            settingsOptions.Protector = new WindowsDataProtector();
        }

        services.TryAddSingleton(settingsOptions);
        services.TryAddSingleton<ISettingsStore, JsonSettingsStore>();
        return services;
    }
}
