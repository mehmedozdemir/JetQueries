using System.Data;
using System.Globalization;
using System.Text;
using JetQueries.Sdk;
using Microsoft.Data.SqlClient;

namespace JetQueries.Provider.SqlServer;

/// <summary>
/// The performance analysis on SQL Server: heavy statements from sys.dm_exec_query_stats (grouped by query hash),
/// missing indexes from sys.dm_db_missing_index_*, index use from sys.dm_db_index_usage_stats with each index's
/// columns, and fragmentation from sys.dm_db_index_physical_stats in LIMITED mode. Constant text with parameters;
/// a database name is only ever written as a bracketed identifier.
/// </summary>
internal static class SqlServerPerformance
{
    private const string StartSql = "/* JetQueries:advisor */ SELECT TODATETIMEOFFSET(sqlserver_start_time, DATEPART(TZOFFSET, SYSDATETIMEOFFSET())) FROM sys.dm_os_sys_info";

    // One row per query hash: the costs of all its cached plans added up, the text of its costliest one.
    private const string HeavySql = """
        /* JetQueries:advisor */
        SELECT TOP (@top)
            SUBSTRING(t.text, x.statement_start_offset / 2 + 1,
                (CASE x.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE x.statement_end_offset END - x.statement_start_offset) / 2 + 1) AS sql_text,
            DB_NAME(COALESCE(t.dbid, pa.dbid)) AS database_name,
            CASE WHEN t.objectid IS NULL THEN NULL ELSE OBJECT_SCHEMA_NAME(t.objectid, t.dbid) + '.' + OBJECT_NAME(t.objectid, t.dbid) END AS object_name,
            x.executions, x.cpu, x.duration, x.reads, x.writes, x.rows_returned,
            TODATETIMEOFFSET(x.last_execution, DATEPART(TZOFFSET, SYSDATETIMEOFFSET())) AS last_execution
        FROM (
            SELECT qs.sql_handle, qs.plan_handle, qs.statement_start_offset, qs.statement_end_offset,
                SUM(qs.execution_count) OVER (PARTITION BY qs.query_hash) AS executions,
                SUM(qs.total_worker_time) OVER (PARTITION BY qs.query_hash) AS cpu,
                SUM(qs.total_elapsed_time) OVER (PARTITION BY qs.query_hash) AS duration,
                SUM(qs.total_logical_reads) OVER (PARTITION BY qs.query_hash) AS reads,
                SUM(qs.total_logical_writes) OVER (PARTITION BY qs.query_hash) AS writes,
                SUM(qs.total_rows) OVER (PARTITION BY qs.query_hash) AS rows_returned,
                MAX(qs.last_execution_time) OVER (PARTITION BY qs.query_hash) AS last_execution,
                ROW_NUMBER() OVER (PARTITION BY qs.query_hash ORDER BY qs.total_worker_time DESC) AS rn
            FROM sys.dm_exec_query_stats qs
        ) x
        CROSS APPLY sys.dm_exec_sql_text(x.sql_handle) t
        OUTER APPLY (SELECT CONVERT(int, a.value) AS dbid FROM sys.dm_exec_plan_attributes(x.plan_handle) a WHERE a.attribute = 'dbid') pa
        WHERE x.rn = 1
          AND (@db IS NULL OR COALESCE(t.dbid, pa.dbid) = DB_ID(@db))
          AND COALESCE(t.dbid, pa.dbid, 0) <> 32767
          AND t.text NOT LIKE N'%JetQueries:advisor%' 
        ORDER BY CASE @order
            WHEN 0 THEN CONVERT(float, x.cpu)
            WHEN 1 THEN CONVERT(float, x.cpu) / NULLIF(x.executions, 0)
            WHEN 2 THEN CONVERT(float, x.duration)
            WHEN 3 THEN CONVERT(float, x.duration) / NULLIF(x.executions, 0)
            WHEN 4 THEN CONVERT(float, x.reads)
            WHEN 5 THEN CONVERT(float, x.reads) / NULLIF(x.executions, 0)
            ELSE CONVERT(float, x.executions) END DESC;
        """;

    private const string MissingSql = """
        /* JetQueries:advisor */
        SELECT TOP (200)
            DB_NAME(d.database_id), d.statement, d.equality_columns, d.inequality_columns, d.included_columns,
            s.user_seeks + s.user_scans, s.avg_user_impact,
            s.avg_total_user_cost * (s.avg_user_impact / 100.0) * (s.user_seeks + s.user_scans) AS score,
            TODATETIMEOFFSET(COALESCE(s.last_user_seek, s.last_user_scan), DATEPART(TZOFFSET, SYSDATETIMEOFFSET()))
        FROM sys.dm_db_missing_index_details d
        JOIN sys.dm_db_missing_index_groups g ON g.index_handle = d.index_handle
        JOIN sys.dm_db_missing_index_group_stats s ON s.group_handle = g.index_group_handle
        WHERE (@db IS NULL OR d.database_id = DB_ID(@db)) AND d.database_id > 4
        ORDER BY score DESC;
        """;

    // {0} is the bracketed database name followed by a dot.
    private const string UsageSql = """
        /* JetQueries:advisor */
        SELECT sc.name, o.name, i.name, CAST(CASE WHEN i.index_id = 1 THEN 1 ELSE 0 END AS bit), i.is_unique,
            CAST(CASE WHEN i.is_primary_key = 1 OR i.is_unique_constraint = 1 THEN 1 ELSE 0 END AS bit),
            STUFF((SELECT ', ' + c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                   FROM {0}sys.index_columns ic JOIN {0}sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0 AND ic.key_ordinal > 0
                   ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '') AS key_columns,
            STUFF((SELECT ', ' + c.name
                   FROM {0}sys.index_columns ic JOIN {0}sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                   ORDER BY ic.index_column_id FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '') AS included_columns,
            i.filter_definition,
            COALESCE(us.user_seeks, 0), COALESCE(us.user_scans, 0), COALESCE(us.user_lookups, 0), COALESCE(us.user_updates, 0),
            TODATETIMEOFFSET((SELECT MAX(v) FROM (VALUES (us.last_user_seek), (us.last_user_scan), (us.last_user_lookup)) AS r(v)),
                DATEPART(TZOFFSET, SYSDATETIMEOFFSET())),
            (SELECT SUM(ps.used_page_count) * 8 FROM {0}sys.dm_db_partition_stats ps WHERE ps.object_id = i.object_id AND ps.index_id = i.index_id),
            (SELECT SUM(ps.row_count) FROM {0}sys.dm_db_partition_stats ps WHERE ps.object_id = i.object_id AND ps.index_id = i.index_id)
        FROM {0}sys.indexes i
        JOIN {0}sys.objects o ON o.object_id = i.object_id
        JOIN {0}sys.schemas sc ON sc.schema_id = o.schema_id
        LEFT JOIN sys.dm_db_index_usage_stats us ON us.database_id = DB_ID(@db) AND us.object_id = i.object_id AND us.index_id = i.index_id
        WHERE o.type = 'U' AND o.is_ms_shipped = 0 AND i.index_id > 0 AND i.is_hypothetical = 0 AND i.is_disabled = 0
        ORDER BY sc.name, o.name, i.index_id;
        """;

    private const string FragmentationSql = """
        /* JetQueries:advisor */
        SELECT sc.name, o.name, i.name, ps.avg_fragmentation_in_percent, ps.page_count, ps.partition_number,
            (SELECT COUNT(*) FROM {0}sys.partitions p WHERE p.object_id = i.object_id AND p.index_id = i.index_id)
        FROM sys.dm_db_index_physical_stats(DB_ID(@db), NULL, NULL, NULL, 'LIMITED') ps
        JOIN {0}sys.indexes i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
        JOIN {0}sys.objects o ON o.object_id = i.object_id
        JOIN {0}sys.schemas sc ON sc.schema_id = o.schema_id
        WHERE ps.index_id > 0 AND ps.alloc_unit_type_desc = 'IN_ROW_DATA' AND ps.page_count >= @pages
          AND ps.avg_fragmentation_in_percent >= 5 AND o.is_ms_shipped = 0
        ORDER BY ps.avg_fragmentation_in_percent * ps.page_count DESC;
        """;

    public static async Task<HeavyQueryReport> HeavyAsync(SqlConnection connection, HeavyQueryRequest request, CancellationToken cancellationToken)
    {
        var queries = new List<HeavyQuery>();
        await ReadAsync(connection, HeavySql, command =>
        {
            command.Parameters.Add(new SqlParameter("@top", SqlDbType.Int) { Value = Math.Clamp(request.Top, 1, 1000) });
            command.Parameters.Add(new SqlParameter("@order", SqlDbType.Int) { Value = (int)request.Order });
            command.Parameters.Add(Database(request.Database));
        }, reader =>
        {
            queries.Add(new HeavyQuery(Text(reader, 0)?.Trim() ?? string.Empty)
            {
                Database = Text(reader, 1),
                ObjectName = Text(reader, 2),
                Executions = Long(reader, 3),
                TotalCpu = Microseconds(reader, 4),
                TotalDuration = Microseconds(reader, 5),
                TotalReads = Long(reader, 6),
                TotalWrites = Long(reader, 7),
                TotalRows = reader.IsDBNull(8) ? null : Long(reader, 8),
                LastExecution = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            });
        }, cancellationToken).ConfigureAwait(false);
        return new HeavyQueryReport(queries, await StartAsync(connection, cancellationToken).ConfigureAwait(false));
    }

    public static async Task<IReadOnlyList<MissingIndex>> MissingAsync(SqlConnection connection, string? database, CancellationToken cancellationToken)
    {
        var indexes = new List<MissingIndex>();
        await ReadAsync(connection, MissingSql, command => command.Parameters.Add(Database(database)), reader =>
        {
            var statement = Text(reader, 1) ?? string.Empty;
            var equality = Columns(Text(reader, 2));
            var inequality = Columns(Text(reader, 3));
            var included = Columns(Text(reader, 4));
            indexes.Add(new MissingIndex(Text(reader, 0) ?? string.Empty, TableOf(statement), CreateScript(statement, equality, inequality, included))
            {
                EqualityColumns = [.. equality.Select(Unquote)],
                InequalityColumns = [.. inequality.Select(Unquote)],
                IncludedColumns = [.. included.Select(Unquote)],
                Uses = Long(reader, 5),
                ImpactPercent = Double(reader, 6),
                Score = Double(reader, 7),
                LastWanted = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            });
        }, cancellationToken).ConfigureAwait(false);
        return indexes;
    }

    public static async Task<IndexUsageReport> UsageAsync(SqlConnection connection, string database, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(database);
        var prefix = SqlServerNames.CatalogPrefix(database);
        var indexes = new List<IndexUsage>();
        await ReadAsync(connection, UsageSql.Replace("{0}", prefix, StringComparison.Ordinal), command => command.Parameters.Add(Database(database)), reader =>
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            var name = Text(reader, 2) ?? string.Empty;
            var clustered = reader.GetBoolean(3);
            var constraint = reader.GetBoolean(5);
            indexes.Add(new IndexUsage(schema + "." + table, name)
            {
                IsClustered = clustered,
                IsUnique = reader.GetBoolean(4),
                IsConstraint = constraint,
                KeyColumns = Split(Text(reader, 6)),
                IncludedColumns = Split(Text(reader, 7)),
                Filter = Text(reader, 8),
                Seeks = Long(reader, 9),
                Scans = Long(reader, 10),
                Lookups = Long(reader, 11),
                Updates = Long(reader, 12),
                LastRead = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
                SizeKb = reader.IsDBNull(14) ? 0 : Long(reader, 14),
                Rows = reader.IsDBNull(15) ? null : Long(reader, 15),
                DropScript = clustered || constraint
                    ? null
                    : "DROP INDEX " + SqlServerNames.Quote(name) + " ON " + SqlServerNames.Quote(new DatabaseObjectName(database, schema, table)) + ";",
            });
        }, cancellationToken).ConfigureAwait(false);
        return new IndexUsageReport(indexes, await StartAsync(connection, cancellationToken).ConfigureAwait(false));
    }

    public static async Task<IReadOnlyList<IndexFragmentation>> FragmentationAsync(SqlConnection connection, string database, long minimumPages, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(database);
        var prefix = SqlServerNames.CatalogPrefix(database);
        var indexes = new List<IndexFragmentation>();
        await ReadAsync(connection, FragmentationSql.Replace("{0}", prefix, StringComparison.Ordinal), command =>
        {
            command.Parameters.Add(Database(database));
            command.Parameters.Add(new SqlParameter("@pages", SqlDbType.BigInt) { Value = Math.Max(0, minimumPages) });
        }, reader =>
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            var name = reader.GetString(2);
            var percent = Double(reader, 3);
            var partitioned = Long(reader, 6) > 1;
            var partition = partitioned ? Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture) : (int?)null;
            var rebuild = percent >= 30;
            var script = new StringBuilder("ALTER INDEX ").Append(SqlServerNames.Quote(name)).Append(" ON ")
                .Append(SqlServerNames.Quote(new DatabaseObjectName(database, schema, table)))
                .Append(rebuild ? " REBUILD" : " REORGANIZE");
            if (partition is { } number)
            {
                script.Append(" PARTITION = ").Append(number.ToString(CultureInfo.InvariantCulture));
            }

            indexes.Add(new IndexFragmentation(schema + "." + table, name)
            {
                FragmentationPercent = percent,
                Pages = Long(reader, 4),
                Partition = partition,
                Rebuild = rebuild,
                MaintenanceScript = script.Append(';').ToString(),
            });
        }, cancellationToken).ConfigureAwait(false);
        return indexes;
    }

    // "[a], [b]" as the missing-index views write column lists.
    internal static IReadOnlyList<string> Columns(string? list) =>
        string.IsNullOrWhiteSpace(list) ? [] : [.. list.Split("], [", StringSplitOptions.None).Select(static c => "[" + c.Trim().TrimStart('[').TrimEnd(']') + "]")];

    internal static string Unquote(string quoted) => quoted.Length >= 2 && quoted[0] == '[' && quoted[^1] == ']' ? quoted[1..^1].Replace("]]", "]", StringComparison.Ordinal) : quoted;

    // "[db].[dbo].[Orders]" → "dbo.Orders".
    internal static string TableOf(string statement)
    {
        var parts = statement.Split("].[", StringSplitOptions.None).Select(static p => p.Trim('[', ']')).ToArray();
        return parts.Length >= 2 ? parts[^2] + "." + parts[^1] : statement;
    }

    internal static string CreateScript(string statement, IReadOnlyList<string> equality, IReadOnlyList<string> inequality, IReadOnlyList<string> included)
    {
        var keys = equality.Concat(inequality).ToList();
        var table = TableOf(statement);
        var name = new StringBuilder("IX_").Append(table.Split('.')[^1]);
        foreach (var column in keys)
        {
            name.Append('_').Append(Unquote(column));
        }

        var safe = new string([.. name.ToString().Select(static c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')]);
        if (safe.Length > 120)
        {
            safe = safe[..120];
        }

        var script = new StringBuilder()
            .Append("-- Proposed by SQL Server's missing index statistics: check the table's existing indexes before creating it.\n")
            .Append("CREATE NONCLUSTERED INDEX ").Append(SqlServerNames.Quote(safe)).Append(" ON ").Append(statement)
            .Append(" (").Append(string.Join(", ", keys)).Append(')');
        if (included.Count > 0)
        {
            script.Append(" INCLUDE (").Append(string.Join(", ", included)).Append(')');
        }

        return script.Append(';').ToString();
    }

    private static IReadOnlyList<string> Split(string? list) =>
        string.IsNullOrWhiteSpace(list) ? [] : [.. list.Split(", ", StringSplitOptions.RemoveEmptyEntries)];

    private static SqlParameter Database(string? database) =>
        new("@db", SqlDbType.NVarChar, 128) { Value = string.IsNullOrEmpty(database) ? DBNull.Value : database };

    private static async Task<DateTimeOffset?> StartAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        DateTimeOffset? start = null;
        await ReadAsync(connection, StartSql, static _ => { }, reader => start = reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0), cancellationToken)
            .ConfigureAwait(false);
        return start;
    }

    private static async Task ReadAsync(SqlConnection connection, string sql, Action<SqlCommand> parameters, Action<SqlDataReader> row, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Constant text; a database name only as a bracketed identifier.
            command.CommandText = sql;
#pragma warning restore CA2100
            command.CommandTimeout = 120;
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

    private static string? Text(SqlDataReader reader, int i) => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);

    private static long Long(SqlDataReader reader, int i) => reader.IsDBNull(i) ? 0 : Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);

    private static double Double(SqlDataReader reader, int i) => reader.IsDBNull(i) ? 0 : Convert.ToDouble(reader.GetValue(i), CultureInfo.InvariantCulture);

    private static TimeSpan Microseconds(SqlDataReader reader, int i) => TimeSpan.FromMicroseconds(Long(reader, i));
}
