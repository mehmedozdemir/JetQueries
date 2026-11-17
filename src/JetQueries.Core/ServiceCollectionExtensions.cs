using JetQueries.Core.Extensions;
using JetQueries.Core.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JetQueries.Core;

/// <summary>Registers JetQueries core services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Adds the core services (job queue and extension host) as singletons.</summary>
    /// <param name="services">The service collection. Logging must be registered by the caller.</param>
    /// <param name="configureExtensions">Sets the directories the extension host searches; none by default.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddJetQueriesCore(this IServiceCollection services, Action<ExtensionHostOptions>? configureExtensions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IJobQueue>(static _ => new JobQueue());

        var options = new ExtensionHostOptions();
        configureExtensions?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<IExtensionHost, ExtensionHost>();
        return services;
    }
}
