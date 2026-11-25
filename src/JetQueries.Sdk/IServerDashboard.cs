namespace JetQueries.Sdk;

/// <summary>
/// Optional for an <see cref="IDatabaseConnection"/>: the server at a glance for the dashboard — what it is and
/// its databases (<see cref="GetOverviewAsync"/>, read once), and a sample of its load (<see cref="GetSampleAsync"/>,
/// read every few seconds while the dashboard is shown). Counters in a sample are running totals; the host turns
/// two samples into rates. The server's own views only, never user tables.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0 (ADR 0011).</remarks>
public interface IServerDashboard
{
    /// <summary>What the server is, its databases, and the CPU history it keeps (if any).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<ServerOverview> GetOverviewAsync(CancellationToken cancellationToken);

    /// <summary>The server's load now.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<ServerSample> GetSampleAsync(CancellationToken cancellationToken);
}

/// <summary>What the server is.</summary>
/// <param name="Product">"Microsoft SQL Server 2022", "Oracle Database 19c"…</param>
/// <param name="Version">The version number.</param>
public sealed record ServerOverview(string Product, string Version)
{
    /// <summary>The edition (Enterprise, Standard, Express…).</summary>
    public string? Edition { get; init; }

    /// <summary>The machine (or instance) name.</summary>
    public string? Host { get; init; }

    /// <summary>When the server started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Logical processors the server sees.</summary>
    public int? CpuCount { get; init; }

    /// <summary>Memory of the machine, in MB.</summary>
    public long? PhysicalMemoryMb { get; init; }

    /// <summary>The databases with their size and backups.</summary>
    public IReadOnlyList<DatabaseSummary> Databases { get; init; } = [];

    /// <summary>CPU use the server recorded before the dashboard opened (SQL Server: one point a minute), oldest first.</summary>
    public IReadOnlyList<CpuPoint> CpuHistory { get; init; } = [];
}

/// <summary>A database at a glance.</summary>
/// <param name="Name">Its name.</param>
public sealed record DatabaseSummary(string Name)
{
    /// <summary>ONLINE, OFFLINE, RESTORING…</summary>
    public string? State { get; init; }

    /// <summary>FULL, SIMPLE, BULK_LOGGED (or ARCHIVELOG…).</summary>
    public string? RecoveryModel { get; init; }

    /// <summary>Size of its data files, in MB.</summary>
    public double DataMb { get; init; }

    /// <summary>Size of its log files, in MB.</summary>
    public double LogMb { get; init; }

    /// <summary>The last full backup; null when there is none or it cannot be read.</summary>
    public DateTimeOffset? LastFullBackup { get; init; }

    /// <summary>The last log backup; null when there is none or it cannot be read.</summary>
    public DateTimeOffset? LastLogBackup { get; init; }

    /// <summary>Whether backups could be read at all (SQL Server: access to msdb).</summary>
    public bool BackupsKnown { get; init; }
}

/// <summary>CPU use at one moment, in percent of the machine.</summary>
/// <param name="At">When.</param>
/// <param name="ServerPercent">Used by the database server.</param>
/// <param name="OtherPercent">Used by everything else on the machine.</param>
public sealed record CpuPoint(DateTimeOffset At, double ServerPercent, double OtherPercent);

/// <summary>The server's load at one moment. Totals only grow (until a restart); the host makes rates of them.</summary>
/// <param name="At">When it was read.</param>
public sealed record ServerSample(DateTimeOffset At)
{
    /// <summary>The latest CPU figure the server has; null when it keeps none.</summary>
    public CpuPoint? Cpu { get; init; }

    /// <summary>Memory the server uses, in MB.</summary>
    public long? MemoryUsedMb { get; init; }

    /// <summary>Memory the server aims to use, in MB.</summary>
    public long? MemoryTargetMb { get; init; }

    /// <summary>Free memory on the machine, in MB.</summary>
    public long? AvailableMemoryMb { get; init; }

    /// <summary>How long a page stays in the buffer pool, in seconds (low means memory pressure).</summary>
    public long? PageLifeExpectancy { get; init; }

    /// <summary>Open user connections.</summary>
    public int? Connections { get; init; }

    /// <summary>User requests running now.</summary>
    public int? ActiveRequests { get; init; }

    /// <summary>Requests waiting on another session's lock.</summary>
    public int? BlockedRequests { get; init; }

    /// <summary>Total batches (statements sent) received.</summary>
    public long? BatchRequests { get; init; }

    /// <summary>Total compilations.</summary>
    public long? Compilations { get; init; }

    /// <summary>Total transactions.</summary>
    public long? Transactions { get; init; }

    /// <summary>Total bytes read from data and log files.</summary>
    public long? BytesRead { get; init; }

    /// <summary>Total bytes written to data and log files.</summary>
    public long? BytesWritten { get; init; }

    /// <summary>Total file reads and writes.</summary>
    public long? IoOperations { get; init; }

    /// <summary>Total time those reads and writes waited, in ms.</summary>
    public long? IoStallMs { get; init; }

    /// <summary>Total wait time by wait type, harmless background waits left out.</summary>
    public IReadOnlyList<WaitTotal> Waits { get; init; } = [];
}

/// <summary>Time spent waiting for one reason since the server started.</summary>
/// <param name="Type">The wait type (PAGEIOLATCH_SH, LCK_M_X, CXPACKET…).</param>
/// <param name="WaitMs">Total time waited, in ms.</param>
public sealed record WaitTotal(string Type, long WaitMs);
