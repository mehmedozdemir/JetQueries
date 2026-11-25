using System.Globalization;
using JetQueries.Sdk;
using Oracle.ManagedDataAccess.Client;

namespace JetQueries.Provider.Oracle;

/// <summary>
/// The DBA screens on Oracle, from views that need no extra licence: V$SESSION / V$SQL (sessions, blocking),
/// V$SQLAREA and V$SQL_PLAN (heavy statements, full table scans, index use), V$SYSSTAT, V$SYSMETRIC, V$SYSTEM_EVENT,
/// V$OSSTAT (load), DBA_* views (health rules, a schema in depth) and RMAN's V$RMAN_BACKUP_JOB_DETAILS. AWR and ASH
/// (Diagnostics Pack) are never read. The login needs SELECT_CATALOG_ROLE (ending a session: ALTER SYSTEM).
/// In this application an Oracle "database" is a schema.
/// </summary>
internal static class OracleDba
{
    private const string Marker = "/* JetQueries:advisor */ ";

    private const string NotMaintained = "(SELECT username FROM dba_users WHERE oracle_maintained = 'N')";

    // Activity ----------------------------------------------------------------------------------------------------

    private const string SessionsSql = Marker + """
        SELECT s.sid || ',' || s.serial#, s.username, s.machine, s.program, s.schemaname, s.status, c.command_name,
            CASE WHEN s.state = 'WAITING' AND s.wait_class <> 'Idle' THEN s.event END,
            CASE WHEN s.state = 'WAITING' AND s.wait_class <> 'Idle' THEN s.wait_time_micro / 1000 END,
            CASE WHEN s.blocking_session IS NOT NULL THEN b.sid || ',' || b.serial# END,
            CASE WHEN s.status = 'ACTIVE' THEN s.last_call_et * 1000 END,
            (SELECT t.value * 10 FROM v$sesstat t JOIN v$statname n ON n.statistic# = t.statistic# WHERE t.sid = s.sid AND n.name = 'CPU used by this session'),
            (SELECT t.value FROM v$sesstat t JOIN v$statname n ON n.statistic# = t.statistic# WHERE t.sid = s.sid AND n.name = 'session logical reads'),
            CASE WHEN s.taddr IS NOT NULL THEN 1 ELSE 0 END,
            s.logon_time,
            CASE WHEN s.status <> 'ACTIVE' THEN s.last_call_et END,
            (SELECT q.sql_text FROM v$sql q WHERE q.sql_id = NVL(s.sql_id, s.prev_sql_id) AND ROWNUM = 1),
            CASE WHEN s.type = 'USER' THEN 1 ELSE 0 END
        FROM v$session s
        LEFT JOIN v$session b ON b.sid = s.blocking_session
        LEFT JOIN v$sqlcommand c ON c.command_type = s.command AND s.command <> 0
        ORDER BY s.sid
        """;

    public static async Task<ActivitySnapshot> ActivityAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        var sessions = new List<ActivitySession>();
        var now = DateTimeOffset.Now;
        await RowsAsync(connection, SessionsSql, null, reader =>
        {
            var idleFor = Long(reader, 15);
            sessions.Add(new ActivitySession(reader.GetString(0))
            {
                Login = Text(reader, 1),
                Host = Text(reader, 2),
                Program = Text(reader, 3),
                Database = Text(reader, 4),
                Status = Text(reader, 5)?.ToLowerInvariant(),
                Command = Text(reader, 5) == "ACTIVE" ? Text(reader, 6) : null,
                WaitType = Text(reader, 7),
                WaitTime = Milliseconds(reader, 8),
                BlockedBy = Text(reader, 9),
                Elapsed = Milliseconds(reader, 10),
                CpuTime = Milliseconds(reader, 11),
                Reads = Long(reader, 12),
                OpenTransactions = (int?)Long(reader, 13),
                LoginTime = Date(reader, 14),
                LastRequestEnd = idleFor is { } seconds ? now - TimeSpan.FromSeconds(seconds) : null,
                Sql = Text(reader, 16)?.Trim(),
                IsUserSession = Long(reader, 17) == 1,
            });
        }, cancellationToken).ConfigureAwait(false);
        var own = await ScalarAsync(connection, Marker + "SELECT s.sid || ',' || s.serial# FROM v$session s WHERE s.sid = SYS_CONTEXT('USERENV', 'SID')", cancellationToken)
            .ConfigureAwait(false);
        return new ActivitySnapshot(now, sessions, Convert.ToString(own, CultureInfo.InvariantCulture));
    }

    public static async Task KillAsync(OracleConnection connection, string sessionId, CancellationToken cancellationToken)
    {
        // ALTER SYSTEM takes no bind variables: "sid,serial#" is checked to be two numbers and written as them.
        var parts = sessionId.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var sid) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var serial))
        {
            throw new ArgumentException("Not an Oracle session (sid,serial#): " + sessionId, nameof(sessionId));
        }

        await ExecuteAsync(connection, "ALTER SYSTEM KILL SESSION '" + sid.ToString(CultureInfo.InvariantCulture) + "," + serial.ToString(CultureInfo.InvariantCulture) + "' IMMEDIATE",
            cancellationToken).ConfigureAwait(false);
    }

    // Performance -------------------------------------------------------------------------------------------------

    private const string HeavySql = Marker + """
        SELECT a.sql_fulltext, a.parsing_schema_name, a.executions, a.cpu_time, a.elapsed_time, a.buffer_gets, a.direct_writes, a.rows_processed, a.last_active_time
        FROM v$sqlarea a
        WHERE a.executions > 0 AND (:db IS NULL OR a.parsing_schema_name = :db)
          AND a.parsing_schema_name NOT IN (SELECT username FROM dba_users WHERE oracle_maintained = 'Y')
          AND a.sql_text NOT LIKE '%JetQueries:advisor%' AND INSTR(a.sql_text, 'dbms_stats') = 0
        ORDER BY CASE :ord
            WHEN 0 THEN a.cpu_time WHEN 1 THEN a.cpu_time / a.executions WHEN 2 THEN a.elapsed_time WHEN 3 THEN a.elapsed_time / a.executions
            WHEN 4 THEN a.buffer_gets WHEN 5 THEN a.buffer_gets / a.executions ELSE a.executions END DESC
        FETCH FIRST :top ROWS ONLY
        """;

    public static async Task<HeavyQueryReport> HeavyAsync(OracleConnection connection, HeavyQueryRequest request, CancellationToken cancellationToken)
    {
        var queries = new List<HeavyQuery>();
        await RowsAsync(connection, HeavySql, command =>
        {
            command.Parameters.Add("db", request.Database);
            command.Parameters.Add("ord", (int)request.Order);
            command.Parameters.Add("top", Math.Clamp(request.Top, 1, 1000));
        }, reader => queries.Add(new HeavyQuery(Text(reader, 0)?.Trim() ?? string.Empty)
        {
            Database = Text(reader, 1),
            Executions = Long(reader, 2) ?? 0,
            TotalCpu = TimeSpan.FromMicroseconds(Long(reader, 3) ?? 0),
            TotalDuration = TimeSpan.FromMicroseconds(Long(reader, 4) ?? 0),
            TotalReads = Long(reader, 5) ?? 0,
            TotalWrites = Long(reader, 6) ?? 0,
            TotalRows = Long(reader, 7),
            LastExecution = Date(reader, 8),
        }), cancellationToken).ConfigureAwait(false);
        return new HeavyQueryReport(queries, await StartupAsync(connection, cancellationToken).ConfigureAwait(false));
    }

    // Statements Oracle runs for itself (the dictionary, DBMS_STATS's sampling queries — parsed as the table's owner) are left out: they are not the application's reads.
    // Oracle keeps no missing-index statistics without the licensed advisors: big tables read by full scans in
    // the cursor cache are what wants an index.
    private const string FullScansSql = Marker + """
        SELECT p.object_owner, p.object_name, SUM(q.executions), MAX(t.num_rows)
        FROM v$sql_plan p
        JOIN v$sql q ON q.sql_id = p.sql_id AND q.child_number = p.child_number
        JOIN dba_tables t ON t.owner = p.object_owner AND t.table_name = p.object_name
        WHERE p.operation = 'TABLE ACCESS' AND p.options LIKE 'FULL%' AND t.num_rows >= 10000
          AND (:db IS NULL OR p.object_owner = :db)
          AND p.object_owner IN (SELECT username FROM dba_users WHERE oracle_maintained = 'N')
          AND q.sql_text NOT LIKE '%JetQueries:advisor%' AND q.parsing_schema_name NOT IN (SELECT username FROM dba_users WHERE oracle_maintained = 'Y') AND INSTR(q.sql_text, 'dbms_stats') = 0
        GROUP BY p.object_owner, p.object_name
        ORDER BY 3 DESC
        FETCH FIRST 100 ROWS ONLY
        """;

    public static async Task<IReadOnlyList<MissingIndex>> FullScansAsync(OracleConnection connection, string? schema, CancellationToken cancellationToken)
    {
        var tables = new List<MissingIndex>();
        await RowsAsync(connection, FullScansSql, command => command.Parameters.Add("db", schema), reader =>
        {
            var owner = reader.GetString(0);
            var table = reader.GetString(1);
            var executions = Long(reader, 2) ?? 0;
            var rows = Long(reader, 3) ?? 0;
            tables.Add(new MissingIndex(owner, owner + "." + table,
                "-- Oracle keeps no missing-index statistics without its licensed advisors. " + owner + "." + table + " (" +
                rows.ToString(CultureInfo.InvariantCulture) + " rows) is read by full scans in cached statements run " + executions.ToString(CultureInfo.InvariantCulture) + " times.\n" +
                "-- Find the WHERE / JOIN columns of those statements (Heavy queries) and index them, for example:\n" +
                "-- CREATE INDEX " + OracleNames.Quote(owner) + "." + OracleNames.Quote(table.Length > 24 ? table[..24] + "_IX" : table + "_IX") + " ON " + OracleNames.Quote(owner) + "." + OracleNames.Quote(table) + " (column) ONLINE;")
            {
                Uses = executions,
                ImpactPercent = 0,
                Score = (double)executions * rows,
            });
        }, cancellationToken).ConfigureAwait(false);
        return tables;
    }

    private const string IndexesSql = Marker + """
        SELECT i.table_name, i.index_name, i.uniqueness, i.index_type, i.num_rows,
            (SELECT SUM(s.bytes) / 1024 FROM dba_segments s WHERE s.owner = i.owner AND s.segment_name = i.index_name),
            (SELECT c.constraint_type FROM dba_constraints c WHERE c.owner = i.owner AND c.index_name = i.index_name AND c.constraint_type IN ('P', 'U') AND ROWNUM = 1),
            (SELECT NVL(SUM(q.executions), 0) FROM v$sql_plan p JOIN v$sql q ON q.sql_id = p.sql_id AND q.child_number = p.child_number
             WHERE p.object_owner = i.owner AND p.object_name = i.index_name AND p.operation = 'INDEX' AND q.parsing_schema_name NOT IN (SELECT username FROM dba_users WHERE oracle_maintained = 'Y') AND INSTR(q.sql_text, 'dbms_stats') = 0 AND p.options IN ('UNIQUE SCAN', 'RANGE SCAN', 'RANGE SCAN DESCENDING', 'SKIP SCAN')),
            (SELECT NVL(SUM(q.executions), 0) FROM v$sql_plan p JOIN v$sql q ON q.sql_id = p.sql_id AND q.child_number = p.child_number
             WHERE p.object_owner = i.owner AND p.object_name = i.index_name AND p.operation = 'INDEX' AND q.parsing_schema_name NOT IN (SELECT username FROM dba_users WHERE oracle_maintained = 'Y') AND INSTR(q.sql_text, 'dbms_stats') = 0 AND p.options IN ('FULL SCAN', 'FAST FULL SCAN', 'FULL SCAN (MIN/MAX)'))
        FROM dba_indexes i
        WHERE i.owner = :owner AND i.index_type NOT IN ('LOB', 'IOT - TOP')
        ORDER BY i.table_name, i.index_name
        """;

    private const string IndexColumnsSql = Marker + """
        SELECT c.index_name, c.column_name, c.descend FROM dba_ind_columns c WHERE c.index_owner = :owner ORDER BY c.index_name, c.column_position
        """;

    public static async Task<IndexUsageReport> UsageAsync(OracleConnection connection, string schema, CancellationToken cancellationToken)
    {
        var columns = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await RowsAsync(connection, IndexColumnsSql, command => command.Parameters.Add("owner", schema), reader =>
        {
            var name = reader.GetString(0);
            if (!columns.TryGetValue(name, out var list))
            {
                columns[name] = list = [];
            }

            list.Add(reader.GetString(1) + (Text(reader, 2) == "DESC" ? " DESC" : string.Empty));
        }, cancellationToken).ConfigureAwait(false);

        var indexes = new List<IndexUsage>();
        await RowsAsync(connection, IndexesSql, command => command.Parameters.Add("owner", schema), reader =>
        {
            var name = reader.GetString(1);
            var constraint = Text(reader, 6) is not null;
            indexes.Add(new IndexUsage(schema + "." + reader.GetString(0), name)
            {
                IsUnique = Text(reader, 2) == "UNIQUE",
                IsConstraint = constraint,
                KeyColumns = columns.GetValueOrDefault(name) ?? [],
                Rows = Long(reader, 4),
                SizeKb = Long(reader, 5) ?? 0,
                Seeks = Long(reader, 7) ?? 0,
                Scans = Long(reader, 8) ?? 0,
                DropScript = constraint ? null : "DROP INDEX " + OracleNames.Quote(schema) + "." + OracleNames.Quote(name) + ";",
            });
        }, cancellationToken).ConfigureAwait(false);
        return new IndexUsageReport(indexes, await StartupAsync(connection, cancellationToken).ConfigureAwait(false));
    }

    public static Task<IReadOnlyList<IndexFragmentation>> FragmentationAsync() =>
        throw new InvalidOperationException(
            "Oracle does not report index fragmentation without ANALYZE INDEX … VALIDATE STRUCTURE, which locks the table; B-tree indexes rarely need rebuilding. Use the Segment Advisor (DBMS_SPACE) to find space worth reclaiming.");

    // Dashboard ---------------------------------------------------------------------------------------------------

    public static async Task<ServerOverview> OverviewAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        string? banner = null, version = null, host = null;
        DateTimeOffset? started = null;
        await RowsAsync(connection, Marker + "SELECT v.banner, i.version_full, i.host_name || ' / ' || i.instance_name, i.startup_time FROM v$version v CROSS JOIN v$instance i WHERE ROWNUM = 1",
            null, reader =>
            {
                banner = Text(reader, 0);
                version = Text(reader, 1);
                host = Text(reader, 2);
                started = Date(reader, 3);
            }, cancellationToken).ConfigureAwait(false);

        long? cpus = null, memory = null;
        await RowsAsync(connection, Marker + "SELECT stat_name, value FROM v$osstat WHERE stat_name IN ('NUM_CPUS', 'PHYSICAL_MEMORY_BYTES')", null, reader =>
        {
            if (reader.GetString(0) == "NUM_CPUS")
            {
                cpus = Long(reader, 1);
            }
            else
            {
                memory = Long(reader, 1) / 1024 / 1024;
            }
        }, cancellationToken).ConfigureAwait(false);

        // The application's "databases" on Oracle are schemas: each with the space of its segments.
        var mode = Convert.ToString(await ScalarAsync(connection, Marker + "SELECT log_mode FROM v$database", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        var schemas = new List<DatabaseSummary>();
        await RowsAsync(connection, Marker + """
            SELECT u.username, u.account_status, (SELECT NVL(SUM(s.bytes), 0) / 1048576 FROM dba_segments s WHERE s.owner = u.username)
            FROM dba_users u WHERE u.oracle_maintained = 'N' ORDER BY u.username
            """, null, reader => schemas.Add(new DatabaseSummary(reader.GetString(0))
        {
            State = Text(reader, 1), RecoveryModel = mode, DataMb = Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture),
        }), cancellationToken).ConfigureAwait(false);

        // CPU of the last hour, a point a minute (V$SYSMETRIC_HISTORY needs no Diagnostics Pack).
        var history = new List<CpuPoint>();
        await RowsAsync(connection, CpuHistorySql, command => command.Parameters.Add("cpus", cpus ?? 1), reader =>
        {
            if (Date(reader, 0) is { } at)
            {
                history.Add(new CpuPoint(at, Double(reader, 1), Math.Max(0, Double(reader, 2) - Double(reader, 1))));
            }
        }, cancellationToken).ConfigureAwait(false);

        var product = banner is null ? "Oracle Database" : banner.Split(" Release", StringSplitOptions.None)[0];
        return new ServerOverview(product, version ?? string.Empty)
        {
            Host = host, StartedAt = started, CpuCount = (int?)cpus, PhysicalMemoryMb = memory, Databases = schemas, CpuHistory = history,
        };
    }

    private const string CpuHistorySql = Marker + """
        SELECT h.end_time,
            NVL(MAX(CASE WHEN h.metric_name = 'CPU Usage Per Sec' THEN h.value END), 0) / :cpus,
            NVL(MAX(CASE WHEN h.metric_name = 'Host CPU Utilization (%)' THEN h.value END), 0)
        FROM v$sysmetric_history h
        WHERE h.group_id = 2 AND h.metric_name IN ('CPU Usage Per Sec', 'Host CPU Utilization (%)')
        GROUP BY h.end_time ORDER BY h.end_time
        """;

    private const string StatsSql = Marker + """
        SELECT name, value FROM v$sysstat WHERE name IN ('user calls', 'parse count (hard)', 'user commits', 'user rollbacks',
            'physical read total bytes', 'physical write total bytes', 'physical read total IO requests', 'physical write total IO requests')
        """;

    public static async Task<ServerSample> SampleAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        var stats = new Dictionary<string, long>(StringComparer.Ordinal);
        await RowsAsync(connection, StatsSql, null, reader => stats[reader.GetString(0)] = Long(reader, 1) ?? 0, cancellationToken).ConfigureAwait(false);

        long? cpus = null, free = null;
        await RowsAsync(connection, Marker + "SELECT stat_name, value FROM v$osstat WHERE stat_name IN ('NUM_CPUS', 'FREE_MEMORY_BYTES')", null, reader =>
        {
            if (reader.GetString(0) == "NUM_CPUS")
            {
                cpus = Long(reader, 1);
            }
            else
            {
                free = Long(reader, 1) / 1024 / 1024;
            }
        }, cancellationToken).ConfigureAwait(false);

        CpuPoint? cpu = null;
        await RowsAsync(connection, CpuHistorySql.Replace("v$sysmetric_history", "v$sysmetric", StringComparison.Ordinal), command => command.Parameters.Add("cpus", cpus ?? 1),
            reader =>
            {
                if (Date(reader, 0) is { } at)
                {
                    cpu = new CpuPoint(at, Double(reader, 1), Math.Max(0, Double(reader, 2) - Double(reader, 1)));
                }
            }, cancellationToken).ConfigureAwait(false);

        long? used = null, target = null;
        await RowsAsync(connection, Marker + """
            SELECT (SELECT SUM(value) FROM v$sga) / 1048576 + (SELECT value FROM v$pgastat WHERE name = 'total PGA allocated') / 1048576,
                (SELECT SUM(TO_NUMBER(value)) FROM v$parameter WHERE name IN ('sga_target', 'pga_aggregate_target')) / 1048576
            FROM dual
            """, null, reader =>
        {
            used = Long(reader, 0);
            target = Long(reader, 1);
        }, cancellationToken).ConfigureAwait(false);

        int? connections = null, active = null, blocked = null;
        long? stall = null, operations = null;
        await RowsAsync(connection, Marker + """
            SELECT (SELECT COUNT(*) FROM v$session WHERE type = 'USER'),
                (SELECT COUNT(*) FROM v$session WHERE type = 'USER' AND status = 'ACTIVE' AND sid <> SYS_CONTEXT('USERENV', 'SID')),
                (SELECT COUNT(*) FROM v$session WHERE blocking_session IS NOT NULL),
                (SELECT SUM(readtim + writetim) * 10 FROM v$filestat),
                (SELECT SUM(phyrds + phywrts) FROM v$filestat)
            FROM dual
            """, null, reader =>
        {
            connections = (int?)Long(reader, 0);
            active = (int?)Long(reader, 1);
            blocked = (int?)Long(reader, 2);
            stall = Long(reader, 3);
            operations = Long(reader, 4);
        }, cancellationToken).ConfigureAwait(false);

        var waits = new List<WaitTotal>();
        await RowsAsync(connection, Marker + """
            SELECT event, time_waited_micro / 1000 FROM v$system_event WHERE wait_class <> 'Idle' ORDER BY time_waited_micro DESC FETCH FIRST 12 ROWS ONLY
            """, null, reader => waits.Add(new WaitTotal(reader.GetString(0), Long(reader, 1) ?? 0)), cancellationToken).ConfigureAwait(false);

        return new ServerSample(DateTimeOffset.Now)
        {
            Cpu = cpu,
            MemoryUsedMb = used,
            MemoryTargetMb = target is > 0 ? target : null,
            AvailableMemoryMb = free,
            Connections = connections,
            ActiveRequests = active,
            BlockedRequests = blocked,
            BatchRequests = stats.GetValueOrDefault("user calls"),
            Compilations = stats.GetValueOrDefault("parse count (hard)"),
            Transactions = stats.GetValueOrDefault("user commits") + stats.GetValueOrDefault("user rollbacks"),
            BytesRead = stats.GetValueOrDefault("physical read total bytes"),
            BytesWritten = stats.GetValueOrDefault("physical write total bytes"),
            IoOperations = operations,
            IoStallMs = stall,
            Waits = waits,
        };
    }

    // Health ------------------------------------------------------------------------------------------------------

    public static async Task<IReadOnlyList<HealthFinding>> HealthAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        var findings = new List<HealthFinding>();
        var mode = Convert.ToString(await ScalarAsync(connection, Marker + "SELECT log_mode FROM v$database", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        findings.Add(mode == "NOARCHIVELOG"
            ? new HealthFinding("ArchiveLog", HealthCategory.Backups, HealthSeverity.Warning, "The database runs in NOARCHIVELOG mode") { Current = "NOARCHIVELOG", Recommended = "ARCHIVELOG" }
            : Ok("ArchiveLog", HealthCategory.Backups, "The database runs in NOARCHIVELOG mode"));

        // RMAN's own record of backups; without access it is unknown, not missing.
        try
        {
            var last = await ScalarAsync(connection, Marker + """
                SELECT MAX(end_time) FROM v$rman_backup_job_details WHERE status LIKE 'COMPLETED%' AND input_type IN ('DB FULL', 'DB INCR')
                """, cancellationToken).ConfigureAwait(false);
            var when = last is DateTime time ? new DateTimeOffset(time) : (DateTimeOffset?)null;
            findings.Add(when is { } finished && DateTimeOffset.Now - finished <= TimeSpan.FromDays(7)
                ? Ok("RmanBackup", HealthCategory.Backups, "No recent RMAN backup")
                : new HealthFinding("RmanBackup", HealthCategory.Backups, HealthSeverity.Critical, "No recent RMAN backup")
                {
                    Current = when?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—",
                });
        }
        catch (OracleException)
        {
            findings.Add(new HealthFinding("Backups", HealthCategory.Backups, HealthSeverity.Info, "Backups could not be read"));
        }

        var area = new List<string>();
        var areaSeverity = HealthSeverity.Ok;
        await RowsAsync(connection, Marker + "SELECT name, space_limit, space_used, space_reclaimable FROM v$recovery_file_dest WHERE space_limit > 0", null, reader =>
        {
            var limit = Double(reader, 1);
            var used = Double(reader, 2) - Double(reader, 3);
            var percent = limit > 0 ? used * 100 / limit : 0;
            if (percent > 85)
            {
                area.Add(reader.GetString(0) + " (" + percent.ToString("0", CultureInfo.InvariantCulture) + " %)");
                areaSeverity = percent > 95 ? HealthSeverity.Critical : HealthSeverity.Warning;
            }
        }, cancellationToken).ConfigureAwait(false);
        findings.Add(area.Count == 0
            ? Ok("RecoveryArea", HealthCategory.Backups, "The recovery area is nearly full")
            : new HealthFinding("RecoveryArea", HealthCategory.Backups, areaSeverity, "The recovery area is nearly full") { Objects = area });

        var full = new List<string>();
        var fullSeverity = HealthSeverity.Ok;
        await RowsAsync(connection, Marker + "SELECT tablespace_name, used_percent FROM dba_tablespace_usage_metrics WHERE used_percent > 85 ORDER BY used_percent DESC", null, reader =>
        {
            var percent = Double(reader, 1);
            full.Add(reader.GetString(0) + " (" + percent.ToString("0", CultureInfo.InvariantCulture) + " %)");
            fullSeverity = percent > 95 || fullSeverity == HealthSeverity.Critical ? HealthSeverity.Critical : HealthSeverity.Warning;
        }, cancellationToken).ConfigureAwait(false);
        findings.Add(full.Count == 0
            ? Ok("TablespaceFull", HealthCategory.Databases, "A tablespace is nearly full")
            : new HealthFinding("TablespaceFull", HealthCategory.Databases, fullSeverity, "A tablespace is nearly full") { Objects = full });

        var invalid = new List<(string Owner, int Count)>();
        await RowsAsync(connection, Marker + "SELECT owner, COUNT(*) FROM dba_objects WHERE status = 'INVALID' AND owner IN " + NotMaintained + " GROUP BY owner ORDER BY 2 DESC",
            null, reader => invalid.Add((reader.GetString(0), (int)(Long(reader, 1) ?? 0))), cancellationToken).ConfigureAwait(false);
        findings.Add(invalid.Count == 0
            ? Ok("InvalidObjects", HealthCategory.Databases, "Objects are invalid")
            : new HealthFinding("InvalidObjects", HealthCategory.Databases, HealthSeverity.Warning, "Objects are invalid")
            {
                Objects = [.. invalid.Select(static i => i.Owner + " (" + i.Count.ToString(CultureInfo.InvariantCulture) + ")")],
                FixScript = string.Join("\n", invalid.Select(static i => "EXEC DBMS_UTILITY.COMPILE_SCHEMA(schema => " + Literal(i.Owner) + ", compile_all => FALSE);")),
            });

        var unusable = new List<(string Owner, string Name)>();
        await RowsAsync(connection, Marker + "SELECT owner, index_name FROM dba_indexes WHERE status = 'UNUSABLE' AND owner IN " + NotMaintained,
            null, reader => unusable.Add((reader.GetString(0), reader.GetString(1))), cancellationToken).ConfigureAwait(false);
        findings.Add(unusable.Count == 0
            ? Ok("UnusableIndexes", HealthCategory.Databases, "Indexes are unusable")
            : new HealthFinding("UnusableIndexes", HealthCategory.Databases, HealthSeverity.Critical, "Indexes are unusable")
            {
                Objects = [.. unusable.Select(static i => i.Owner + "." + i.Name)],
                FixScript = string.Join("\n", unusable.Select(static i => "ALTER INDEX " + OracleNames.Quote(i.Owner) + "." + OracleNames.Quote(i.Name) + " REBUILD ONLINE;")),
            });

        var stale = new List<(string Owner, int Count)>();
        await RowsAsync(connection, Marker + """
            SELECT owner, COUNT(*) FROM dba_tab_statistics
            WHERE owner IN (SELECT username FROM dba_users WHERE oracle_maintained = 'N') AND object_type = 'TABLE' AND (stale_stats = 'YES' OR last_analyzed IS NULL)
            GROUP BY owner ORDER BY 2 DESC
            """, null, reader => stale.Add((reader.GetString(0), (int)(Long(reader, 1) ?? 0))), cancellationToken).ConfigureAwait(false);
        findings.Add(stale.Count == 0
            ? Ok("OracleStats", HealthCategory.Performance, "Optimizer statistics are stale or missing")
            : new HealthFinding("OracleStats", HealthCategory.Performance, HealthSeverity.Warning, "Optimizer statistics are stale or missing")
            {
                Objects = [.. stale.Select(static s => s.Owner + " (" + s.Count.ToString(CultureInfo.InvariantCulture) + ")")],
                FixScript = string.Join("\n", stale.Select(static s => "EXEC DBMS_STATS.GATHER_SCHEMA_STATS(ownname => " + Literal(s.Owner) + ", options => 'GATHER AUTO');")),
            });

        var defaults = new List<string>();
        try
        {
            await RowsAsync(connection, Marker + """
                SELECT d.username FROM dba_users_with_defpwd d JOIN dba_users u ON u.username = d.username WHERE u.account_status = 'OPEN' ORDER BY 1
                """, null, reader => defaults.Add(reader.GetString(0)), cancellationToken).ConfigureAwait(false);
        }
        catch (OracleException)
        {
        }

        findings.Add(defaults.Count == 0
            ? Ok("DefaultPasswords", HealthCategory.Security, "Accounts use default passwords")
            : new HealthFinding("DefaultPasswords", HealthCategory.Security, HealthSeverity.Warning, "Accounts use default passwords") { Objects = defaults });

        var dbas = new List<string>();
        await RowsAsync(connection, Marker + """
            SELECT p.grantee FROM dba_role_privs p WHERE p.granted_role = 'DBA'
              AND p.grantee NOT IN (SELECT username FROM dba_users WHERE oracle_maintained = 'Y')
              AND p.grantee NOT IN (SELECT role FROM dba_roles) ORDER BY 1
            """, null, reader => dbas.Add(reader.GetString(0)), cancellationToken).ConfigureAwait(false);
        findings.Add(dbas.Count > 3
            ? new HealthFinding("DbaRole", HealthCategory.Security, HealthSeverity.Info, "Many accounts have the DBA role")
            {
                Current = dbas.Count.ToString(CultureInfo.InvariantCulture), Recommended = "≤ 3", Objects = dbas,
            }
            : Ok("DbaRole", HealthCategory.Security, "Many accounts have the DBA role"));

        var limits = new List<string>();
        await RowsAsync(connection, Marker + """
            SELECT resource_name, current_utilization, max_utilization, limit_value FROM v$resource_limit
            WHERE resource_name IN ('processes', 'sessions') AND TRIM(limit_value) NOT IN ('UNLIMITED', '0')
            """, null, reader =>
        {
            var limit = double.TryParse(Text(reader, 3), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
            if (limit > 0 && Double(reader, 2) / limit > 0.8)
            {
                limits.Add(reader.GetString(0) + " (" + Long(reader, 2)?.ToString(CultureInfo.InvariantCulture) + " / " + Text(reader, 3)?.Trim() + ")");
            }
        }, cancellationToken).ConfigureAwait(false);
        findings.Add(limits.Count == 0
            ? Ok("ProcessLimit", HealthCategory.Configuration, "Sessions or processes come close to their limit")
            : new HealthFinding("ProcessLimit", HealthCategory.Configuration, HealthSeverity.Warning, "Sessions or processes come close to their limit") { Objects = limits });

        var started = await StartupAsync(connection, cancellationToken).ConfigureAwait(false);
        findings.Add(started is { } start && DateTimeOffset.Now - start < TimeSpan.FromDays(1)
            ? new HealthFinding("Uptime", HealthCategory.Performance, HealthSeverity.Info, "The server started less than a day ago")
            {
                Current = start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            }
            : Ok("Uptime", HealthCategory.Performance, "The server started less than a day ago"));
        return findings;
    }

    // Schema detail -----------------------------------------------------------------------------------------------

    public static async Task<DatabaseDetail> DetailAsync(OracleConnection connection, string schema, CancellationToken cancellationToken)
    {
        DatabaseDetail? detail = null;
        await RowsAsync(connection, Marker + """
            SELECT u.username, u.account_status, u.created, u.default_tablespace, u.temporary_tablespace, u.profile, u.expiry_date, u.authentication_type
            FROM dba_users u WHERE u.username = :owner
            """, command => command.Parameters.Add("owner", schema), reader => detail = new DatabaseDetail(reader.GetString(0))
        {
            State = Text(reader, 1),
            Created = Date(reader, 2),
            Options =
            [
                new("accountStatus", "Account status", Text(reader, 1)),
                new("defaultTablespace", "Default tablespace", Text(reader, 3)),
                new("temporaryTablespace", "Temporary tablespace", Text(reader, 4)),
                new("profile", "Profile", Text(reader, 5)),
                new("passwordExpires", "Password expires", Date(reader, 6)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                new("authentication", "Authentication", Text(reader, 7)),
            ],
        }, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            throw new ArgumentException("No schema named " + schema + " in this database.", nameof(schema));
        }

        var mode = Convert.ToString(await ScalarAsync(connection, Marker + "SELECT log_mode FROM v$database", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        double dataMb = 0;
        await RowsAsync(connection, Marker + "SELECT NVL(SUM(bytes), 0) / 1048576 FROM dba_segments WHERE owner = :owner", command => command.Parameters.Add("owner", schema),
            reader => dataMb = Double(reader, 0), cancellationToken).ConfigureAwait(false);

        int tableCount = 0;
        long rows = 0;
        await RowsAsync(connection, Marker + "SELECT COUNT(*), NVL(SUM(num_rows), 0) FROM dba_tables WHERE owner = :owner", command => command.Parameters.Add("owner", schema), reader =>
        {
            tableCount = (int)(Long(reader, 0) ?? 0);
            rows = Long(reader, 1) ?? 0;
        }, cancellationToken).ConfigureAwait(false);

        var tables = new List<TableSpace>();
        await RowsAsync(connection, Marker + """
            SELECT t.table_name, NVL(t.num_rows, 0),
                (SELECT NVL(SUM(s.bytes), 0) / 1048576 FROM dba_segments s WHERE s.owner = t.owner AND s.segment_name = t.table_name),
                (SELECT NVL(SUM(s.bytes), 0) / 1048576 FROM dba_segments s JOIN dba_indexes i ON i.owner = s.owner AND i.index_name = s.segment_name
                 WHERE i.table_owner = t.owner AND i.table_name = t.table_name)
            FROM dba_tables t WHERE t.owner = :owner
            ORDER BY 3 + 4 DESC FETCH FIRST 50 ROWS ONLY
            """, command => command.Parameters.Add("owner", schema), reader =>
        {
            var data = Double(reader, 2);
            var index = Double(reader, 3);
            tables.Add(new TableSpace(schema + "." + reader.GetString(0)) { Rows = Long(reader, 1) ?? 0, DataMb = data, IndexMb = index, ReservedMb = data + index });
        }, cancellationToken).ConfigureAwait(false);

        // The data files of the tablespaces the schema's segments live in.
        var files = new List<DatabaseFileInfo>();
        await RowsAsync(connection, Marker + """
            SELECT f.file_name, f.tablespace_name, f.bytes / 1048576, f.autoextensible, f.increment_by * t.block_size / 1048576, f.maxbytes / 1048576
            FROM dba_data_files f JOIN dba_tablespaces t ON t.tablespace_name = f.tablespace_name
            WHERE f.tablespace_name IN (SELECT DISTINCT s.tablespace_name FROM dba_segments s WHERE s.owner = :owner
                                        UNION SELECT u.default_tablespace FROM dba_users u WHERE u.username = :owner)
            ORDER BY f.tablespace_name, f.file_name
            """, command => command.Parameters.Add("owner", schema), reader =>
        {
            var path = reader.GetString(0);
            var auto = Text(reader, 3) == "YES";
            files.Add(new DatabaseFileInfo(Path.GetFileName(path))
            {
                Type = "DATA",
                Filegroup = Text(reader, 1),
                Path = path,
                SizeMb = Double(reader, 2),
                Growth = auto ? Double(reader, 4).ToString("0.#", CultureInfo.InvariantCulture) + " MB" : "0",
                MaxSize = auto ? Double(reader, 5).ToString("0", CultureInfo.InvariantCulture) + " MB" : "0",
            });
        }, cancellationToken).ConfigureAwait(false);

        double logMb = 0;
        await RowsAsync(connection, Marker + "SELECT NVL(SUM(bytes * members), 0) / 1048576 FROM v$log", null, reader => logMb = Double(reader, 0), cancellationToken).ConfigureAwait(false);

        // RMAN backups are of the whole database (every schema shares them).
        var backups = new List<BackupRecord>();
        var known = true;
        try
        {
            await RowsAsync(connection, Marker + """
                SELECT input_type, start_time, end_time, input_bytes / 1048576, output_bytes / 1048576, output_device_type
                FROM v$rman_backup_job_details WHERE status LIKE 'COMPLETED%' AND end_time IS NOT NULL ORDER BY end_time DESC FETCH FIRST 30 ROWS ONLY
                """, null, reader =>
            {
                var kind = Text(reader, 0) switch
                {
                    "DB FULL" => BackupKind.Full,
                    "DB INCR" => BackupKind.Differential,
                    "ARCHIVELOG" => BackupKind.Log,
                    _ => BackupKind.Other,
                };
                if (Date(reader, 2) is { } end)
                {
                    backups.Add(new BackupRecord(kind, end) { Started = Date(reader, 1), SizeMb = Double(reader, 3), CompressedMb = Double(reader, 4), Device = Text(reader, 5) });
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OracleException)
        {
            known = false;
        }

        return detail with
        {
            RecoveryModel = mode,
            DataMb = dataMb,
            LogMb = logMb,
            TableCount = tableCount,
            TotalRows = rows,
            Tables = tables,
            Files = files,
            Backups = backups,
            BackupsKnown = known,
        };
    }

    // Helpers -----------------------------------------------------------------------------------------------------

    private static async Task<DateTimeOffset?> StartupAsync(OracleConnection connection, CancellationToken cancellationToken) =>
        await ScalarAsync(connection, Marker + "SELECT startup_time FROM v$instance", cancellationToken).ConfigureAwait(false) is DateTime time ? new DateTimeOffset(time) : null;

    private static async Task RowsAsync(OracleConnection connection, string sql, Action<OracleCommand>? binds, Action<OracleDataReader> row, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Constant text; values are bind variables.
            command.CommandText = sql;
#pragma warning restore CA2100
            command.BindByName = true;
            command.CommandTimeout = 60;
            binds?.Invoke(command);
            try
            {
                var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        row(reader);
                    }
                }
            }
            catch (OracleException ex) when (ex.Number == 942)
            {
                throw Permission(ex);
            }
        }
    }

    private static async Task<object?> ScalarAsync(OracleConnection connection, string sql, CancellationToken cancellationToken)
    {
        object? value = null;
        await RowsAsync(connection, sql, null, reader => value = reader.IsDBNull(0) ? null : reader.GetValue(0), cancellationToken).ConfigureAwait(false);
        return value;
    }

    private static async Task ExecuteAsync(OracleConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Validated numbers only.
            command.CommandText = sql;
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ORA-00942 on a V$ or DBA_ view means the login lacks the dictionary role, not that the view is missing.
    private static InvalidOperationException Permission(OracleException ex) =>
        new("This screen reads Oracle's dynamic performance (V$) and DBA_ views: the login needs SELECT_CATALOG_ROLE (or SELECT ANY DICTIONARY). " + ex.Message, ex);

    private static HealthFinding Ok(string id, HealthCategory category, string title) => new(id, category, HealthSeverity.Ok, title);

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string? Text(OracleDataReader reader, int i) => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);

    private static long? Long(OracleDataReader reader, int i) => reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);

    private static double Double(OracleDataReader reader, int i) => reader.IsDBNull(i) ? 0 : Convert.ToDouble(reader.GetValue(i), CultureInfo.InvariantCulture);

    private static DateTimeOffset? Date(OracleDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetValue(i) switch
    {
        DateTime time => new DateTimeOffset(time),
        DateTimeOffset offset => offset,
        _ => null,
    };

    private static TimeSpan? Milliseconds(OracleDataReader reader, int i) => reader.IsDBNull(i) ? null : TimeSpan.FromMilliseconds(Double(reader, i));
}
