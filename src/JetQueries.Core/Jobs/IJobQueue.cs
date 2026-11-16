namespace JetQueries.Core.Jobs;

/// <summary>
/// The single entry point for background work. Every database call, parse, metadata load and export goes through
/// here so that it has a priority, reports progress, can be canceled, and never runs on the UI thread.
/// </summary>
public interface IJobQueue
{
    /// <summary>Queues work that produces a result.</summary>
    /// <typeparam name="TResult">Type of the result.</typeparam>
    /// <param name="name">Human-readable name for progress UI and logs.</param>
    /// <param name="work">The work. It must honor <see cref="JobContext.CancellationToken"/>.</param>
    /// <param name="priority">Scheduling priority.</param>
    /// <param name="cancellationToken">Cancels the job, in addition to <see cref="JobHandle.Cancel"/>.</param>
    /// <returns>A handle to observe, await and cancel the job.</returns>
    /// <exception cref="ObjectDisposedException">The queue has been shut down.</exception>
    JobHandle<TResult> Enqueue<TResult>(
        string name,
        Func<JobContext, Task<TResult>> work,
        JobPriority priority = JobPriority.Normal,
        CancellationToken cancellationToken = default);

    /// <summary>Queues work that produces no result.</summary>
    /// <param name="name">Human-readable name for progress UI and logs.</param>
    /// <param name="work">The work. It must honor <see cref="JobContext.CancellationToken"/>.</param>
    /// <param name="priority">Scheduling priority.</param>
    /// <param name="cancellationToken">Cancels the job, in addition to <see cref="JobHandle.Cancel"/>.</param>
    /// <returns>A handle to observe, await and cancel the job.</returns>
    /// <exception cref="ObjectDisposedException">The queue has been shut down.</exception>
    JobHandle Enqueue(
        string name,
        Func<JobContext, Task> work,
        JobPriority priority = JobPriority.Normal,
        CancellationToken cancellationToken = default);
}
