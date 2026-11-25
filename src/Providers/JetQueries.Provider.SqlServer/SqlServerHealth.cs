using System.Globalization;
using System.Text;
using JetQueries.Sdk;
using Microsoft.Data.SqlClient;

namespace JetQueries.Provider.SqlServer;

/// <summary>
/// The health check on SQL Server: server settings (sys.configurations), database options and files, tempdb, the
/// plan cache, logins, and from msdb backups and suspect pages. One batch of catalog reads; every rule is decided
/// here and reported, passing ones as OK. Fix scripts are written with bracketed names and never run.
/// </summary>
internal static class SqlServerHealth
{
    private const string Sql = """
        /* JetQueries:advisor */
        SELECT name, CONVERT(bigint, value_in_use) FROM sys.configurations
        WHERE name IN ('max server memory (MB)', 'max degree of parallelism', 'cost threshold for parallelism',
                       'optimize for ad hoc workloads', 'priority boost');
        SELECT i.cpu_count, i.physical_memory_kb / 1024,
            TODATETIMEOFFSET(i.sqlserver_start_time, DATEPART(TZOFFSET, SYSDATETIMEOFFSET())),
            CONVERT(int, SERVERPROPERTY('ProductMajorVersion')) * 10,
            (SELECT MAX(n) FROM (SELECT COUNT(*) AS n FROM sys.dm_os_schedulers WHERE status = 'VISIBLE ONLINE' GROUP BY parent_node_id) x),
            CONVERT(int, SERVERPROPERTY('IsIntegratedSecurityOnly'))
        FROM sys.dm_os_sys_info i;
        SELECT d.name, d.database_id, d.state_desc, d.recovery_model_desc, d.is_auto_shrink_on, d.is_auto_close_on,
            d.page_verify_option_desc, d.compatibility_level, d.is_auto_create_stats_on, d.is_auto_update_stats_on,
            d.is_trustworthy_on, d.log_reuse_wait_desc,
            CASE WHEN d.state = 0 THEN CONVERT(datetime, DATABASEPROPERTYEX(d.name, 'LastGoodCheckDbTime')) END
        FROM sys.databases d ORDER BY d.name;
        SELECT DB_NAME(f.database_id), f.name, f.type, f.is_percent_growth, f.growth, CONVERT(bigint, f.size) * 8 / 1024
        FROM sys.master_files f ORDER BY f.database_id, f.file_id;
        SELECT CONVERT(float, SUM(CASE WHEN p.usecounts = 1 THEN CONVERT(bigint, p.size_in_bytes) ELSE 0 END)) * 100
               / NULLIF(SUM(CONVERT(bigint, p.size_in_bytes)), 0)
        FROM sys.dm_exec_cached_plans p;
        SELECT p.is_disabled FROM sys.server_principals p WHERE p.sid = 0x01;
        SELECT m.name FROM sys.server_role_members r
        JOIN sys.server_principals g ON g.principal_id = r.role_principal_id AND g.name = 'sysadmin'
        JOIN sys.server_principals m ON m.principal_id = r.member_principal_id
        WHERE m.sid <> 0x01 AND m.name NOT LIKE 'NT SERVICE\%' AND m.name NOT LIKE '##%' AND m.name NOT LIKE 'NT AUTHORITY\%'
        ORDER BY m.name;
        """;

    private const string MsdbSql = """
        /* JetQueries:advisor */
        SELECT b.database_name, MAX(CASE WHEN b.type = 'D' THEN b.backup_finish_date END), MAX(CASE WHEN b.type = 'L' THEN b.backup_finish_date END)
        FROM msdb.dbo.backupset b GROUP BY b.database_name;
        SELECT DB_NAME(s.database_id), COUNT(*) FROM msdb.dbo.suspect_pages s WHERE s.event_type IN (1, 2, 3) GROUP BY s.database_id;
        """;

    private sealed record Database(string Name, int Id, string State, string Recovery, bool AutoShrink, bool AutoClose, string PageVerify,
        int Compatibility, bool AutoCreateStats, bool AutoUpdateStats, bool Trustworthy, string LogReuseWait, DateTime? LastCheckDb)
    {
        public bool Online => State == "ONLINE";

        public bool IsUser => Id > 4;
    }

    private sealed record DataFile(string Database, string Name, bool IsLog, bool PercentGrowth, int Growth, long SizeMb);

    public static async Task<IReadOnlyList<HealthFinding>> CheckAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var config = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        int cpus = 1, serverCompatibility = 0, coresPerNode = 0;
        long memoryMb = 0;
        var integratedOnly = false;
        DateTimeOffset? started = null;
        var databases = new List<Database>();
        var files = new List<DataFile>();
        double? singleUse = null;
        bool? saDisabled = null;
        var sysadmins = new List<string>();

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Constant text.
            command.CommandText = Sql;
#pragma warning restore CA2100
            command.CommandTimeout = 60;
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    config[reader.GetString(0)] = reader.GetInt64(1);
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    cpus = Int(reader, 0) ?? 1;
                    memoryMb = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                    started = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2);
                    serverCompatibility = Int(reader, 3) ?? 0;
                    coresPerNode = Int(reader, 4) ?? cpus;
                    integratedOnly = Int(reader, 5) == 1;
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    databases.Add(new Database(reader.GetString(0), Int(reader, 1) ?? 0, reader.GetString(2), reader.GetString(3), reader.GetBoolean(4),
                        reader.GetBoolean(5), reader.IsDBNull(6) ? "NONE" : reader.GetString(6), Int(reader, 7) ?? 0, reader.GetBoolean(8), reader.GetBoolean(9),
                        reader.GetBoolean(10), reader.IsDBNull(11) ? "NOTHING" : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetDateTime(12)));
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    files.Add(new DataFile(reader.IsDBNull(0) ? "?" : reader.GetString(0), reader.GetString(1), Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) == 1,
                        reader.GetBoolean(3), Int(reader, 4) ?? 0, Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture)));
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !reader.IsDBNull(0))
                {
                    singleUse = reader.GetDouble(0);
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    saDisabled = reader.GetBoolean(0);
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    sysadmins.Add(reader.GetString(0));
                }
            }
        }

        var (backups, suspect) = await MsdbAsync(connection, cancellationToken).ConfigureAwait(false);
        var now = DateTime.Now;
        var findings = new List<HealthFinding>();

        // Configuration ------------------------------------------------------------------------------------------
        var maxMemory = config.GetValueOrDefault("max server memory (MB)");
        var recommendedMemory = Math.Max(1024, memoryMb - Math.Max(4096, memoryMb / 10));
        findings.Add(maxMemory >= 2_147_483_647
            ? Finding("MaxServerMemory", HealthCategory.Configuration, HealthSeverity.Warning, "Max server memory is not set") with
            {
                Current = maxMemory.ToString(CultureInfo.InvariantCulture),
                Recommended = recommendedMemory.ToString(CultureInfo.InvariantCulture),
                FixScript = Configure("max server memory (MB)", recommendedMemory),
            }
            : Ok("MaxServerMemory", HealthCategory.Configuration, "Max server memory is not set", maxMemory));

        var maxDop = config.GetValueOrDefault("max degree of parallelism");
        var recommendedDop = Math.Min(8, Math.Max(1, coresPerNode));
        findings.Add(maxDop == 0 && cpus > 8
            ? Finding("MaxDop", HealthCategory.Configuration, HealthSeverity.Warning, "Max degree of parallelism is unlimited on many CPUs") with
            {
                Current = "0",
                Recommended = recommendedDop.ToString(CultureInfo.InvariantCulture),
                FixScript = Configure("max degree of parallelism", recommendedDop),
            }
            : Ok("MaxDop", HealthCategory.Configuration, "Max degree of parallelism is unlimited on many CPUs", maxDop));

        var costThreshold = config.GetValueOrDefault("cost threshold for parallelism", 5);
        findings.Add(costThreshold <= 5
            ? Finding("CostThreshold", HealthCategory.Performance, HealthSeverity.Warning, "Cost threshold for parallelism is the default 5") with
            {
                Current = costThreshold.ToString(CultureInfo.InvariantCulture), Recommended = "50", FixScript = Configure("cost threshold for parallelism", 50),
            }
            : Ok("CostThreshold", HealthCategory.Performance, "Cost threshold for parallelism is the default 5", costThreshold));

        var adhoc = config.GetValueOrDefault("optimize for ad hoc workloads");
        findings.Add(adhoc == 0 && singleUse > 30
            ? Finding("OptimizeAdhoc", HealthCategory.Performance, HealthSeverity.Info, "Single-use plans fill the plan cache") with
            {
                Current = singleUse.Value.ToString("0", CultureInfo.InvariantCulture) + " %", Recommended = "1", FixScript = Configure("optimize for ad hoc workloads", 1),
            }
            : Ok("OptimizeAdhoc", HealthCategory.Performance, "Single-use plans fill the plan cache", adhoc));

        var boost = config.GetValueOrDefault("priority boost");
        findings.Add(boost == 1
            ? Finding("PriorityBoost", HealthCategory.Configuration, HealthSeverity.Warning, "Priority boost is on") with
            {
                Current = "1", Recommended = "0", FixScript = Configure("priority boost", 0),
            }
            : Ok("PriorityBoost", HealthCategory.Configuration, "Priority boost is on", boost));

        // Databases ----------------------------------------------------------------------------------------------
        var online = databases.Where(static d => d.Online).ToList();
        findings.Add(Many("AutoShrink", HealthCategory.Databases, HealthSeverity.Warning, "Auto shrink is on",
            online.Where(static d => d.AutoShrink).Select(static d => (d.Name, (string?)null, (string?)$"ALTER DATABASE {SqlServerNames.Quote(d.Name)} SET AUTO_SHRINK OFF;"))));
        findings.Add(Many("AutoClose", HealthCategory.Databases, HealthSeverity.Warning, "Auto close is on",
            online.Where(static d => d.AutoClose).Select(static d => (d.Name, (string?)null, (string?)$"ALTER DATABASE {SqlServerNames.Quote(d.Name)} SET AUTO_CLOSE OFF;"))));
        findings.Add(Many("PageVerify", HealthCategory.Databases, HealthSeverity.Warning, "Page verification is not CHECKSUM",
            online.Where(static d => d.Id != 2 && d.PageVerify != "CHECKSUM")
                .Select(static d => (d.Name, (string?)d.PageVerify, (string?)$"ALTER DATABASE {SqlServerNames.Quote(d.Name)} SET PAGE_VERIFY CHECKSUM WITH NO_WAIT;"))));
        findings.Add(Many("AutoStats", HealthCategory.Performance, HealthSeverity.Warning, "Automatic statistics are off",
            online.Where(static d => !d.AutoCreateStats || !d.AutoUpdateStats).Select(static d => (d.Name,
                (string?)((d.AutoCreateStats ? string.Empty : "AUTO_CREATE_STATISTICS OFF ") + (d.AutoUpdateStats ? string.Empty : "AUTO_UPDATE_STATISTICS OFF")).Trim(),
                (string?)$"ALTER DATABASE {SqlServerNames.Quote(d.Name)} SET AUTO_CREATE_STATISTICS ON;\nALTER DATABASE {SqlServerNames.Quote(d.Name)} SET AUTO_UPDATE_STATISTICS ON;"))));
        findings.Add(Many("Trustworthy", HealthCategory.Security, HealthSeverity.Warning, "TRUSTWORTHY is on",
            online.Where(static d => d.Trustworthy && d.Name != "msdb").Select(static d => (d.Name, (string?)null, (string?)$"ALTER DATABASE {SqlServerNames.Quote(d.Name)} SET TRUSTWORTHY OFF;"))));
        findings.Add(Many("CompatibilityLevel", HealthCategory.Databases, HealthSeverity.Info, "Compatibility level is older than the server's",
            online.Where(d => d.IsUser && serverCompatibility > 0 && d.Compatibility < serverCompatibility)
                .Select(d => (d.Name, (string?)(d.Compatibility.ToString(CultureInfo.InvariantCulture) + " < " + serverCompatibility.ToString(CultureInfo.InvariantCulture)), (string?)null))));

        // Growth by a percentage, or by less than 64 MB (data) / 64 MB (log): many small stalls, fragmented files.
        findings.Add(Many("FileGrowth", HealthCategory.Databases, HealthSeverity.Warning, "Files grow by a percentage or in tiny steps",
            files.Where(static f => f.Growth > 0 && (f.PercentGrowth || f.Growth * 8 / 1024 < 64))
                .Select(f => (f.Database + " · " + f.Name, (string?)(f.PercentGrowth ? f.Growth.ToString(CultureInfo.InvariantCulture) + " %" : (f.Growth * 8 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB"),
                    (string?)$"ALTER DATABASE {SqlServerNames.Quote(f.Database)} MODIFY FILE (NAME = {SqlServerNames.Quote(f.Name)}, FILEGROWTH = {(f.IsLog ? 256 : 512)}MB);"))));

        var dataSize = files.Where(static f => !f.IsLog).GroupBy(static f => f.Database).ToDictionary(static g => g.Key, static g => g.Sum(static f => f.SizeMb));
        findings.Add(Many("LogReuse", HealthCategory.Databases, HealthSeverity.Warning, "A log is larger than its data and cannot be reused",
            files.Where(static f => f.IsLog).GroupBy(static f => f.Database)
                .Select(g => (Name: g.Key, Log: g.Sum(static f => f.SizeMb), Database: online.FirstOrDefault(d => d.Name == g.Key)))
                .Where(x => x.Database is { } d && x.Log > 1024 && x.Log > dataSize.GetValueOrDefault(x.Name) && d.LogReuseWait is not ("NOTHING" or "CHECKPOINT"))
                .Select(x => (x.Name, (string?)(x.Log.ToString("N0", CultureInfo.InvariantCulture) + " MB · " + x.Database!.LogReuseWait), (string?)null))));

        var tempdb = files.Where(static f => f.Database == "tempdb" && !f.IsLog).ToList();
        var tempdbWanted = Math.Min(8, cpus);
        findings.Add(tempdb.Count > 0 && (tempdb.Count < tempdbWanted || tempdb.Select(static f => f.SizeMb).Distinct().Count() > 1)
            ? Finding("TempdbFiles", HealthCategory.Configuration, HealthSeverity.Warning, "tempdb has too few or unequal data files") with
            {
                Current = tempdb.Count.ToString(CultureInfo.InvariantCulture) + " · " + string.Join(", ", tempdb.Select(static f => f.SizeMb.ToString(CultureInfo.InvariantCulture) + " MB")),
                Recommended = tempdbWanted.ToString(CultureInfo.InvariantCulture),
            }
            : Ok("TempdbFiles", HealthCategory.Configuration, "tempdb has too few or unequal data files", tempdb.Count));

        // Backups and integrity ----------------------------------------------------------------------------------
        var backed = online.Where(static d => d.Id != 2).ToList();
        if (backups is null)
        {
            findings.Add(Finding("Backups", HealthCategory.Backups, HealthSeverity.Info, "Backups could not be read (no access to msdb)"));
        }
        else
        {
            findings.Add(Many("FullBackup", HealthCategory.Backups, HealthSeverity.Critical, "A database has no recent full backup",
                backed.Select(d => (d.Name, Full: backups.GetValueOrDefault(d.Name).Full))
                    .Where(x => x.Full is null || now - x.Full > TimeSpan.FromDays(7))
                    .Select(x => (x.Name, (string?)(x.Full is { } full ? full.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—"), (string?)null))));
            findings.Add(Many("LogBackup", HealthCategory.Backups, HealthSeverity.Critical, "A FULL recovery database has no recent log backup",
                backed.Where(static d => d.IsUser && d.Recovery == "FULL").Select(d => (d.Name, Log: backups.GetValueOrDefault(d.Name).Log))
                    .Where(x => x.Log is null || now - x.Log > TimeSpan.FromDays(1))
                    .Select(x => (x.Name, (string?)(x.Log is { } log ? log.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—"), (string?)null))));
        }

        findings.Add(Many("CheckDb", HealthCategory.Backups, HealthSeverity.Warning, "Integrity has not been checked for a week",
            backed.Where(static d => d.LastCheckDb is not null)
                .Where(d => d.LastCheckDb!.Value.Year < 1901 || now - d.LastCheckDb.Value > TimeSpan.FromDays(7))
                .Select(static d => (d.Name, (string?)(d.LastCheckDb!.Value.Year < 1901 ? "—" : d.LastCheckDb.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    (string?)$"DBCC CHECKDB ({SqlServerNames.Quote(d.Name)}) WITH NO_INFOMSGS, ALL_ERRORMSGS;"))));

        if (suspect is not null)
        {
            findings.Add(Many("SuspectPages", HealthCategory.Backups, HealthSeverity.Critical, "Damaged pages were recorded",
                suspect.Select(static s => (s.Key, (string?)s.Value.ToString(CultureInfo.InvariantCulture), (string?)$"DBCC CHECKDB ({SqlServerNames.Quote(s.Key)}) WITH NO_INFOMSGS, ALL_ERRORMSGS;"))));
        }

        // Security -----------------------------------------------------------------------------------------------
        findings.Add(saDisabled == false && !integratedOnly
            ? Finding("SaEnabled", HealthCategory.Security, HealthSeverity.Warning, "The sa login is enabled") with { FixScript = "ALTER LOGIN [sa] DISABLE;" }
            : Ok("SaEnabled", HealthCategory.Security, "The sa login is enabled", null));
        findings.Add(sysadmins.Count > 3
            ? Finding("Sysadmins", HealthCategory.Security, HealthSeverity.Info, "Many logins are sysadmin") with
            {
                Current = sysadmins.Count.ToString(CultureInfo.InvariantCulture), Recommended = "≤ 3", Objects = sysadmins,
            }
            : Ok("Sysadmins", HealthCategory.Security, "Many logins are sysadmin", sysadmins.Count));

        // Uptime: everything counted since start (waits, index use, plan cache) is short.
        findings.Add(started is { } start && DateTimeOffset.Now - start < TimeSpan.FromDays(1)
            ? Finding("Uptime", HealthCategory.Performance, HealthSeverity.Info, "The server started less than a day ago") with
            {
                Current = start.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            }
            : Ok("Uptime", HealthCategory.Performance, "The server started less than a day ago", null));
        return findings;
    }

    private static HealthFinding Finding(string id, HealthCategory category, HealthSeverity severity, string title) => new(id, category, severity, title);

    private static HealthFinding Ok(string id, HealthCategory category, string title, long? current) =>
        new(id, category, HealthSeverity.Ok, title) { Current = current?.ToString(CultureInfo.InvariantCulture) };

    // A rule about several objects: OK when none, else each object with its detail, and one script for all.
    private static HealthFinding Many(string id, HealthCategory category, HealthSeverity severity, string title, IEnumerable<(string Name, string? Detail, string? Script)> hits)
    {
        var list = hits.ToList();
        if (list.Count == 0)
        {
            return new HealthFinding(id, category, HealthSeverity.Ok, title);
        }

        var scripts = list.Where(static h => h.Script is not null).Select(static h => h.Script!).ToList();
        return new HealthFinding(id, category, severity, title)
        {
            Objects = [.. list.Select(static h => h.Detail is null ? h.Name : h.Name + " (" + h.Detail + ")")],
            FixScript = scripts.Count == 0 ? null : string.Join("\n", scripts),
        };
    }

    private static string Configure(string option, long value)
    {
        var script = new StringBuilder();
        script.Append("EXEC sys.sp_configure N'show advanced options', 1;\nRECONFIGURE;\n");
        script.Append("EXEC sys.sp_configure N'").Append(option.Replace("'", "''", StringComparison.Ordinal)).Append("', ")
            .Append(value.ToString(CultureInfo.InvariantCulture)).Append(";\nRECONFIGURE;");
        return script.ToString();
    }

    private static async Task<(Dictionary<string, (DateTime? Full, DateTime? Log)>? Backups, Dictionary<string, int>? Suspect)> MsdbAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var backups = new Dictionary<string, (DateTime? Full, DateTime? Log)>(StringComparer.OrdinalIgnoreCase);
            var suspect = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
#pragma warning disable CA2100 // Constant text.
                command.CommandText = MsdbSql;
#pragma warning restore CA2100
                command.CommandTimeout = 30;
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        backups[reader.GetString(0)] = (reader.IsDBNull(1) ? null : reader.GetDateTime(1), reader.IsDBNull(2) ? null : reader.GetDateTime(2));
                    }

                    await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (!reader.IsDBNull(0))
                        {
                            suspect[reader.GetString(0)] = reader.GetInt32(1);
                        }
                    }
                }
            }

            return (backups, suspect);
        }
        catch (SqlException)
        {
            return (null, null);
        }
    }

    private static int? Int(SqlDataReader reader, int i) => reader.IsDBNull(i) ? null : Convert.ToInt32(reader.GetValue(i), CultureInfo.InvariantCulture);
}
