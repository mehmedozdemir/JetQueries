using JetQueries.Core.Extensions;
using JetQueries.Core.Jobs;
using JetQueries.Desktop.ViewModels;
using Microsoft.Extensions.Hosting;

namespace JetQueries.Desktop.Services;

/// <summary>
/// Starts loading extensions as a background job when the application starts. The window is already visible
/// by then; nothing waits for the result.
/// </summary>
internal sealed class ExtensionStartupService(IJobQueue jobs, IExtensionHost extensions, MainWindowViewModel shell) : IHostedService
{
    private JobHandle? _job;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _job = jobs.Enqueue(
            "Load extensions",
            async context =>
            {
                var results = await extensions.LoadAsync(context.CancellationToken).ConfigureAwait(false);
                var loaded = results.Count(static r => r.State == ExtensionState.Loaded);
                shell.ShowExtensions(loaded, results.Count - loaded);
            },
            JobPriority.High,
            CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _job?.Cancel();
        return Task.CompletedTask;
    }
}
