using JetQueries.Core.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JetQueries.Core;

/// <summary>Registers JetQueries core services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Adds the core services (currently the job queue) as singletons.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddJetQueriesCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IJobQueue>(static _ => new JobQueue());
        return services;
    }
}
