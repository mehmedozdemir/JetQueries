namespace JetQueries.Sdk;

/// <summary>
/// Optional for an <see cref="IDatabaseConnection"/>: one database in depth for the database detail screen: its
/// settings, space of data and log, files, largest tables, integrity check and backup history. Catalog views only,
/// read when the screen opens or is refreshed.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0 (ADR 0014).</remarks>
public interface IDatabaseDetails
{
    /// <summary>Reads everything about <paramref name="database"/>.</summary>
    /// <param name="database">The database (a name from the server's list).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<DatabaseDetail> GetDatabaseDetailAsync(string database, CancellationToken cancellationToken);
}

/// <summary>One database in depth.</summary>
/// <param name="Name">Its name.</param>
public sealed record DatabaseDetail(string Name)
{
    /// <summary>ONLINE, OFFLINE, RESTORING…</summary>
    public string? State { get; init; }

    /// <summary>FULL, SIMPLE, BULK_LOGGED.</summary>
    public string? RecoveryModel { get; init; }

    /// <summary>The compatibility level (160, 150…).</summary>
    public int? CompatibilityLevel { get; init; }

    /// <summary>The default collation.</summary>
    public string? Collation { get; init; }

    /// <summary>The owner login.</summary>
    public string? Owner { get; init; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>
    /// Its settings as key / label / value, keys stable (<c>pageVerify</c>, <c>autoShrink</c>…) for the host to translate
    /// (<c>DbProp_&lt;key&gt;</c>), labels in English as a fallback.
    /// </summary>
    public IReadOnlyList<ObjectProperty> Options { get; init; } = [];

    /// <summary>Space of the data files, in MB.</summary>
    public double DataMb { get; init; }

    /// <summary>Space used inside the data files, in MB; null when unknown.</summary>
    public double? DataUsedMb { get; init; }

    /// <summary>Space of the log files, in MB.</summary>
    public double LogMb { get; init; }

    /// <summary>How full the log is, in percent; null when unknown.</summary>
    public double? LogUsedPercent { get; init; }

    /// <summary>What keeps the log from being reused (LOG_BACKUP, ACTIVE_TRANSACTION…); NOTHING when nothing.</summary>
    public string? LogReuseWait { get; init; }

    /// <summary>How many virtual log files the log is made of; null when unknown.</summary>
    public int? VirtualLogFiles { get; init; }

    /// <summary>User tables.</summary>
    public int TableCount { get; init; }

    /// <summary>Rows of all user tables.</summary>
    public long TotalRows { get; init; }

    /// <summary>The last integrity check that found no errors; null when unknown, the year 1900 when never.</summary>
    public DateTimeOffset? LastGoodCheckDb { get; init; }

    /// <summary>The data and log files.</summary>
    public IReadOnlyList<DatabaseFileInfo> Files { get; init; } = [];

    /// <summary>The largest tables, largest first.</summary>
    public IReadOnlyList<TableSpace> Tables { get; init; } = [];

    /// <summary>The latest backups, newest first.</summary>
    public IReadOnlyList<BackupRecord> Backups { get; init; } = [];

    /// <summary>Whether the backup history could be read (SQL Server: access to msdb).</summary>
    public bool BackupsKnown { get; init; }
}

/// <summary>A data or log file.</summary>
/// <param name="Name">Its logical name.</param>
public sealed record DatabaseFileInfo(string Name)
{
    /// <summary>ROWS, LOG, FILESTREAM…</summary>
    public string? Type { get; init; }

    /// <summary>Its filegroup; null for the log.</summary>
    public string? Filegroup { get; init; }

    /// <summary>Its path on the server.</summary>
    public string? Path { get; init; }

    /// <summary>Its size, in MB.</summary>
    public double SizeMb { get; init; }

    /// <summary>Space used inside it, in MB; null when unknown.</summary>
    public double? UsedMb { get; init; }

    /// <summary>How it grows ("512 MB", "10 %", "none").</summary>
    public string? Growth { get; init; }

    /// <summary>Its size limit ("unlimited", "2 048 MB").</summary>
    public string? MaxSize { get; init; }
}

/// <summary>A table and the space it takes.</summary>
/// <param name="Name">As <c>schema.table</c>.</param>
public sealed record TableSpace(string Name)
{
    /// <summary>Its rows.</summary>
    public long Rows { get; init; }

    /// <summary>Space reserved for it (data, indexes and free pages), in MB.</summary>
    public double ReservedMb { get; init; }

    /// <summary>Space of its data, in MB.</summary>
    public double DataMb { get; init; }

    /// <summary>Space of its indexes, in MB.</summary>
    public double IndexMb { get; init; }
}

/// <summary>A backup taken of the database.</summary>
/// <param name="Kind">What it saved.</param>
/// <param name="Finished">When it ended.</param>
public sealed record BackupRecord(BackupKind Kind, DateTimeOffset Finished)
{
    /// <summary>When it started.</summary>
    public DateTimeOffset? Started { get; init; }

    /// <summary>Its size, in MB.</summary>
    public double? SizeMb { get; init; }

    /// <summary>Its size after compression, in MB.</summary>
    public double? CompressedMb { get; init; }

    /// <summary>Where it was written (a file, a URL, a device).</summary>
    public string? Device { get; init; }

    /// <summary>A copy-only backup (outside the backup chain).</summary>
    public bool CopyOnly { get; init; }
}

/// <summary>What a backup saved.</summary>
public enum BackupKind
{
    /// <summary>The whole database.</summary>
    Full,

    /// <summary>What changed since the last full backup.</summary>
    Differential,

    /// <summary>The transaction log since the last log backup.</summary>
    Log,

    /// <summary>Files or filegroups, or another kind.</summary>
    Other,
}
