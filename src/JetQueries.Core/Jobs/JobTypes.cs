namespace JetQueries.Core.Jobs;

/// <summary>Scheduling priority of a job. Higher priorities are dequeued first; equal priorities run in FIFO order.</summary>
public enum JobPriority
{
    /// <summary>Background work the user is not waiting for (cache warm-up, indexing).</summary>
    Low = -1,

    /// <summary>Default priority.</summary>
    Normal = 0,

    /// <summary>Work the user is actively waiting for (completion, running a statement).</summary>
    High = 1,
}

/// <summary>Lifecycle state of a job.</summary>
public enum JobStatus
{
    /// <summary>Waiting for a free worker.</summary>
    Queued,

    /// <summary>Currently executing.</summary>
    Running,

    /// <summary>Finished without error.</summary>
    Succeeded,

    /// <summary>Finished with an exception.</summary>
    Failed,

    /// <summary>Canceled before or during execution.</summary>
    Canceled,
}

/// <summary>A progress report from a running job.</summary>
/// <param name="Fraction">Completed share in the range 0–1, or <see langword="null"/> when indeterminate.</param>
/// <param name="Message">Optional short description of the current step.</param>
public readonly record struct JobProgress(double? Fraction, string? Message = null);

/// <summary>Options for <see cref="JobQueue"/>.</summary>
public sealed class JobQueueOptions
{
    /// <summary>
    /// Maximum number of jobs running at the same time. Jobs are expected to be asynchronous (I/O-bound),
    /// so the default is deliberately higher than the core count.
    /// </summary>
    public int MaxConcurrency { get; init; } = Math.Max(8, Environment.ProcessorCount);
}
