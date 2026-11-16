using System.Collections.Concurrent;
using JetQueries.Core.Jobs;

namespace JetQueries.Core.Tests.Jobs;

public sealed class JobQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Enqueue_runs_work_and_returns_result()
    {
        await using var queue = new JobQueue();

        var job = queue.Enqueue("answer", _ => Task.FromResult(42));

        Assert.Equal(42, await job.Completion.WaitAsync(Timeout, TestToken));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal("answer", job.Name);
    }

    [Fact]
    public async Task Work_never_runs_on_the_enqueuing_thread()
    {
        await using var queue = new JobQueue();
        using var started = new ManualResetEventSlim();
        var callerThread = Environment.CurrentManagedThreadId;
        var workerThread = callerThread;

        var job = queue.Enqueue("thread", _ =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            started.Set();
            return Task.CompletedTask;
        });

        // The caller blocks its own thread here (as a busy UI thread would), so the work can only have started
        // on another one.
        Assert.True(started.Wait(Timeout, TestToken));
        Assert.NotEqual(callerThread, workerThread);
        await job.Completion.WaitAsync(Timeout, TestToken);
    }

    [Fact]
    public async Task Jobs_run_by_priority_then_in_submission_order()
    {
        await using var queue = new JobQueue(new JobQueueOptions { MaxConcurrency = 1 });
        var order = new ConcurrentQueue<string>();
        var (blocker, release) = await OccupyWorkerAsync(queue);

        var jobs = new[]
        {
            queue.Enqueue("low", _ => Record("low"), JobPriority.Low),
            queue.Enqueue("normal-1", _ => Record("normal-1")),
            queue.Enqueue("high-1", _ => Record("high-1"), JobPriority.High),
            queue.Enqueue("normal-2", _ => Record("normal-2")),
            queue.Enqueue("high-2", _ => Record("high-2"), JobPriority.High),
        };
        release.SetResult();
        await Task.WhenAll(jobs.Select(j => j.Completion).Append(blocker.Completion)).WaitAsync(Timeout, TestToken);

        Assert.Equal(["high-1", "high-2", "normal-1", "normal-2", "low"], order);

        Task Record(string name)
        {
            order.Enqueue(name);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Concurrency_never_exceeds_the_configured_maximum()
    {
        await using var queue = new JobQueue(new JobQueueOptions { MaxConcurrency = 3 });
        var running = 0;
        var peak = 0;

        var jobs = Enumerable.Range(0, 30).Select(i => queue.Enqueue($"job-{i}", async context =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(10, context.CancellationToken);
            Interlocked.Decrement(ref running);
        })).ToArray();
        await Task.WhenAll(jobs.Select(j => j.Completion)).WaitAsync(Timeout, TestToken);

        Assert.InRange(peak, 2, 3);
    }

    [Fact]
    public async Task Progress_handlers_see_every_report_in_order()
    {
        await using var queue = new JobQueue(new JobQueueOptions { MaxConcurrency = 1 });
        var reports = new List<JobProgress>();
        var (blocker, release) = await OccupyWorkerAsync(queue);

        var job = queue.Enqueue("progress", context =>
        {
            context.Report(0.25, "a");
            context.Report(null, "b");
            context.Report(1.0);
            return Task.CompletedTask;
        });
        job.ProgressChanged += (_, progress) => reports.Add(progress); // Attached while the job is still queued.
        release.SetResult();
        await Task.WhenAll(blocker.Completion, job.Completion).WaitAsync(Timeout, TestToken);

        Assert.Equal([new JobProgress(0.25, "a"), new JobProgress(null, "b"), new JobProgress(1.0)], reports);
        Assert.Equal(new JobProgress(1.0), job.Progress);
    }

    [Fact]
    public async Task Canceling_a_queued_job_completes_it_without_running_it()
    {
        await using var queue = new JobQueue(new JobQueueOptions { MaxConcurrency = 1 });
        var (blocker, release) = await OccupyWorkerAsync(queue);
        var ran = false;

        var job = queue.Enqueue("never", _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        job.Cancel();

        // Completes immediately, while the only worker is still busy.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.Completion.WaitAsync(Timeout, TestToken));
        Assert.Equal(JobStatus.Canceled, job.Status);

        release.SetResult();
        await blocker.Completion.WaitAsync(Timeout, TestToken);
        var after = queue.Enqueue("after", _ => Task.CompletedTask);
        await after.Completion.WaitAsync(Timeout, TestToken);
        Assert.False(ran);
    }

    [Fact]
    public async Task Canceling_a_running_job_signals_its_token()
    {
        await using var queue = new JobQueue();
        var started = NewSignal();

        var job = queue.Enqueue("long", async context =>
        {
            started.SetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, context.CancellationToken);
        });
        await started.Task.WaitAsync(Timeout, TestToken);
        Assert.Equal(JobStatus.Running, job.Status);
        job.Cancel();
        job.Cancel(); // Idempotent.

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.Completion.WaitAsync(Timeout, TestToken));
        Assert.Equal(JobStatus.Canceled, job.Status);
    }

    [Fact]
    public async Task The_callers_token_cancels_the_job()
    {
        await using var queue = new JobQueue();
        using var cts = new CancellationTokenSource();
        var started = NewSignal();

        var job = queue.Enqueue(
            "long",
            async context =>
            {
                started.SetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, context.CancellationToken);
            },
            cancellationToken: cts.Token);
        await started.Task.WaitAsync(Timeout, TestToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.Completion.WaitAsync(Timeout, TestToken));
        Assert.Equal(JobStatus.Canceled, job.Status);
    }

    [Fact]
    public async Task A_failing_job_faults_its_handle_and_the_queue_keeps_working()
    {
        await using var queue = new JobQueue(new JobQueueOptions { MaxConcurrency = 1 });

        var failing = queue.Enqueue("boom", _ => throw new InvalidOperationException("boom"));
        var next = queue.Enqueue("next", _ => Task.FromResult("ok"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => failing.Completion.WaitAsync(Timeout, TestToken));
        Assert.Equal("boom", ex.Message);
        Assert.Equal(JobStatus.Failed, failing.Status);
        Assert.Equal("ok", await next.Completion.WaitAsync(Timeout, TestToken));
    }

    [Fact]
    public async Task An_OperationCanceledException_without_cancellation_is_a_failure()
    {
        await using var queue = new JobQueue();

        var job = queue.Enqueue("timeout", _ => throw new TimeoutException("driver timeout", new OperationCanceledException()));
        var foreign = queue.Enqueue("foreign", _ => throw new OperationCanceledException("not ours"));

        await Assert.ThrowsAsync<TimeoutException>(() => job.Completion.WaitAsync(Timeout, TestToken));
        await Assert.ThrowsAsync<OperationCanceledException>(() => foreign.Completion.WaitAsync(Timeout, TestToken));
        Assert.Equal(JobStatus.Failed, foreign.Status);
    }

    [Fact]
    public async Task Disposing_cancels_running_and_queued_jobs()
    {
        var queue = new JobQueue(new JobQueueOptions { MaxConcurrency = 1 });
        var started = NewSignal();

        var running = queue.Enqueue("running", async context =>
        {
            started.SetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, context.CancellationToken);
        });
        var queued = queue.Enqueue("queued", _ => Task.CompletedTask);
        await started.Task.WaitAsync(Timeout, TestToken);

        await queue.DisposeAsync().AsTask().WaitAsync(Timeout, TestToken);

        Assert.Equal(JobStatus.Canceled, running.Status);
        Assert.Equal(JobStatus.Canceled, queued.Status);
        Assert.Throws<ObjectDisposedException>(() => queue.Enqueue("late", _ => Task.CompletedTask));
        await queue.DisposeAsync(); // Idempotent.
    }

    [Fact]
    public void Constructor_rejects_a_non_positive_concurrency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobQueue(new JobQueueOptions { MaxConcurrency = 0 }));
    }

    /// <summary>Starts a job that holds one worker until the returned signal is set.</summary>
    private static async Task<(JobHandle Job, TaskCompletionSource Release)> OccupyWorkerAsync(JobQueue queue)
    {
        var started = NewSignal();
        var release = NewSignal();
        var job = queue.Enqueue(
            "blocker",
            async _ =>
            {
                started.SetResult();
                await release.Task;
            },
            JobPriority.High);
        await started.Task.WaitAsync(Timeout, TestToken);
        return (job, release);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
