using System.Runtime.CompilerServices;

namespace JetQueries.Core.Jobs;

/// <summary>
/// A job submitted to an <see cref="IJobQueue"/>: observe its status and progress, await its completion, cancel it.
/// All members are thread-safe.
/// </summary>
public abstract class JobHandle
{
    private readonly CancellationTokenSource _cts;
    private readonly CancellationTokenRegistration _cancelRegistration;
    private int _status = (int)JobStatus.Queued;
    private int _cancelRequested;
    private StrongBox<JobProgress>? _progress; // Boxed so readers never see a torn struct.

    private protected JobHandle(string name, JobPriority priority, CancellationToken callerToken, CancellationToken shutdownToken)
    {
        Name = name;
        Priority = priority;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, shutdownToken);

        // A job canceled while still queued completes immediately instead of waiting for a worker to skip it.
        _cancelRegistration = _cts.Token.UnsafeRegister(static state => ((JobHandle)state!).CancelIfQueued(), this);
    }

    /// <summary>Unique identifier of the job.</summary>
    public Guid Id { get; } = Guid.CreateVersion7();

    /// <summary>Human-readable name, shown in progress UI and logs. Never put secrets in it.</summary>
    public string Name { get; }

    /// <summary>Scheduling priority.</summary>
    public JobPriority Priority { get; }

    /// <summary>Current lifecycle state.</summary>
    public JobStatus Status => (JobStatus)Volatile.Read(ref _status);

    /// <summary>The most recent progress report, or <see langword="null"/> if none was made.</summary>
    public JobProgress? Progress => Volatile.Read(ref _progress)?.Value;

    /// <summary>
    /// Completes when the job reaches a final state. Faults with the job's exception on failure and is canceled
    /// on cancellation. <see cref="Status"/> is already final when this task completes.
    /// </summary>
    public Task Completion => CompletionTask;

    /// <summary>
    /// Raised on the job's thread (never the UI thread) for every progress report. Handlers must be fast and
    /// must marshal to the UI thread themselves.
    /// </summary>
    public event EventHandler<JobProgress>? ProgressChanged;

    /// <summary>Requests cancellation. Safe to call at any time, any number of times.</summary>
    public void Cancel()
    {
        if (Interlocked.Exchange(ref _cancelRequested, 1) == 1 || IsFinal(Status))
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The job finished between the status check and the cancel call.
        }
    }

    private protected abstract Task CompletionTask { get; }

    private protected CancellationToken Token => _cts.Token;

    /// <summary>Runs the job on the calling worker. Never throws.</summary>
    internal abstract Task RunAsync();

    internal void OnProgress(JobProgress progress)
    {
        if (Status != JobStatus.Running)
        {
            return;
        }

        Volatile.Write(ref _progress, new StrongBox<JobProgress>(progress));
        ProgressChanged?.Invoke(this, progress);
    }

    private protected bool TryStart() => TryTransition(JobStatus.Queued, JobStatus.Running);

    /// <summary>Records the final status and releases cancellation resources. Call before completing the task.</summary>
    private protected void Finish(JobStatus finalStatus)
    {
        Volatile.Write(ref _status, (int)finalStatus);
        ReleaseCancellation();
    }

    /// <summary>Unlinks the job from the caller's and the queue's tokens.</summary>
    private protected void ReleaseCancellation()
    {
        _cancelRegistration.Dispose();
        _cts.Dispose();
    }

    private protected abstract void CompleteAsCanceled(CancellationToken token);

    private void CancelIfQueued()
    {
        if (TryTransition(JobStatus.Queued, JobStatus.Canceled))
        {
            // The CTS is not disposed here (we are inside its own callback); the worker that later dequeues
            // this job releases it.
            CompleteAsCanceled(_cts.Token);
        }
    }

    private bool TryTransition(JobStatus from, JobStatus to) =>
        Interlocked.CompareExchange(ref _status, (int)to, (int)from) == (int)from;

    private static bool IsFinal(JobStatus status) =>
        status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Canceled;
}

/// <summary>A job that produces a result of type <typeparamref name="TResult"/>.</summary>
/// <typeparam name="TResult">Type of the job's result.</typeparam>
public sealed class JobHandle<TResult> : JobHandle
{
    private readonly TaskCompletionSource<TResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<JobContext, Task<TResult>> _work;

    internal JobHandle(
        string name,
        JobPriority priority,
        Func<JobContext, Task<TResult>> work,
        CancellationToken callerToken,
        CancellationToken shutdownToken)
        : base(name, priority, callerToken, shutdownToken)
    {
        _work = work;
    }

    /// <summary>
    /// Completes with the job's result. Faults with the job's exception on failure and is canceled on cancellation.
    /// </summary>
    public new Task<TResult> Completion => _completion.Task;

    private protected override Task CompletionTask => _completion.Task;

    internal override async Task RunAsync()
    {
        if (!TryStart())
        {
            ReleaseCancellation(); // Canceled while queued; Completion is already canceled.
            return;
        }

        var token = Token;
        try
        {
            var result = await _work(new JobContext(this, token)).ConfigureAwait(false);
            Finish(JobStatus.Succeeded);
            _completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Finish(JobStatus.Canceled);
            _completion.TrySetCanceled(token);
        }
        catch (Exception ex)
        {
            // A job may throw anything; the exception is delivered through Completion.
            Finish(JobStatus.Failed);
            _completion.TrySetException(ex);
        }
    }

    private protected override void CompleteAsCanceled(CancellationToken token) => _completion.TrySetCanceled(token);
}
