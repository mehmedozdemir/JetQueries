using System.Data;
using System.Globalization;
using JetQueries.Provider.SqlServer.Resources;
using JetQueries.Sdk;
using Microsoft.Data.SqlClient;

namespace JetQueries.Provider.SqlServer;

/// <summary>
/// The database detail screen on SQL Server: sys.databases, the log's space and virtual log files, files, the largest
/// tables, read inside the database through <c>[db].sys.sp_executesql</c> (the connection's own database is left as it
/// is), and the backup history from msdb. Constant text; the database name only as a bracketed identifier.
/// </summary>
internal static class SqlServerDatabaseDetail
{
    private const string StateSql = "/* JetQueries:advisor */ SELECT d.state_desc FROM sys.databases d WHERE d.name = @db;";

    // Run inside the database: DB_ID() and the sys views are that database's.
    private const string InsideSql = """
        /* JetQueries:advisor */
        SELECT d.name, d.state_desc, d.recovery_model_desc, d.compatibility_level, d.collation_name, SUSER_SNAME(d.owner_sid),
            TODATETIMEOFFSET(d.create_date, DATEPART(TZOFFSET, SYSDATETIMEOFFSET())), d.user_access_desc, d.is_read_only,
            d.is_read_committed_snapshot_on, d.snapshot_isolation_state_desc, d.is_encrypted, d.page_verify_option_desc,
            d.is_auto_close_on, d.is_auto_shrink_on, d.is_auto_create_stats_on, d.is_auto_update_stats_on, d.is_auto_update_stats_async_on,
            d.is_trustworthy_on, d.delayed_durability_desc, d.log_reuse_wait_desc,
            TODATETIMEOFFSET(CONVERT(datetime, DATABASEPROPERTYEX(d.name, 'LastGoodCheckDbTime')), DATEPART(TZOFFSET, SYSDATETIMEOFFSET())),
            (SELECT o.actual_state_desc FROM sys.database_query_store_options o)
        FROM sys.databases d WHERE d.database_id = DB_ID();
        SELECT u.total_log_size_in_bytes / 1048576.0, CONVERT(float, u.used_log_space_in_percent) FROM sys.dm_db_log_space_usage u;
        SELECT COUNT(*) FROM sys.dm_db_log_info(DB_ID());
        SELECT f.name, f.type_desc, fg.name, f.physical_name, f.size / 128.0, CONVERT(float, FILEPROPERTY(f.name, 'SpaceUsed')) / 128.0,
            f.is_percent_growth, f.growth, f.max_size
        FROM sys.database_files f LEFT JOIN sys.filegroups fg ON fg.data_space_id = f.data_space_id
        ORDER BY f.file_id;
        SELECT TOP (50) s.name + N'.' + t.name,
            SUM(CASE WHEN p.index_id IN (0, 1) THEN p.row_count ELSE 0 END),
            SUM(p.reserved_page_count) * 8 / 1024.0,
            SUM(CASE WHEN p.index_id IN (0, 1) THEN p.in_row_data_page_count + p.lob_used_page_count + p.row_overflow_used_page_count ELSE 0 END) * 8 / 1024.0,
            SUM(p.used_page_count) * 8 / 1024.0
        FROM sys.dm_db_partition_stats p
        JOIN sys.tables t ON t.object_id = p.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0
        GROUP BY s.name, t.name
        ORDER BY 3 DESC;
        SELECT COUNT(*),
            (SELECT SUM(p.row_count) FROM sys.dm_db_partition_stats p JOIN sys.tables x ON x.object_id = p.object_id
             WHERE p.index_id IN (0, 1) AND x.is_ms_shipped = 0)
        FROM sys.tables t WHERE t.is_ms_shipped = 0;
        """;

    private const string BackupsSql = """
        /* JetQueries:advisor */
        SELECT TOP (30) b.type,
            TODATETIMEOFFSET(b.backup_start_date, DATEPART(TZOFFSET, SYSDATETIMEOFFSET())),
            TODATETIMEOFFSET(b.backup_finish_date, DATEPART(TZOFFSET, SYSDATETIMEOFFSET())),
            b.backup_size / 1048576.0, b.compressed_backup_size / 1048576.0, m.physical_device_name, b.is_copy_only
        FROM msdb.dbo.backupset b
        LEFT JOIN msdb.dbo.backupmediafamily m ON m.media_set_id = b.media_set_id AND m.family_sequence_number = 1
        WHERE b.database_name = @db AND b.backup_finish_date IS NOT NULL
        ORDER BY b.backup_finish_date DESC;
        """;

    public static async Task<DatabaseDetail> ReadAsync(SqlConnection connection, string database, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(database);
        string? state = null;
        await ReadAsync(connection, StateSql, command => command.Parameters.Add(Name(database)), reader => state = reader.GetString(0), cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            throw new ArgumentException(Messages.NoSuchDatabase(database), nameof(database));
        }

        var (backups, known) = await BackupsAsync(connection, database, cancellationToken).ConfigureAwait(false);
        if (state != "ONLINE")
        {
            // Nothing can be read inside a database that is not online.
            return new DatabaseDetail(database) { State = state, Backups = backups, BackupsKnown = known };
        }

        var detail = new DatabaseDetail(database);
        var files = new List<DatabaseFileInfo>();
        var tables = new List<TableSpace>();
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // A bracketed database name; the batch is a parameter.
            command.CommandText = "EXEC " + SqlServerNames.Quote(database) + ".sys.sp_executesql @batch;";
#pragma warning restore CA2100
            command.Parameters.Add(new SqlParameter("@batch", SqlDbType.NVarChar, -1) { Value = InsideSql });
            command.CommandTimeout = 60;
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    detail = detail with
                    {
                        State = Text(reader, 1),
                        RecoveryModel = Text(reader, 2),
                        CompatibilityLevel = reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                        Collation = Text(reader, 4),
                        Owner = Text(reader, 5),
                        Created = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                        LogReuseWait = Text(reader, 20),
                        LastGoodCheckDb = reader.IsDBNull(21) ? null : reader.GetFieldValue<DateTimeOffset>(21),
                        Options =
                        [
                            new("userAccess", "User access", Text(reader, 7)),
                            new("readOnly", "Read only", OnOff(reader, 8)),
                            new("readCommittedSnapshot", "Read committed snapshot", OnOff(reader, 9)),
                            new("snapshotIsolation", "Snapshot isolation", Text(reader, 10)),
                            new("encrypted", "Encrypted (TDE)", OnOff(reader, 11)),
                            new("pageVerify", "Page verify", Text(reader, 12)),
                            new("autoClose", "Auto close", OnOff(reader, 13)),
                            new("autoShrink", "Auto shrink", OnOff(reader, 14)),
                            new("autoCreateStats", "Auto create statistics", OnOff(reader, 15)),
                            new("autoUpdateStats", "Auto update statistics", OnOff(reader, 16)),
                            new("autoUpdateStatsAsync", "Auto update statistics asynchronously", OnOff(reader, 17)),
                            new("trustworthy", "Trustworthy", OnOff(reader, 18)),
                            new("delayedDurability", "Delayed durability", Text(reader, 19)),
                            new("queryStore", "Query Store", Text(reader, 22)),
                        ],
                    };
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    detail = detail with { LogUsedPercent = reader.IsDBNull(1) ? null : reader.GetDouble(1) };
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    detail = detail with { VirtualLogFiles = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture) };
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var percent = reader.GetBoolean(6);
                    var growth = Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture);
                    var max = Convert.ToInt64(reader.GetValue(8), CultureInfo.InvariantCulture);
                    files.Add(new DatabaseFileInfo(reader.GetString(0))
                    {
                        Type = Text(reader, 1),
                        Filegroup = Text(reader, 2),
                        Path = Text(reader, 3),
                        SizeMb = Number(reader, 4) ?? 0,
                        UsedMb = Number(reader, 5),
                        Growth = growth == 0 ? "0" : percent ? growth.ToString(CultureInfo.InvariantCulture) + " %" : Mb(growth * 8 / 1024.0),
                        MaxSize = max == -1 || max == 268_435_456 ? "∞" : Mb(max * 8 / 1024.0),
                    });
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var used = Number(reader, 4) ?? 0;
                    var data = Number(reader, 3) ?? 0;
                    tables.Add(new TableSpace(reader.GetString(0))
                    {
                        Rows = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                        ReservedMb = Number(reader, 2) ?? 0,
                        DataMb = data,
                        IndexMb = Math.Max(0, used - data),
                    });
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    detail = detail with
                    {
                        TableCount = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                        TotalRows = reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    };
                }
            }
        }

        var dataFiles = files.Where(static f => f.Type != "LOG").ToList();
        var logFiles = files.Where(static f => f.Type == "LOG").ToList();
        return detail with
        {
            Files = files,
            Tables = tables,
            DataMb = dataFiles.Sum(static f => f.SizeMb),
            DataUsedMb = dataFiles.All(static f => f.UsedMb is not null) ? dataFiles.Sum(static f => f.UsedMb!.Value) : null,
            LogMb = logFiles.Sum(static f => f.SizeMb),
            Backups = backups,
            BackupsKnown = known,
        };
    }

    private static async Task<(IReadOnlyList<BackupRecord> Backups, bool Known)> BackupsAsync(SqlConnection connection, string database, CancellationToken cancellationToken)
    {
        var backups = new List<BackupRecord>();
        try
        {
            await ReadAsync(connection, BackupsSql, command => command.Parameters.Add(Name(database)), reader =>
            {
                var kind = Text(reader, 0) switch
                {
                    "D" => BackupKind.Full,
                    "I" => BackupKind.Differential,
                    "L" => BackupKind.Log,
                    _ => BackupKind.Other,
                };
                backups.Add(new BackupRecord(kind, reader.GetFieldValue<DateTimeOffset>(2))
                {
                    Started = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
                    SizeMb = Number(reader, 3),
                    CompressedMb = Number(reader, 4),
                    Device = Text(reader, 5),
                    CopyOnly = !reader.IsDBNull(6) && reader.GetBoolean(6),
                });
            }, cancellationToken).ConfigureAwait(false);
            return (backups, true);
        }
        catch (SqlException)
        {
            return ([], false);
        }
    }

    private static SqlParameter Name(string database) => new("@db", SqlDbType.NVarChar, 128) { Value = database };

    private static async Task ReadAsync(SqlConnection connection, string sql, Action<SqlCommand> parameters, Action<SqlDataReader> row, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Constant text.
            command.CommandText = sql;
#pragma warning restore CA2100
            command.CommandTimeout = 30;
            parameters(command);
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    row(reader);
                }
            }
        }
    }

    private static string Mb(double mb) => mb.ToString("0.#", CultureInfo.InvariantCulture) + " MB";

    private static string? Text(SqlDataReader reader, int i) => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);

    private static string? OnOff(SqlDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetBoolean(i) ? "ON" : "OFF";

    private static double? Number(SqlDataReader reader, int i) => reader.IsDBNull(i) ? null : Convert.ToDouble(reader.GetValue(i), CultureInfo.InvariantCulture);
}
