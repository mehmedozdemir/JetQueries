namespace JetQueries.Sdk;

/// <summary>
/// Optional for an <see cref="IDatabaseConnection"/>: what is happening on the server right now (its sessions, what
/// each runs and waits for, who blocks whom) and ending a session, for the activity monitor. The host gives the
/// monitor a connection of its own and asks again every few seconds while the monitor is shown, so the queries must
/// be light: the server's own views only, never user tables.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0 (ADR 0009).</remarks>
public interface IActivityMonitor
{
    /// <summary>Reads the server's sessions as they are now.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<ActivitySnapshot> GetActivityAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Ends a session (SQL Server's <c>KILL</c>); its open transaction is rolled back by the server. The host asks
    /// the user first and never offers the monitor's own session.
    /// </summary>
    /// <param name="sessionId">A <see cref="ActivitySession.Id"/> from the last snapshot.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task KillSessionAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>The server's sessions at one moment.</summary>
/// <param name="CapturedAt">When they were read.</param>
/// <param name="Sessions">Every session the login may see, the monitor's own included.</param>
/// <param name="OwnSessionId">The monitor's own session, which is never ended from the monitor.</param>
public sealed record ActivitySnapshot(DateTimeOffset CapturedAt, IReadOnlyList<ActivitySession> Sessions, string? OwnSessionId)
{
    /// <summary>
    /// Why only part of the server is visible (the login lacks a permission such as <c>VIEW SERVER STATE</c>),
    /// in English; null when everything is shown.
    /// </summary>
    public string? Limitation { get; init; }
}

/// <summary>One session on the server and, when it is running something, its current request.</summary>
/// <param name="Id">The session's identifier as the server writes it (SQL Server's session id).</param>
public sealed record ActivitySession(string Id)
{
    /// <summary>The login (user) of the session.</summary>
    public string? Login { get; init; }

    /// <summary>The client computer.</summary>
    public string? Host { get; init; }

    /// <summary>The client program, as it named itself.</summary>
    public string? Program { get; init; }

    /// <summary>The database the session (or its request) is in.</summary>
    public string? Database { get; init; }

    /// <summary>The server's status word: running, runnable, suspended, sleeping…</summary>
    public string? Status { get; init; }

    /// <summary>What the current request does (SELECT, UPDATE, BACKUP DATABASE…); null when idle.</summary>
    public string? Command { get; init; }

    /// <summary>What the request is waiting for (LCK_M_X, PAGEIOLATCH_SH…); null when it is not waiting.</summary>
    public string? WaitType { get; init; }

    /// <summary>How long it has been waiting.</summary>
    public TimeSpan? WaitTime { get; init; }

    /// <summary>The session that blocks this one; null when nothing blocks it.</summary>
    public string? BlockedBy { get; init; }

    /// <summary>CPU time used (by the request when there is one, else by the session).</summary>
    public TimeSpan? CpuTime { get; init; }

    /// <summary>Logical reads (pages).</summary>
    public long? Reads { get; init; }

    /// <summary>Writes (pages).</summary>
    public long? Writes { get; init; }

    /// <summary>How long the current request has been running; null when idle.</summary>
    public TimeSpan? Elapsed { get; init; }

    /// <summary>Transactions the session has open.</summary>
    public int? OpenTransactions { get; init; }

    /// <summary>When the session logged in.</summary>
    public DateTimeOffset? LoginTime { get; init; }

    /// <summary>When its last request ended.</summary>
    public DateTimeOffset? LastRequestEnd { get; init; }

    /// <summary>The statement running now, else the last one sent; null when unknown.</summary>
    public string? Sql { get; init; }

    /// <summary>A session of a user or program (false for the server's own background sessions).</summary>
    public bool IsUserSession { get; init; } = true;
}
