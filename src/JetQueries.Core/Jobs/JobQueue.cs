using System.Threading.Channels;

namespace JetQueries.Core.Jobs;

/// <summary>
/// <see cref="IJobQueue"/> on a prioritized <see cref="Channel"/>, drained by a fixed number of worker loops on
/// the thread pool.
/// </summary>
public sealed class JobQueue : IJobQueue, IAsyncDisposable, IDisposable
{
    private readonly Channel<QueuedJob> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private long _sequence;
    private int _disposed;

    /// <summary>Creates the queue and starts its workers.</summary>
    /// <param name="options">Queue options, or <see langword="null"/> for the defaults.</param>
    public JobQueue(JobQueueOptions? options = null)
    {
        var maxConcurrency = (options ?? new JobQueueOptions()).MaxConcurrency;
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1, nameof(options));

        _channel = Channel.CreateUnboundedPrioritized(new UnboundedPrioritizedChannelOptions<QueuedJob>
        {
            Comparer = QueuedJobComparer.Instance,
        });

        _workers = new Task[maxConcurrency];
        for (var i = 0; i < _workers.Length; i++)
        {
            _workers[i] = Task.Run(WorkerLoopAsync);
        }
    }

    /// <inheritdoc />
    public JobHandle<TResult> Enqueue<TResult>(
        string name,
        Func<JobContext, Task<TResult>> work,
        JobPriority priority = JobPriority.Normal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var job = new JobHandle<TResult>(name, priority, work, cancellationToken, _shutdown.Token);
        if (!_channel.Writer.TryWrite(new QueuedJob(job, Interlocked.Increment(ref _sequence))))
        {
            // Lost a race with shutdown: the writer was completed after the check above.
            job.Cancel();
            throw new ObjectDisposedException(nameof(JobQueue));
        }

        return job;
    }

    /// <inheritdoc />
    public JobHandle Enqueue(
        string name,
        Func<JobContext, Task> work,
        JobPriority priority = JobPriority.Normal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Enqueue<object?>(
            name,
            async context =>
            {
                await work(context).ConfigureAwait(false);
                return null;
            },
            priority,
            cancellationToken);
    }

    /// <summary>Cancels queued and running jobs and waits for the workers to finish.</summary>
    /// <returns>A task that completes when all workers have stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (BeginShutdown())
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
            _shutdown.Dispose();
        }
    }

    /// <summary>
    /// Cancels queued and running jobs without waiting for the workers; they stop on their own once the running
    /// jobs observe cancellation. Prefer <see cref="DisposeAsync"/>.
    /// </summary>
    public void Dispose() => BeginShutdown();

    private bool BeginShutdown()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return false;
        }

        _channel.Writer.TryComplete();
        _shutdown.Cancel();
        return true;
    }

    private async Task WorkerLoopAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await item.Job.RunAsync().ConfigureAwait(false);
        }
    }

    private readonly record struct QueuedJob(JobHandle Job, long Sequence);

    private sealed class QueuedJobComparer : IComparer<QueuedJob>
    {
        public static readonly QueuedJobComparer Instance = new();

        public int Compare(QueuedJob x, QueuedJob y)
        {
            // Higher priority first; within a priority, submission order (the heap alone is not FIFO).
            var byPriority = ((int)y.Job.Priority).CompareTo((int)x.Job.Priority);
            return byPriority != 0 ? byPriority : x.Sequence.CompareTo(y.Sequence);
        }
    }
}
