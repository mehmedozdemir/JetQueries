using System.Globalization;
using JetQueries.Sdk;
using Microsoft.Data.SqlClient;

namespace JetQueries.Provider.SqlServer;

/// <summary>
/// The activity monitor's reads on SQL Server: sys.dm_exec_sessions with each session's request, the statement it
/// runs (or last sent) and who blocks it; <c>KILL</c> to end one. Dynamic management views only, constant text.
/// </summary>
internal static class SqlServerActivity
{
    // One row per session; the request's figures when it runs something, else the session's.
    private const string SessionsSql = """
        /* JetQueries:advisor */
        SELECT
            s.session_id,
            s.login_name,
            s.host_name,
            s.program_name,
            DB_NAME(COALESCE(r.database_id, s.database_id)) AS database_name,
            COALESCE(r.status, s.status) AS status,
            r.command,
            r.wait_type,
            r.wait_time,
            NULLIF(r.blocking_session_id, 0) AS blocked_by,
            COALESCE(r.cpu_time, s.cpu_time) AS cpu_time,
            COALESCE(r.logical_reads, s.logical_reads) AS reads,
            COALESCE(r.writes, s.writes) AS writes,
            r.total_elapsed_time,
            s.open_transaction_count,
            TODATETIMEOFFSET(s.login_time, DATEPART(TZOFFSET, SYSDATETIMEOFFSET())) AS login_time,
            TODATETIMEOFFSET(s.last_request_end_time, DATEPART(TZOFFSET, SYSDATETIMEOFFSET())) AS last_request_end,
            CAST(s.is_user_process AS int) AS is_user,
            COALESCE(
                SUBSTRING(t.text, r.statement_start_offset / 2 + 1,
                    (CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) ELSE r.statement_end_offset END - r.statement_start_offset) / 2 + 1),
                last_sql.text) AS sql_text
        FROM sys.dm_exec_sessions s
        LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
        LEFT JOIN sys.dm_exec_connections c ON c.session_id = s.session_id AND c.parent_connection_id IS NULL
        OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
        OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) last_sql
        ORDER BY s.session_id;
        SELECT @@SPID, HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE');
        """;

    public static async Task<ActivitySnapshot> ReadAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var sessions = new List<ActivitySession>();
        string? own = null;
        var canSeeAll = true;
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Constant text.
            command.CommandText = SessionsSql;
#pragma warning restore CA2100
            command.CommandTimeout = 30;
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    sessions.Add(new ActivitySession(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!)
                    {
                        Login = Text(reader, 1),
                        Host = Text(reader, 2),
                        Program = Text(reader, 3),
                        Database = Text(reader, 4),
                        Status = Text(reader, 5),
                        Command = Text(reader, 6),
                        WaitType = Text(reader, 7),
                        WaitTime = Milliseconds(reader, 8),
                        BlockedBy = reader.IsDBNull(9) ? null : Convert.ToString(reader.GetValue(9), CultureInfo.InvariantCulture),
                        CpuTime = Milliseconds(reader, 10),
                        Reads = Number(reader, 11),
                        Writes = Number(reader, 12),
                        Elapsed = Milliseconds(reader, 13),
                        OpenTransactions = reader.IsDBNull(14) ? null : Convert.ToInt32(reader.GetValue(14), CultureInfo.InvariantCulture),
                        LoginTime = reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
                        LastRequestEnd = reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
                        IsUserSession = !reader.IsDBNull(17) && reader.GetInt32(17) == 1,
                        Sql = Text(reader, 18)?.Trim(),
                    });
                }

                if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false) && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    own = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
                    canSeeAll = !reader.IsDBNull(1) && Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) == 1;
                }
            }
        }

        return new ActivitySnapshot(DateTimeOffset.Now, sessions, own)
        {
            Limitation = canSeeAll ? null : "The login lacks VIEW SERVER STATE: only its own sessions are shown.",
        };
    }

    public static async Task KillAsync(SqlConnection connection, string sessionId, CancellationToken cancellationToken)
    {
        // KILL takes no parameter: the id is checked to be a number and written as one.
        if (!int.TryParse(sessionId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            throw new ArgumentException("Not a SQL Server session id: " + sessionId, nameof(sessionId));
        }

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // A validated integer.
            command.CommandText = "KILL " + id.ToString(CultureInfo.InvariantCulture);
#pragma warning restore CA2100
            command.CommandTimeout = 30;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? Text(SqlDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

    private static long? Number(SqlDataReader reader, int i) =>
        reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);

    private static TimeSpan? Milliseconds(SqlDataReader reader, int i) =>
        reader.IsDBNull(i) ? null : TimeSpan.FromMilliseconds(Convert.ToDouble(reader.GetValue(i), CultureInfo.InvariantCulture));
}
