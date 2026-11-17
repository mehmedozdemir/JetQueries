using JetQueries.Core.Connections;
using JetQueries.Core.Extensions;
using JetQueries.Core.Jobs;
using JetQueries.Core.Metadata;
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
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddJetQueriesCore(
        this IServiceCollection services,
        Action<ExtensionHostOptions>? configureExtensions = null,
        Action<MetadataCacheOptions>? configureMetadataCache = null,
        Action<ConnectionStoreOptions>? configureConnectionStore = null)
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
        services.TryAddSingleton<IConnectionManager, ConnectionManager>();
        return services;
    }
}
