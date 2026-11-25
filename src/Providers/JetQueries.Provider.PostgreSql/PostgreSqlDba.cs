using System.Globalization;
using JetQueries.Sdk;
using Npgsql;

namespace JetQueries.Provider.PostgreSql;

/// <summary>
/// The DBA screens on PostgreSQL: pg_stat_activity (sessions, blocking through pg_blocking_pids), pg_stat_statements
/// (heavy queries; an extension), pg_stat_user_tables / pg_stat_user_indexes (scans, index use), pgstattuple (index
/// fragmentation; an extension), pg_stat_database / pg_stat_io (load), pg_settings and catalogs (health rules) and
/// one database in depth. Constant text with parameters; every statement carries the analysis marker.
/// </summary>
internal static class PostgreSqlDba
{
    private const string Marker = "/* JetQueries:advisor */ ";

    // Activity ----------------------------------------------------------------------------------------------------

    private const string ActivitySql = Marker + """
        SELECT a.pid::text, a.usename, COALESCE(a.client_hostname, host(a.client_addr)), a.application_name, a.datname, a.state,
            a.wait_event_type, a.wait_event,
            CASE WHEN a.state = 'active' AND a.wait_event IS NOT NULL THEN EXTRACT(EPOCH FROM now() - a.state_change) * 1000 END,
            (pg_blocking_pids(a.pid))[1]::text,
            CASE WHEN a.state = 'active' THEN EXTRACT(EPOCH FROM now() - a.query_start) * 1000 END,
            CASE WHEN a.backend_xid IS NOT NULL OR a.state LIKE 'idle in transaction%' THEN 1 ELSE 0 END,
            a.backend_start, CASE WHEN a.state <> 'active' THEN a.state_change END, a.query, a.backend_type = 'client backend'
        FROM pg_stat_activity a WHERE a.pid IS NOT NULL ORDER BY a.pid;
        SELECT pg_backend_pid()::text,
            COALESCE((SELECT r.rolsuper FROM pg_roles r WHERE r.rolname = current_user), false) OR pg_has_role(current_user, 'pg_read_all_stats', 'MEMBER');
        """;

    public static async Task<ActivitySnapshot> ActivityAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var sessions = new List<ActivitySession>();
        string? own = null;
        var seesAll = true;
        await using var reader = await ReaderAsync(connection, ActivitySql, null, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = Text(reader, 5);
            var sql = Text(reader, 14)?.Trim();
            var waitType = Text(reader, 6);
            var waitEvent = Text(reader, 7);
            sessions.Add(new ActivitySession(reader.GetString(0))
            {
                Login = Text(reader, 1),
                Host = Text(reader, 2),
                Program = Text(reader, 3),
                Database = Text(reader, 4),
                Status = state,
                Command = state == "active" ? FirstWord(sql) : null,
                WaitType = waitEvent is null ? null : waitType + ":" + waitEvent,
                WaitTime = Milliseconds(reader, 8),
                BlockedBy = Text(reader, 9),
                Elapsed = Milliseconds(reader, 10),
                OpenTransactions = reader.GetInt32(11),
                LoginTime = reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                LastRequestEnd = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
                Sql = sql,
                IsUserSession = !reader.IsDBNull(15) && reader.GetBoolean(15),
            });
        }

        if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false) && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            own = reader.GetString(0);
            seesAll = reader.GetBoolean(1);
        }

        return new ActivitySnapshot(DateTimeOffset.Now, sessions, own)
        {
            Limitation = seesAll ? null : "The role lacks pg_read_all_stats: the statements of other users' sessions are hidden.",
        };
    }

    public static async Task KillAsync(NpgsqlConnection connection, string sessionId, CancellationToken cancellationToken)
    {
        if (!int.TryParse(sessionId, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            throw new ArgumentException("Not a PostgreSQL process id: " + sessionId, nameof(sessionId));
        }

        await using var command = new NpgsqlCommand(Marker + "SELECT pg_terminate_backend(@pid)", connection);
        command.Parameters.AddWithValue("pid", pid);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            throw new InvalidOperationException("PostgreSQL did not end process " + sessionId + " (it may have ended already, or the role may not end it).");
        }
    }

    // Performance -------------------------------------------------------------------------------------------------

    private const string HeavySql = Marker + """
        SELECT s.query, d.datname, s.calls, s.total_exec_time, s.shared_blks_hit + s.shared_blks_read, s.shared_blks_written + s.shared_blks_dirtied, s.rows
        FROM pg_stat_statements s JOIN pg_database d ON d.oid = s.dbid
        WHERE (@db::text IS NULL OR d.datname = @db) AND s.query NOT LIKE '%JetQueries:advisor%'
        ORDER BY CASE @order
            WHEN 0 THEN s.total_exec_time WHEN 1 THEN s.mean_exec_time WHEN 2 THEN s.total_exec_time WHEN 3 THEN s.mean_exec_time
            WHEN 4 THEN (s.shared_blks_hit + s.shared_blks_read)::float8
            WHEN 5 THEN (s.shared_blks_hit + s.shared_blks_read)::float8 / NULLIF(s.calls, 0)
            ELSE s.calls::float8 END DESC NULLS LAST
        LIMIT @top;
        """;

    public static async Task<HeavyQueryReport> HeavyAsync(NpgsqlConnection connection, HeavyQueryRequest request, CancellationToken cancellationToken)
    {
        if (!await HasExtensionAsync(connection, "pg_stat_statements", cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The pg_stat_statements extension is not installed in this database: add it to shared_preload_libraries, restart PostgreSQL and run CREATE EXTENSION pg_stat_statements;");
        }

        var queries = new List<HeavyQuery>();
        await using (var reader = await ReaderAsync(connection, HeavySql, command =>
        {
            command.Parameters.AddWithValue("db", (object?)request.Database ?? DBNull.Value);
            command.Parameters.AddWithValue("order", (int)request.Order);
            command.Parameters.AddWithValue("top", Math.Clamp(request.Top, 1, 1000));
        }, cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // PostgreSQL measures the time of a statement, not its CPU apart: both are that time.
                var time = TimeSpan.FromMilliseconds(reader.GetDouble(3));
                queries.Add(new HeavyQuery(reader.GetString(0).Trim())
                {
                    Database = Text(reader, 1),
                    Executions = reader.GetInt64(2),
                    TotalCpu = time,
                    TotalDuration = time,
                    TotalReads = Long(reader, 4) ?? 0,
                    TotalWrites = Long(reader, 5) ?? 0,
                    TotalRows = Long(reader, 6),
                });
            }
        }

        // pg_stat_statements_info came with PostgreSQL 14.
        var since = connection.PostgreSqlVersion.Major >= 14
            ? "SELECT COALESCE((SELECT stats_reset FROM pg_stat_statements_info), pg_postmaster_start_time())"
            : "SELECT pg_postmaster_start_time()";
        return new HeavyQueryReport(queries, await ScalarAsync<DateTimeOffset?>(connection, Marker + since, cancellationToken).ConfigureAwait(false));
    }

    // No missing-index statistics: tables read by many sequential scans of many rows are what wants an index.
    private const string ScansSql = Marker + """
        SELECT current_database(), s.schemaname || '.' || s.relname, s.seq_scan, s.seq_tup_read, COALESCE(s.idx_scan, 0), s.n_live_tup
        FROM pg_stat_user_tables s
        WHERE s.seq_scan > 0 AND s.n_live_tup >= 10000 AND s.seq_tup_read / s.seq_scan >= 1000
        ORDER BY s.seq_tup_read DESC LIMIT 100;
        """;

    public static async Task<IReadOnlyList<MissingIndex>> ScansAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var tables = new List<MissingIndex>();
        await using var reader = await ReaderAsync(connection, ScansSql, null, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var table = reader.GetString(1);
            var seq = reader.GetInt64(2);
            var read = reader.GetInt64(3);
            var idx = reader.GetInt64(4);
            tables.Add(new MissingIndex(reader.GetString(0), table,
                "-- PostgreSQL keeps no missing-index statistics. " + table + " was read by " + seq.ToString(CultureInfo.InvariantCulture) +
                " sequential scans of about " + (read / seq).ToString(CultureInfo.InvariantCulture) + " rows each.\n" +
                "-- Find the WHERE / JOIN columns of its queries (Heavy queries) and index them, for example:\n" +
                "-- CREATE INDEX CONCURRENTLY ON " + Quoted(table) + " (column);")
            {
                Uses = seq,
                ImpactPercent = seq + idx == 0 ? 0 : seq * 100.0 / (seq + idx),
                Score = read,
            });
        }

        return tables;
    }

    private const string UsageSql = Marker + """
        SELECT s.schemaname, s.relname, s.indexrelname, i.indisunique, i.indisprimary OR c.conindid IS NOT NULL,
            ARRAY(SELECT pg_get_indexdef(i.indexrelid, k, true) FROM generate_series(1, i.indnkeyatts) k),
            ARRAY(SELECT pg_get_indexdef(i.indexrelid, k, true) FROM generate_series(i.indnkeyatts + 1, i.indnatts) k),
            pg_get_expr(i.indpred, i.indrelid), s.idx_scan, t.n_tup_ins + t.n_tup_upd + t.n_tup_del,
            pg_relation_size(s.indexrelid) / 1024, t.n_live_tup, s.last_idx_scan
        FROM pg_stat_user_indexes s
        JOIN pg_index i ON i.indexrelid = s.indexrelid
        JOIN pg_stat_user_tables t ON t.relid = s.relid
        LEFT JOIN pg_constraint c ON c.conindid = s.indexrelid AND c.contype IN ('p', 'u', 'x')
        ORDER BY s.schemaname, s.relname, s.indexrelname;
        """;

    public static async Task<IndexUsageReport> UsageAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var indexes = new List<IndexUsage>();
        // last_idx_scan came with PostgreSQL 16.
        var sql = connection.PostgreSqlVersion.Major >= 16 ? UsageSql : UsageSql.Replace("s.last_idx_scan", "NULL::timestamptz", StringComparison.Ordinal);
        await using (var reader = await ReaderAsync(connection, sql, null, cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(2);
                var constraint = reader.GetBoolean(4);
                indexes.Add(new IndexUsage(schema + "." + reader.GetString(1), name)
                {
                    IsUnique = reader.GetBoolean(3),
                    IsConstraint = constraint,
                    KeyColumns = reader.GetFieldValue<string[]>(5),
                    IncludedColumns = reader.GetFieldValue<string[]>(6),
                    Filter = Text(reader, 7),
                    Seeks = Long(reader, 8) ?? 0,
                    Updates = Long(reader, 9) ?? 0,
                    SizeKb = Long(reader, 10) ?? 0,
                    Rows = Long(reader, 11),
                    LastRead = reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                    DropScript = constraint ? null : "DROP INDEX CONCURRENTLY " + PostgreSqlNames.Quote(schema, name) + ";",
                });
            }
        }

        return new IndexUsageReport(indexes, await ScalarAsync<DateTimeOffset?>(connection,
            Marker + "SELECT COALESCE((SELECT stats_reset FROM pg_stat_database WHERE datname = current_database()), pg_postmaster_start_time())", cancellationToken).ConfigureAwait(false));
    }

    private const string FragmentationSql = Marker + """
        SELECT n.nspname, t.relname, c.relname, f.leaf_fragmentation, pg_relation_size(c.oid) / current_setting('block_size')::bigint
        FROM pg_index i
        JOIN pg_class c ON c.oid = i.indexrelid
        JOIN pg_class t ON t.oid = i.indrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_am a ON a.oid = c.relam AND a.amname = 'btree'
        CROSS JOIN LATERAL pgstatindex(c.oid) f
        WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg_toast%'
          AND pg_relation_size(c.oid) / current_setting('block_size')::bigint >= @pages
          AND f.leaf_fragmentation >= 5
        ORDER BY f.leaf_fragmentation * pg_relation_size(c.oid) DESC;
        """;

    public static async Task<IReadOnlyList<IndexFragmentation>> FragmentationAsync(NpgsqlConnection connection, long minimumPages, CancellationToken cancellationToken)
    {
        if (!await HasExtensionAsync(connection, "pgstattuple", cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Measuring index fragmentation needs the pgstattuple extension in this database: CREATE EXTENSION pgstattuple;");
        }

        var indexes = new List<IndexFragmentation>();
        await using var reader = await ReaderAsync(connection, FragmentationSql, command => command.Parameters.AddWithValue("pages", Math.Max(0, minimumPages)), cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(2);
            var percent = reader.GetDouble(3);
            indexes.Add(new IndexFragmentation(schema + "." + reader.GetString(1), name)
            {
                FragmentationPercent = percent,
                Pages = reader.GetInt64(4),
                Rebuild = percent >= 30,

                // PostgreSQL has no reorganize: below 30 % nothing is worth the rebuild.
                MaintenanceScript = percent >= 30 ? "REINDEX INDEX CONCURRENTLY " + PostgreSqlNames.Quote(schema, name) + ";" : null,
            });
        }

        return indexes;
    }

    // Dashboard ---------------------------------------------------------------------------------------------------

    private const string OverviewSql = Marker + """
        SELECT current_setting('server_version'), current_setting('server_version_num')::int, pg_postmaster_start_time(),
            COALESCE(NULLIF(current_setting('cluster_name'), ''), host(inet_server_addr()));
        SELECT d.datname, CASE WHEN d.datallowconn THEN 'ONLINE' ELSE 'NO CONNECTIONS' END,
            CASE WHEN has_database_privilege(d.datname, 'CONNECT') THEN pg_database_size(d.oid) / 1048576.0 END
        FROM pg_database d WHERE NOT d.datistemplate ORDER BY d.datname;
        """;

    public static async Task<ServerOverview> OverviewAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ServerOverview? overview = null;
        var databases = new List<DatabaseSummary>();
        await using (var reader = await ReaderAsync(connection, OverviewSql, null, cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var major = reader.GetInt32(1) / 10000;
                overview = new ServerOverview("PostgreSQL " + major.ToString(CultureInfo.InvariantCulture), reader.GetString(0))
                {
                    StartedAt = reader.GetFieldValue<DateTimeOffset>(2),
                    Host = Text(reader, 3),
                };
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                databases.Add(new DatabaseSummary(reader.GetString(0)) { State = reader.GetString(1), DataMb = reader.IsDBNull(2) ? 0 : Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture) });
            }
        }

        return (overview ?? new ServerOverview("PostgreSQL", string.Empty)) with { Databases = databases };
    }

    private const string SampleSql = Marker + """
        SELECT
            (SELECT count(*) FROM pg_stat_activity WHERE backend_type = 'client backend'),
            (SELECT count(*) FROM pg_stat_activity WHERE backend_type = 'client backend' AND state = 'active' AND pid <> pg_backend_pid()),
            (SELECT count(*) FROM pg_stat_activity WHERE cardinality(pg_blocking_pids(pid)) > 0),
            (SELECT sum(xact_commit + xact_rollback) FROM pg_stat_database),
            (SELECT sum(blks_read) FROM pg_stat_database),
            (SELECT sum(blk_read_time + blk_write_time) FROM pg_stat_database),
            (SELECT setting::bigint * current_setting('block_size')::bigint / 1048576 FROM pg_settings WHERE name = 'shared_buffers'),
            current_setting('block_size')::bigint;
        """;

    public static async Task<ServerSample> SampleAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var sample = new ServerSample(DateTimeOffset.Now);
        await using (var reader = await ReaderAsync(connection, SampleSql, null, cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var block = reader.GetInt64(7);
                sample = sample with
                {
                    Connections = (int?)Long(reader, 0),
                    ActiveRequests = (int?)Long(reader, 1),
                    BlockedRequests = (int?)Long(reader, 2),
                    BatchRequests = Long(reader, 3),
                    Transactions = Long(reader, 3),
                    BytesRead = Long(reader, 4) * block,
                    IoOperations = Long(reader, 4),
                    IoStallMs = reader.IsDBNull(5) ? null : (long)reader.GetDouble(5),
                    MemoryUsedMb = Long(reader, 6),
                };
            }
        }

        // pg_stat_io (PostgreSQL 16) counts writes; older servers have no total of them.
        try
        {
            sample = sample with { BytesWritten = await ScalarAsync<long?>(connection, Marker + "SELECT COALESCE(sum(writes * op_bytes), 0)::bigint FROM pg_stat_io", cancellationToken).ConfigureAwait(false) };
        }
        catch (PostgresException)
        {
        }

        return sample;
    }

    // Health ------------------------------------------------------------------------------------------------------

    private const string HealthSql = Marker + """
        SELECT name, setting, COALESCE(unit, '') FROM pg_settings
        WHERE name IN ('shared_buffers', 'work_mem', 'random_page_cost', 'autovacuum', 'track_io_timing', 'archive_mode', 'shared_preload_libraries', 'block_size');
        SELECT pg_postmaster_start_time();
        SELECT d.datname, age(d.datfrozenxid) FROM pg_database d WHERE d.datallowconn ORDER BY 2 DESC;
        SELECT s.slot_name, s.active, COALESCE(pg_wal_lsn_diff(pg_current_wal_lsn(), s.restart_lsn) / 1048576, 0)::bigint FROM pg_replication_slots s;
        SELECT a.pid::text, a.usename, a.datname, (EXTRACT(EPOCH FROM now() - a.state_change) / 60)::int
        FROM pg_stat_activity a WHERE a.state LIKE 'idle in transaction%' AND now() - a.state_change > interval '5 minutes';
        SELECT r.rolname FROM pg_roles r WHERE r.rolsuper AND r.rolname NOT LIKE 'pg\_%' ORDER BY 1;
        SELECT s.schemaname, s.relname, s.n_live_tup, s.n_dead_tup, s.last_analyze IS NULL AND s.last_autoanalyze IS NULL
        FROM pg_stat_user_tables s;
        """;

    public static async Task<IReadOnlyList<HealthFinding>> HealthAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var settings = new Dictionary<string, (string Value, string Unit)>(StringComparer.Ordinal);
        DateTimeOffset? started = null;
        var ages = new List<(string Database, long Age)>();
        var slots = new List<(string Name, bool Active, long Mb)>();
        var idle = new List<string>();
        var superusers = new List<string>();
        var tables = new List<(string Schema, string Name, long Live, long Dead, bool NeverAnalyzed)>();
        await using (var reader = await ReaderAsync(connection, HealthSql, null, cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                settings[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                started = reader.GetFieldValue<DateTimeOffset>(0);
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ages.Add((reader.GetString(0), Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)));
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                slots.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetInt64(2)));
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                idle.Add(reader.GetString(0) + " · " + Text(reader, 1) + " · " + Text(reader, 2) + " (" + reader.GetInt32(3).ToString(CultureInfo.InvariantCulture) + " min)");
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                superusers.Add(reader.GetString(0));
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetBoolean(4)));
            }
        }

        string Setting(string name) => settings.TryGetValue(name, out var s) ? s.Value : string.Empty;
        long Bytes(string name)
        {
            if (!settings.TryGetValue(name, out var s) || !long.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return 0;
            }

            return s.Unit switch
            {
                "8kB" => value * 8192,
                "kB" => value * 1024,
                "MB" => value * 1024 * 1024,
                _ => value,
            };
        }

        var findings = new List<HealthFinding>();
        var sharedMb = Bytes("shared_buffers") / 1024 / 1024;
        findings.Add(sharedMb <= 128
            ? new HealthFinding("SharedBuffers", HealthCategory.Configuration, HealthSeverity.Warning, "shared_buffers is the default 128 MB")
            {
                Current = sharedMb.ToString(CultureInfo.InvariantCulture) + " MB",
                Recommended = "25 % RAM",
                FixScript = "ALTER SYSTEM SET shared_buffers = '4GB'; -- about a quarter of the server's memory; takes effect after a restart",
            }
            : Ok("SharedBuffers", HealthCategory.Configuration, "shared_buffers is the default 128 MB"));

        var workMb = Bytes("work_mem") / 1024 / 1024;
        findings.Add(workMb <= 4
            ? new HealthFinding("WorkMem", HealthCategory.Performance, HealthSeverity.Info, "work_mem is the default 4 MB")
            {
                Current = workMb.ToString(CultureInfo.InvariantCulture) + " MB", Recommended = "16–64 MB",
                FixScript = "ALTER SYSTEM SET work_mem = '16MB';\nSELECT pg_reload_conf();",
            }
            : Ok("WorkMem", HealthCategory.Performance, "work_mem is the default 4 MB"));

        findings.Add(Setting("random_page_cost") == "4"
            ? new HealthFinding("RandomPageCost", HealthCategory.Performance, HealthSeverity.Info, "random_page_cost assumes spinning disks")
            {
                Current = "4", Recommended = "1.1", FixScript = "ALTER SYSTEM SET random_page_cost = 1.1;\nSELECT pg_reload_conf();",
            }
            : Ok("RandomPageCost", HealthCategory.Performance, "random_page_cost assumes spinning disks"));

        findings.Add(Setting("autovacuum") == "off"
            ? new HealthFinding("Autovacuum", HealthCategory.Configuration, HealthSeverity.Critical, "Autovacuum is off")
            {
                Current = "off", Recommended = "on", FixScript = "ALTER SYSTEM SET autovacuum = on;\nSELECT pg_reload_conf();",
            }
            : Ok("Autovacuum", HealthCategory.Configuration, "Autovacuum is off"));

        findings.Add(Setting("track_io_timing") == "off"
            ? new HealthFinding("TrackIoTiming", HealthCategory.Performance, HealthSeverity.Info, "I/O timing is not tracked")
            {
                Current = "off", Recommended = "on", FixScript = "ALTER SYSTEM SET track_io_timing = on;\nSELECT pg_reload_conf();",
            }
            : Ok("TrackIoTiming", HealthCategory.Performance, "I/O timing is not tracked"));

        findings.Add(!Setting("shared_preload_libraries").Contains("pg_stat_statements", StringComparison.Ordinal)
            ? new HealthFinding("StatStatements", HealthCategory.Performance, HealthSeverity.Info, "pg_stat_statements is not loaded")
            {
                FixScript = "ALTER SYSTEM SET shared_preload_libraries = 'pg_stat_statements'; -- keep any libraries already listed; restart PostgreSQL\nCREATE EXTENSION IF NOT EXISTS pg_stat_statements;",
            }
            : Ok("StatStatements", HealthCategory.Performance, "pg_stat_statements is not loaded"));

        findings.Add(Setting("archive_mode") == "off"
            ? new HealthFinding("Archiving", HealthCategory.Backups, HealthSeverity.Info, "WAL archiving is off") { Current = "off" }
            : Ok("Archiving", HealthCategory.Backups, "WAL archiving is off"));

        // Transaction id wraparound: at about 2 billion PostgreSQL stops accepting writes.
        var old = ages.Where(static a => a.Age > 1_000_000_000).ToList();
        findings.Add(old.Count == 0
            ? Ok("Wraparound", HealthCategory.Databases, "Transaction ids are close to wraparound")
            : new HealthFinding("Wraparound", HealthCategory.Databases, old.Any(static a => a.Age > 1_500_000_000) ? HealthSeverity.Critical : HealthSeverity.Warning,
                "Transaction ids are close to wraparound")
            {
                Objects = [.. old.Select(static a => a.Database + " (" + a.Age.ToString(CultureInfo.InvariantCulture) + ")")],
                FixScript = string.Join("\n", old.Select(static a => "-- in database " + a.Database + ":\nVACUUM (FREEZE, ANALYZE);")),
            });

        var lost = slots.Where(static s => !s.Active).ToList();
        findings.Add(lost.Count == 0
            ? Ok("ReplicationSlots", HealthCategory.Backups, "Inactive replication slots keep WAL")
            : new HealthFinding("ReplicationSlots", HealthCategory.Backups, HealthSeverity.Critical, "Inactive replication slots keep WAL")
            {
                Objects = [.. lost.Select(static s => s.Name + " (" + s.Mb.ToString(CultureInfo.InvariantCulture) + " MB)")],
                FixScript = string.Join("\n", lost.Select(static s => "SELECT pg_drop_replication_slot(" + PostgreSqlNames.Literal(s.Name) + ");")),
            });

        findings.Add(idle.Count == 0
            ? Ok("IdleInTransaction", HealthCategory.Databases, "Sessions sit idle in a transaction")
            : new HealthFinding("IdleInTransaction", HealthCategory.Databases, HealthSeverity.Warning, "Sessions sit idle in a transaction")
            {
                Objects = idle,
                FixScript = string.Join("\n", idle.Select(static i => "SELECT pg_terminate_backend(" + i[..i.IndexOf(' ', StringComparison.Ordinal)] + ");")),
            });

        var dead = tables.Where(static t => t.Dead > 10_000 && t.Dead > t.Live / 5).ToList();
        findings.Add(dead.Count == 0
            ? Ok("DeadTuples", HealthCategory.Databases, "Tables carry many dead rows")
            : new HealthFinding("DeadTuples", HealthCategory.Databases, HealthSeverity.Warning, "Tables carry many dead rows")
            {
                Objects = [.. dead.Select(static t => t.Schema + "." + t.Name + " (" + t.Dead.ToString(CultureInfo.InvariantCulture) + " / " + t.Live.ToString(CultureInfo.InvariantCulture) + ")")],
                FixScript = string.Join("\n", dead.Select(static t => "VACUUM (ANALYZE) " + PostgreSqlNames.Quote(t.Schema, t.Name) + ";")),
            });

        var stale = tables.Where(static t => t.NeverAnalyzed && t.Live >= 1000).ToList();
        findings.Add(stale.Count == 0
            ? Ok("StaleStats", HealthCategory.Performance, "Tables were never analyzed")
            : new HealthFinding("StaleStats", HealthCategory.Performance, HealthSeverity.Warning, "Tables were never analyzed")
            {
                Objects = [.. stale.Select(static t => t.Schema + "." + t.Name)],
                FixScript = string.Join("\n", stale.Select(static t => "ANALYZE " + PostgreSqlNames.Quote(t.Schema, t.Name) + ";")),
            });

        findings.Add(superusers.Count > 3
            ? new HealthFinding("Superusers", HealthCategory.Security, HealthSeverity.Info, "Many roles are superuser")
            {
                Current = superusers.Count.ToString(CultureInfo.InvariantCulture), Recommended = "≤ 3", Objects = superusers,
            }
            : Ok("Superusers", HealthCategory.Security, "Many roles are superuser"));

        findings.Add(started is { } start && DateTimeOffset.Now - start < TimeSpan.FromDays(1)
            ? new HealthFinding("Uptime", HealthCategory.Performance, HealthSeverity.Info, "The server started less than a day ago")
            {
                Current = start.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            }
            : Ok("Uptime", HealthCategory.Performance, "The server started less than a day ago"));
        return findings;
    }

    // Database detail ---------------------------------------------------------------------------------------------

    private const string DetailSql = Marker + """
        SELECT d.datname, pg_get_userbyid(d.datdba), pg_encoding_to_char(d.encoding), d.datcollate, d.datconnlimit, d.datallowconn,
            t.spcname, pg_database_size(d.oid) / 1048576.0
        FROM pg_database d JOIN pg_tablespace t ON t.oid = d.dattablespace WHERE d.datname = current_database();
        SELECT count(*), COALESCE(sum(n_live_tup), 0)::bigint FROM pg_stat_user_tables;
        SELECT s.schemaname || '.' || s.relname, s.n_live_tup, pg_total_relation_size(s.relid) / 1048576.0,
            pg_table_size(s.relid) / 1048576.0, pg_indexes_size(s.relid) / 1048576.0
        FROM pg_stat_user_tables s ORDER BY 3 DESC LIMIT 50;
        SELECT t.spcname, COALESCE(NULLIF(pg_tablespace_location(t.oid), ''), current_setting('data_directory')),
            pg_tablespace_size(t.oid) / 1048576.0
        FROM pg_tablespace t ORDER BY t.spcname;
        """;

    public static async Task<DatabaseDetail> DetailAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var detail = new DatabaseDetail(connection.Database);
        var tables = new List<TableSpace>();
        var files = new List<DatabaseFileInfo>();
        await using (var reader = await ReaderAsync(connection, DetailSql, null, cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var limit = reader.GetInt32(4);
                detail = detail with
                {
                    State = reader.GetBoolean(5) ? "ONLINE" : "NO CONNECTIONS",
                    Owner = Text(reader, 1),
                    Collation = Text(reader, 3),
                    DataMb = Convert.ToDouble(reader.GetValue(7), CultureInfo.InvariantCulture),
                    Options =
                    [
                        new("encoding", "Encoding", Text(reader, 2)),
                        new("collation", "Collation", Text(reader, 3)),
                        new("connectionLimit", "Connection limit", limit < 0 ? "∞" : limit.ToString(CultureInfo.InvariantCulture)),
                        new("allowConnections", "Allow connections", reader.GetBoolean(5) ? "ON" : "OFF"),
                        new("tablespace", "Tablespace", Text(reader, 6)),
                    ],
                };
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                detail = detail with { TableCount = (int)reader.GetInt64(0), TotalRows = reader.GetInt64(1) };
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(new TableSpace(reader.GetString(0))
                {
                    Rows = reader.GetInt64(1),
                    ReservedMb = Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture),
                    DataMb = Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture),
                    IndexMb = Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture),
                });
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                files.Add(new DatabaseFileInfo(reader.GetString(0))
                {
                    Type = "TABLESPACE", Path = Text(reader, 1), SizeMb = Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture), MaxSize = "∞",
                });
            }
        }

        // The write-ahead log of the whole server (pg_ls_waldir needs pg_monitor or superuser).
        double? walMb = null;
        try
        {
            walMb = await ScalarAsync<double?>(connection, Marker + "SELECT COALESCE(sum(size), 0) / 1048576.0 FROM pg_ls_waldir()", cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException)
        {
        }

        return detail with { Tables = tables, Files = files, LogMb = walMb ?? 0, BackupsKnown = false };
    }

    // Helpers -----------------------------------------------------------------------------------------------------

    private static async Task<bool> HasExtensionAsync(NpgsqlConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(Marker + "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = @name)", connection);
        command.Parameters.AddWithValue("name", name);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task<NpgsqlDataReader> ReaderAsync(NpgsqlConnection connection, string sql, Action<NpgsqlCommand>? parameters, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // Constant text.
        var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 60 };
#pragma warning restore CA2100
        parameters?.Invoke(command);

        // The reader owns the command: disposing it disposes both.
        return await command.ExecuteReaderAsync(System.Data.CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // Constant text.
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
#pragma warning restore CA2100
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
        {
            return default;
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return target == typeof(DateTimeOffset) && value is DateTime time
            ? (T)(object)new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc))
            : (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    private static HealthFinding Ok(string id, HealthCategory category, string title) => new(id, category, HealthSeverity.Ok, title);

    private static string Quoted(string table)
    {
        var dot = table.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? PostgreSqlNames.Quote(table) : PostgreSqlNames.Quote(table[..dot], table[(dot + 1)..]);
    }

    private static string? FirstWord(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return null;
        }

        var text = sql.TrimStart();
        var end = 0;
        while (end < text.Length && char.IsLetter(text[end]))
        {
            end++;
        }

        return end == 0 ? "QUERY" : text[..end].ToUpperInvariant();
    }

    private static string? Text(NpgsqlDataReader reader, int i) => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);

    private static long? Long(NpgsqlDataReader reader, int i) => reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);

    private static TimeSpan? Milliseconds(NpgsqlDataReader reader, int i) =>
        reader.IsDBNull(i) ? null : TimeSpan.FromMilliseconds(Convert.ToDouble(reader.GetValue(i), CultureInfo.InvariantCulture));
}
