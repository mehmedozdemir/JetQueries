namespace JetQueries.Core.Jobs;

/// <summary>What a running job sees: its cancellation token and a way to report progress.</summary>
public sealed class JobContext : IProgress<JobProgress>
{
    private readonly JobHandle _job;

    internal JobContext(JobHandle job, CancellationToken cancellationToken)
    {
        _job = job;
        CancellationToken = cancellationToken;
    }

    /// <summary>Signaled when the job is canceled or the queue shuts down. Pass it to every awaited call.</summary>
    public CancellationToken CancellationToken { get; }

    /// <inheritdoc />
    public void Report(JobProgress value) => _job.OnProgress(value);

    /// <summary>Reports progress.</summary>
    /// <param name="fraction">Completed share in the range 0–1, or <see langword="null"/> when indeterminate.</param>
    /// <param name="message">Optional short description of the current step.</param>
    public void Report(double? fraction, string? message = null) => Report(new JobProgress(fraction, message));
}
