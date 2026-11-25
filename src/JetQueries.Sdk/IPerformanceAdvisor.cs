namespace JetQueries.Sdk;

/// <summary>
/// Optional for an <see cref="IDatabaseConnection"/>: what costs the server most and what the indexes could do
/// better, for the performance analysis screen. Each call is one read of the server's own statistics, made only
/// when the user asks; scripts it returns (CREATE INDEX, DROP INDEX, ALTER INDEX …) are proposals the host puts into
/// a query tab and never runs by itself.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0 (ADR 0010).</remarks>
public interface IPerformanceAdvisor
{
    /// <summary>The statements that cost most, from the server's statistics (SQL Server: the plan cache).</summary>
    /// <param name="request">Order, how many, and an optional database.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<HeavyQueryReport> GetHeavyQueriesAsync(HeavyQueryRequest request, CancellationToken cancellationToken);

    /// <summary>Indexes the optimizer wished it had, most useful first.</summary>
    /// <param name="database">One database, or null for all.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<MissingIndex>> GetMissingIndexesAsync(string? database, CancellationToken cancellationToken);

    /// <summary>The indexes of a database's tables with their columns and how much they were used.</summary>
    /// <param name="database">The database.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IndexUsageReport> GetIndexUsageAsync(string database, CancellationToken cancellationToken);

    /// <summary>
    /// How fragmented a database's indexes are. Reads index pages (SQL Server: <c>LIMITED</c> mode), so it is only
    /// called on the user's explicit request.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="minimumPages">Smaller indexes are left out (fragmentation does not matter for them).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<IndexFragmentation>> GetFragmentationAsync(string database, long minimumPages, CancellationToken cancellationToken);
}

/// <summary>What the heavy queries are ranked by.</summary>
public enum HeavyQueryOrder
{
    /// <summary>CPU time of all executions.</summary>
    TotalCpu,

    /// <summary>CPU time per execution.</summary>
    AverageCpu,

    /// <summary>Elapsed time of all executions.</summary>
    TotalDuration,

    /// <summary>Elapsed time per execution.</summary>
    AverageDuration,

    /// <summary>Logical reads of all executions.</summary>
    TotalReads,

    /// <summary>Logical reads per execution.</summary>
    AverageReads,

    /// <summary>How many times it ran.</summary>
    Executions,
}

/// <summary>Which heavy queries to read.</summary>
/// <param name="Order">What they are ranked by.</param>
/// <param name="Top">How many.</param>
/// <param name="Database">Only those that ran in this database; null for all.</param>
public sealed record HeavyQueryRequest(HeavyQueryOrder Order, int Top, string? Database);

/// <summary>The heavy queries and since when the server has been counting.</summary>
/// <param name="Queries">Ranked as asked.</param>
/// <param name="CountingSince">When the statistics start (SQL Server: server start; plans may be dropped earlier).</param>
public sealed record HeavyQueryReport(IReadOnlyList<HeavyQuery> Queries, DateTimeOffset? CountingSince);

/// <summary>One statement (statements differing only in their literals are counted together) and what it cost.</summary>
/// <param name="Sql">The statement.</param>
public sealed record HeavyQuery(string Sql)
{
    /// <summary>The database it ran in, when known.</summary>
    public string? Database { get; init; }

    /// <summary>The procedure, function or trigger it belongs to (<c>dbo.Proc</c>); null for an ad hoc statement.</summary>
    public string? ObjectName { get; init; }

    /// <summary>How many times it ran.</summary>
    public long Executions { get; init; }

    /// <summary>CPU time of all executions.</summary>
    public TimeSpan TotalCpu { get; init; }

    /// <summary>Elapsed time of all executions.</summary>
    public TimeSpan TotalDuration { get; init; }

    /// <summary>Logical reads of all executions.</summary>
    public long TotalReads { get; init; }

    /// <summary>Logical writes of all executions.</summary>
    public long TotalWrites { get; init; }

    /// <summary>Rows returned by all executions, when the server counts them.</summary>
    public long? TotalRows { get; init; }

    /// <summary>When it last ran.</summary>
    public DateTimeOffset? LastExecution { get; init; }

    /// <summary>CPU time per execution.</summary>
    public TimeSpan AverageCpu => Executions > 0 ? TotalCpu / Executions : TimeSpan.Zero;

    /// <summary>Elapsed time per execution.</summary>
    public TimeSpan AverageDuration => Executions > 0 ? TotalDuration / Executions : TimeSpan.Zero;

    /// <summary>Logical reads per execution.</summary>
    public long AverageReads => Executions > 0 ? TotalReads / Executions : 0;
}

/// <summary>An index the optimizer asked for.</summary>
/// <param name="Database">Its database.</param>
/// <param name="Table">The table, as <c>schema.table</c>.</param>
/// <param name="CreateScript">A <c>CREATE INDEX</c> proposal, quoted for the server; to be reviewed, never run by the host.</param>
public sealed record MissingIndex(string Database, string Table, string CreateScript)
{
    /// <summary>Columns compared with <c>=</c>, in the order proposed.</summary>
    public IReadOnlyList<string> EqualityColumns { get; init; } = [];

    /// <summary>Columns compared otherwise (<c>&gt;</c>, <c>&lt;&gt;</c>, <c>BETWEEN</c>…).</summary>
    public IReadOnlyList<string> InequalityColumns { get; init; } = [];

    /// <summary>Columns only read (for <c>INCLUDE</c>).</summary>
    public IReadOnlyList<string> IncludedColumns { get; init; } = [];

    /// <summary>Seeks and scans that would have used it.</summary>
    public long Uses { get; init; }

    /// <summary>The estimated cost reduction of those queries, in percent.</summary>
    public double ImpactPercent { get; init; }

    /// <summary>A ranking score: cost × impact × uses (larger is more useful).</summary>
    public double Score { get; init; }

    /// <summary>When a query last wished for it.</summary>
    public DateTimeOffset? LastWanted { get; init; }
}

/// <summary>A database's indexes and since when their use is counted.</summary>
/// <param name="Indexes">Every index of every user table (heaps left out).</param>
/// <param name="CountingSince">When the counters start (SQL Server: server start or the database coming online).</param>
public sealed record IndexUsageReport(IReadOnlyList<IndexUsage> Indexes, DateTimeOffset? CountingSince);

/// <summary>An index and how it was used.</summary>
/// <param name="Table">The table, as <c>schema.table</c>.</param>
/// <param name="Name">The index.</param>
public sealed record IndexUsage(string Table, string Name)
{
    /// <summary>The clustered index (or the table's own storage); never proposed for dropping.</summary>
    public bool IsClustered { get; init; }

    /// <summary>Its key values are unique.</summary>
    public bool IsUnique { get; init; }

    /// <summary>It backs a primary key or a unique constraint; never proposed for dropping.</summary>
    public bool IsConstraint { get; init; }

    /// <summary>Key columns in order, with <c>DESC</c> after a descending one.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>Columns stored with the key (<c>INCLUDE</c>).</summary>
    public IReadOnlyList<string> IncludedColumns { get; init; } = [];

    /// <summary>A filtered index's condition.</summary>
    public string? Filter { get; init; }

    /// <summary>Seeks by user queries.</summary>
    public long Seeks { get; init; }

    /// <summary>Scans by user queries.</summary>
    public long Scans { get; init; }

    /// <summary>Key lookups by user queries.</summary>
    public long Lookups { get; init; }

    /// <summary>Writes that had to maintain it.</summary>
    public long Updates { get; init; }

    /// <summary>When a user query last read it.</summary>
    public DateTimeOffset? LastRead { get; init; }

    /// <summary>Space it takes, in KB.</summary>
    public long SizeKb { get; init; }

    /// <summary>Rows in it.</summary>
    public long? Rows { get; init; }

    /// <summary>A <c>DROP INDEX</c> statement, quoted for the server; never run by the host.</summary>
    public string? DropScript { get; init; }

    /// <summary>Reads of any kind.</summary>
    public long Reads => Seeks + Scans + Lookups;
}

/// <summary>A fragmented index.</summary>
/// <param name="Table">The table, as <c>schema.table</c>.</param>
/// <param name="Name">The index.</param>
public sealed record IndexFragmentation(string Table, string Name)
{
    /// <summary>Logical fragmentation, in percent.</summary>
    public double FragmentationPercent { get; init; }

    /// <summary>Its size in pages.</summary>
    public long Pages { get; init; }

    /// <summary>The partition, when the index has several.</summary>
    public int? Partition { get; init; }

    /// <summary>What to run for it (<c>ALTER INDEX … REBUILD</c> above 30 %, else <c>REORGANIZE</c>); never run by the host.</summary>
    public string? MaintenanceScript { get; init; }

    /// <summary>The script rebuilds (true) or reorganizes.</summary>
    public bool Rebuild { get; init; }
}
