using System.Globalization;
using JetQueries.Sdk;
using Microsoft.Data.SqlClient;

namespace JetQueries.Provider.SqlServer;

/// <summary>
/// The server dashboard on SQL Server: SERVERPROPERTY, sys.dm_os_sys_info and the databases with their files and
/// msdb backups (overview); the scheduler monitor ring buffer for CPU, performance counters, memory, requests, file
/// I/O and wait statistics (sample). Constant text, marked so the analysis leaves it out of the heavy queries.
/// </summary>
internal static class SqlServerDashboard
{
    private const string Marker = "/* JetQueries:advisor */\n";

    private const string OverviewSql = Marker + """
        SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')), CONVERT(nvarchar(128), SERVERPROPERTY('Edition')),
            CONVERT(nvarchar(128), SERVERPROPERTY('ProductLevel')), CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')),
            TODATETIMEOFFSET(i.sqlserver_start_time, DATEPART(TZOFFSET, SYSDATETIMEOFFSET())), i.cpu_count, i.physical_memory_kb / 1024
        FROM sys.dm_os_sys_info i;
        SELECT d.name, d.state_desc, d.recovery_model_desc,
            SUM(CASE WHEN f.type = 0 THEN f.size ELSE 0 END) * 8 / 1024.0,
            SUM(CASE WHEN f.type = 1 THEN f.size ELSE 0 END) * 8 / 1024.0
        FROM sys.databases d LEFT JOIN sys.master_files f ON f.database_id = d.database_id
        GROUP BY d.name, d.state_desc, d.recovery_model_desc, d.database_id
        ORDER BY CASE WHEN d.database_id <= 4 THEN 1 ELSE 0 END, d.name;
        """ + CpuSql;

    // The scheduler monitor writes one record a minute: SQL Server's CPU and the machine's idle time.
    private const string CpuSql = """
        DECLARE @now bigint = (SELECT ms_ticks FROM sys.dm_os_sys_info);
        SELECT TOP (60) TODATETIMEOFFSET(DATEADD(ms, -1 * (@now - x.[timestamp]), GETDATE()), DATEPART(TZOFFSET, SYSDATETIMEOFFSET())),
            x.record.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int'),
            x.record.value('(./Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]', 'int')
        FROM (SELECT [timestamp], CONVERT(xml, record) AS record FROM sys.dm_os_ring_buffers
              WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR' AND record LIKE N'%<SystemHealth>%') x
        ORDER BY x.[timestamp] DESC;
        """;

    private const string BackupsSql = Marker + """
        SELECT b.database_name,
            TODATETIMEOFFSET(MAX(CASE WHEN b.type = 'D' THEN b.backup_finish_date END), DATEPART(TZOFFSET, SYSDATETIMEOFFSET())),
            TODATETIMEOFFSET(MAX(CASE WHEN b.type = 'L' THEN b.backup_finish_date END), DATEPART(TZOFFSET, SYSDATETIMEOFFSET()))
        FROM msdb.dbo.backupset b
        GROUP BY b.database_name;
        """;

    // Waits that only mean the server is idle or doing housekeeping (the usual exclusion list).
    private const string SampleSql = Marker + """
        SELECT
            MAX(CASE WHEN counter_name = 'Batch Requests/sec' THEN cntr_value END),
            MAX(CASE WHEN counter_name = 'SQL Compilations/sec' THEN cntr_value END),
            MAX(CASE WHEN counter_name = 'Transactions/sec' AND object_name LIKE '%:Databases%' AND instance_name = '_Total' THEN cntr_value END),
            MAX(CASE WHEN counter_name = 'User Connections' THEN cntr_value END),
            MAX(CASE WHEN counter_name = 'Page life expectancy' AND object_name LIKE '%Buffer Manager%' THEN cntr_value END),
            MAX(CASE WHEN counter_name = 'Total Server Memory (KB)' THEN cntr_value END) / 1024,
            MAX(CASE WHEN counter_name = 'Target Server Memory (KB)' THEN cntr_value END) / 1024
        FROM sys.dm_os_performance_counters
        WHERE counter_name IN ('Batch Requests/sec', 'SQL Compilations/sec', 'Transactions/sec', 'User Connections', 'Page life expectancy',
                               'Total Server Memory (KB)', 'Target Server Memory (KB)');
        SELECT (SELECT available_physical_memory_kb / 1024 FROM sys.dm_os_sys_memory),
            (SELECT COUNT(*) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id
             WHERE s.is_user_process = 1 AND r.session_id <> @@SPID),
            (SELECT COUNT(*) FROM sys.dm_exec_requests r WHERE r.blocking_session_id > 0),
            SUM(v.num_of_bytes_read), SUM(v.num_of_bytes_written), SUM(v.num_of_reads + v.num_of_writes), SUM(v.io_stall_read_ms + v.io_stall_write_ms)
        FROM sys.dm_io_virtual_file_stats(NULL, NULL) v;
        SELECT TOP (12) w.wait_type, w.wait_time_ms
        FROM sys.dm_os_wait_stats w
        WHERE w.wait_time_ms > 0 AND w.wait_type NOT IN (
            N'BROKER_EVENTHANDLER', N'BROKER_RECEIVE_WAITFOR', N'BROKER_TASK_STOP', N'BROKER_TO_FLUSH', N'BROKER_TRANSMITTER', N'CHECKPOINT_QUEUE',
            N'CHKPT', N'CLR_AUTO_EVENT', N'CLR_MANUAL_EVENT', N'CLR_SEMAPHORE', N'DBMIRROR_DBM_EVENT', N'DBMIRROR_EVENTS_QUEUE', N'DBMIRROR_WORKER_QUEUE',
            N'DBMIRRORING_CMD', N'DIRTY_PAGE_POLL', N'DISPATCHER_QUEUE_SEMAPHORE', N'EXECSYNC', N'FSAGENT', N'FT_IFTS_SCHEDULER_IDLE_WAIT', N'FT_IFTSHC_MUTEX',
            N'HADR_CLUSAPI_CALL', N'HADR_FILESTREAM_IOMGR_IOCOMPLETION', N'HADR_LOGCAPTURE_WAIT', N'HADR_NOTIFICATION_DEQUEUE', N'HADR_TIMER_TASK',
            N'HADR_WORK_QUEUE', N'KSOURCE_WAKEUP', N'LAZYWRITER_SLEEP', N'LOGMGR_QUEUE', N'MEMORY_ALLOCATION_EXT', N'ONDEMAND_TASK_QUEUE',
            N'PARALLEL_REDO_DRAIN_WORKER', N'PARALLEL_REDO_LOG_CACHE', N'PARALLEL_REDO_TRAN_LIST', N'PARALLEL_REDO_WORKER_SYNC', N'PARALLEL_REDO_WORKER_WAIT_WORK',
            N'PREEMPTIVE_OS_FLUSHFILEBUFFERS', N'PREEMPTIVE_XE_GETTARGETSTATE', N'PVS_PREALLOCATE', N'PWAIT_ALL_COMPONENTS_INITIALIZED',
            N'PWAIT_DIRECTLOGCONSUMER_GETNEXT', N'PWAIT_EXTENSIBILITY_CLEANUP_TASK', N'QDS_PERSIST_TASK_MAIN_LOOP_SLEEP', N'QDS_ASYNC_QUEUE',
            N'QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP', N'QDS_SHUTDOWN_QUEUE', N'REDO_THREAD_PENDING_WORK', N'REQUEST_FOR_DEADLOCK_SEARCH',
            N'RESOURCE_QUEUE', N'SERVER_IDLE_CHECK', N'SLEEP_BPOOL_FLUSH', N'SLEEP_DBSTARTUP', N'SLEEP_DCOMSTARTUP', N'SLEEP_MASTERDBREADY',
            N'SLEEP_MASTERMDREADY', N'SLEEP_MASTERUPGRADED', N'SLEEP_MSDBSTARTUP', N'SLEEP_SYSTEMTASK', N'SLEEP_TASK', N'SLEEP_TEMPDBSTARTUP',
            N'SNI_HTTP_ACCEPT', N'SOS_WORK_DISPATCHER', N'SP_SERVER_DIAGNOSTICS_SLEEP', N'SQLTRACE_BUFFER_FLUSH', N'SQLTRACE_INCREMENTAL_FLUSH_SLEEP',
            N'SQLTRACE_WAIT_ENTRIES', N'VDI_CLIENT_OTHER', N'WAIT_FOR_RESULTS', N'WAITFOR', N'WAITFOR_TASKSHUTDOWN', N'WAIT_XTP_RECOVERY',
            N'WAIT_XTP_HOST_WAIT', N'WAIT_XTP_OFFLINE_CKPT_NEW_LOG', N'WAIT_XTP_CKPT_CLOSE', N'XE_DISPATCHER_JOIN', N'XE_DISPATCHER_WAIT',
            N'XE_TIMER_EVENT', N'XE_LIVE_TARGET_TVF', N'SOS_WORKER_MIGRATION', N'STARTUP_DEPENDENCY_MANAGER', N'POPULATE_LOCK_ORDINALS')
        ORDER BY w.wait_time_ms DESC;
        """ + CpuLatestSql;

    private const string CpuLatestSql = """
        DECLARE @now bigint = (SELECT ms_ticks FROM sys.dm_os_sys_info);
        SELECT TOP (1) TODATETIMEOFFSET(DATEADD(ms, -1 * (@now - x.[timestamp]), GETDATE()), DATEPART(TZOFFSET, SYSDATETIMEOFFSET())),
            x.record.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int'),
            x.record.value('(./Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]', 'int')
        FROM (SELECT [timestamp], CONVERT(xml, record) AS record FROM sys.dm_os_ring_buffers
              WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR' AND record LIKE N'%<SystemHealth>%') x
        ORDER BY x.[timestamp] DESC;
        """;

    public static async Task<ServerOverview> OverviewAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        ServerOverview? overview = null;
        var databases = new List<DatabaseSummary>();
        var cpu = new List<CpuPoint>();
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Constant text.
            command.CommandText = OverviewSql;
#pragma warning restore CA2100
            command.CommandTimeout = 60;
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var level = Text(reader, 2);
                    overview = new ServerOverview(Product(Text(reader, 0)), Text(reader, 0) + (level is null ? string.Empty : " " + level))
                    {
                        Edition = Text(reader, 1),
                        Host = Text(reader, 3),
                        StartedAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                        CpuCount = reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
                        PhysicalMemoryMb = Long(reader, 6),
                    };
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    databases.Add(new DatabaseSummary(reader.GetString(0))
                    {
                        State = Text(reader, 1),
                        RecoveryModel = Text(reader, 2),
                        DataMb = reader.IsDBNull(3) ? 0 : Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture),
                        LogMb = reader.IsDBNull(4) ? 0 : Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture),
                    });
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (Cpu(reader) is { } point)
                    {
                        cpu.Add(point);
                    }
                }
            }
        }

        // Backups live in msdb, which a login may not read: then they are unknown, not missing.
        var backups = await BackupsAsync(connection, cancellationToken).ConfigureAwait(false);
        var withBackups = databases.Select(d => backups is null
            ? d
            : d with
            {
                BackupsKnown = true,
                LastFullBackup = backups.GetValueOrDefault(d.Name).Full,
                LastLogBackup = backups.GetValueOrDefault(d.Name).Log,
            }).ToList();
        cpu.Reverse();
        return (overview ?? new ServerOverview("SQL Server", string.Empty)) with { Databases = withBackups, CpuHistory = cpu };
    }

    public static async Task<ServerSample> SampleAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var sample = new ServerSample(DateTimeOffset.Now);
        var waits = new List<WaitTotal>();
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Constant text.
            command.CommandText = SampleSql;
#pragma warning restore CA2100
            command.CommandTimeout = 30;
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    sample = sample with
                    {
                        BatchRequests = Long(reader, 0),
                        Compilations = Long(reader, 1),
                        Transactions = Long(reader, 2),
                        Connections = (int?)Long(reader, 3),
                        PageLifeExpectancy = Long(reader, 4),
                        MemoryUsedMb = Long(reader, 5),
                        MemoryTargetMb = Long(reader, 6),
                    };
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    sample = sample with
                    {
                        AvailableMemoryMb = Long(reader, 0),
                        ActiveRequests = (int?)Long(reader, 1),
                        BlockedRequests = (int?)Long(reader, 2),
                        BytesRead = Long(reader, 3),
                        BytesWritten = Long(reader, 4),
                        IoOperations = Long(reader, 5),
                        IoStallMs = Long(reader, 6),
                    };
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    waits.Add(new WaitTotal(reader.GetString(0), Long(reader, 1) ?? 0));
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    sample = sample with { Cpu = Cpu(reader) };
                }
            }
        }

        return sample with { Waits = waits };
    }

    private static async Task<Dictionary<string, (DateTimeOffset? Full, DateTimeOffset? Log)>?> BackupsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var backups = new Dictionary<string, (DateTimeOffset? Full, DateTimeOffset? Log)>(StringComparer.OrdinalIgnoreCase);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
#pragma warning disable CA2100 // Constant text.
                command.CommandText = BackupsSql;
#pragma warning restore CA2100
                command.CommandTimeout = 30;
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        backups[reader.GetString(0)] = (
                            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
                            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
                    }
                }
            }

            return backups;
        }
        catch (SqlException)
        {
            return null;
        }
    }

    // "16.0.4135.4" → "SQL Server 2022".
    private static string Product(string? version)
    {
        var major = version?.Split('.')[0];
        return major switch
        {
            "17" => "SQL Server 2025",
            "16" => "SQL Server 2022",
            "15" => "SQL Server 2019",
            "14" => "SQL Server 2017",
            "13" => "SQL Server 2016",
            "12" => "SQL Server 2014",
            "11" => "SQL Server 2012",
            _ => "SQL Server",
        };
    }

    private static CpuPoint? Cpu(SqlDataReader reader)
    {
        if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
        {
            return null;
        }

        var server = Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture);
        var idle = Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture);
        return new CpuPoint(reader.GetFieldValue<DateTimeOffset>(0), server, Math.Max(0, 100 - idle - server));
    }

    private static string? Text(SqlDataReader reader, int i) => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);

    private static long? Long(SqlDataReader reader, int i) => reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);
}
