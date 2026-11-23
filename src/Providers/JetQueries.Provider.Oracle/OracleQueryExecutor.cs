using System.Data;
using System.Runtime.CompilerServices;
using JetQueries.Provider.Oracle.Language;
using JetQueries.Sdk;
using Oracle.ManagedDataAccess.Client;

namespace JetQueries.Provider.Oracle;

/// <summary>
/// Runs one statement or PL/SQL unit (Oracle takes one at a time; the script splitter cuts scripts up) and
/// streams what happens: rows in batches, then whatever the code printed through DBMS_OUTPUT, then — after a
/// CREATE of stored code — the compiler's errors, which Oracle keeps in the catalog instead of returning.
/// </summary>
internal sealed class OracleQueryExecutor(OracleConnection connection, Func<CancellationToken, Task<bool>>? serverCancel = null) : IQueryExecutor
{
    /// <summary>How long a break may take before the server is asked to cancel from a second connection.</summary>
    internal static TimeSpan BreakGrace { get; set; } = TimeSpan.FromSeconds(1.5);

    private const int MaxOutputLines = 5_000;

    public async IAsyncEnumerable<QueryEvent> ExecuteAsync(QueryRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var batchSize = Math.Max(1, request.BatchSize);

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Executing the user's SQL text is this method's purpose; values travel as bind variables.
            command.CommandText = request.Sql;
#pragma warning restore CA2100
            command.BindByName = true;
            command.CommandTimeout = request.Timeout is { } timeout ? Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)) : 0;

            // Whole LONG and LOB values with the row, instead of a round trip per cell.
            command.InitialLONGFetchSize = -1;
            command.InitialLOBFetchSize = -1;
            foreach (var parameter in request.Parameters)
            {
                command.Parameters.Add(new OracleParameter(parameter.Name, parameter.Value ?? DBNull.Value));
            }

            // Cancel sends a "break" to the server. Measured on Oracle 23 in Docker: a statement that is
            // computing or returning rows stops within a fraction of a second; one that is waiting (for a row
            // lock, in DBMS_SESSION.SLEEP) is only interrupted when the break arrives as TCP urgent data, which
            // Docker's port forwarding, and often NAT and VPN tunnels, drop (an in-band break is not read during a
            // wait either). So when the statement is still running a moment later, the server is asked to cancel
            // it from a second connection (ALTER SYSTEM CANCEL SQL, which needs that privilege). Simply dropping
            // the connection would not do: a waiting UPDATE would still run, and commit, once the lock is free.
            var running = new StrongBox<bool>(true);
            var registration = cancellationToken.Register(() =>
            {
                command.Cancel();
                if (serverCancel is { } cancelOnServer)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(BreakGrace).ConfigureAwait(false);
                        if (running.Value)
                        {
                            await cancelOnServer(CancellationToken.None).ConfigureAwait(false);
                        }
                    });
                }
            });
            try
            {
                await using (registration.ConfigureAwait(false))
                {
                    var reader = (OracleDataReader)await Translate(command.ExecuteReaderAsync(CommandBehavior.Default, cancellationToken), cancellationToken).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        var any = false;
                        do
                        {
                            if (reader.FieldCount == 0)
                            {
                                continue;
                            }

                            any = true;
                            var columns = new ResultColumn[reader.FieldCount];
                            for (var i = 0; i < columns.Length; i++)
                            {
                                columns[i] = new ResultColumn(reader.GetName(i), reader.GetDataTypeName(i), reader.GetFieldType(i));
                            }

                            yield return new ResultSetStarted(columns);

                            long rowCount = 0;
                            var truncated = false;
                            var rows = new List<object?[]>(batchSize);
                            while (await Translate(reader.ReadAsync(cancellationToken), cancellationToken).ConfigureAwait(false))
                            {
                                if (request.MaxRows is { } max && rowCount >= max)
                                {
                                    truncated = true;
                                    break;
                                }

                                var values = new object?[columns.Length];
                                for (var i = 0; i < values.Length; i++)
                                {
                                    values[i] = Read(reader, i);
                                }

                                rows.Add(values);
                                rowCount++;
                                if (rows.Count == batchSize)
                                {
                                    yield return new RowBatch(rows);
                                    rows = new List<object?[]>(batchSize);
                                }
                            }

                            if (rows.Count > 0)
                            {
                                yield return new RowBatch(rows);
                            }

                            yield return new ResultSetCompleted(rowCount, truncated);
                        }
                        while (await Translate(reader.NextResultAsync(cancellationToken), cancellationToken).ConfigureAwait(false));

                        if (!any)
                        {
                            // Only DML has a meaningful count; for DDL and PL/SQL the driver's number means nothing.
                            yield return new StatementCompleted(IsDml(request.Sql) && reader.RecordsAffected >= 0 ? reader.RecordsAffected : null);
                        }
                    }
                }
            }
            finally
            {
                // The statement has ended (or failed): no server cancel after this.
                running.Value = false;
            }
        }

        await foreach (var line in ReadOutputAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new QueryMessage(line, QueryMessageSeverity.Info);
        }

        await foreach (var error in ReadCompileErrorsAsync(request.Sql, cancellationToken).ConfigureAwait(false))
        {
            yield return error;
        }
    }

    /// <summary>
    /// A cell as a .NET value. Oracle's NUMBER holds 38 digits, more than <see cref="decimal"/>: such a value
    /// is returned as text rather than failing the whole result.
    /// </summary>
    private static object? Read(OracleDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            return reader.GetValue(ordinal);
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException)
        {
            return reader.GetOracleValue(ordinal).ToString();
        }
    }

    private static bool IsDml(string sql)
    {
        var lexer = new PlLexer(sql);
        return lexer.Next().Keyword is PlKeyword.Insert or PlKeyword.Update or PlKeyword.Delete or PlKeyword.Merge;
    }

    /// <summary>Lines written with DBMS_OUTPUT.PUT_LINE since the last call.</summary>
    private async IAsyncEnumerable<string> ReadOutputAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = "BEGIN DBMS_OUTPUT.GET_LINE(:line, :status); END;";
            command.BindByName = true;
            var line = command.Parameters.Add("line", OracleDbType.Varchar2, 32767, null, ParameterDirection.Output);
            var status = command.Parameters.Add("status", OracleDbType.Int32, ParameterDirection.Output);
            for (var i = 0; i < MaxOutputLines; i++)
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (Convert.ToInt32(status.Value.ToString(), System.Globalization.CultureInfo.InvariantCulture) != 0)
                {
                    yield break;
                }

                // An empty line comes back as a database NULL, whose text is "null".
                var value = line.Value is global::Oracle.ManagedDataAccess.Types.OracleString { IsNull: false } text ? text.Value : string.Empty;
                yield return value;
            }
        }
    }

    /// <summary>
    /// After CREATE PROCEDURE / FUNCTION / PACKAGE / TRIGGER / TYPE, the statement "succeeds" even when the code
    /// does not compile; the errors are in ALL_ERRORS. They are reported with their line in the source.
    /// </summary>
    private async IAsyncEnumerable<QueryMessage> ReadCompileErrorsAsync(string sql, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (StoredUnit(sql) is not var (type, owner, name))
        {
            yield break;
        }

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = """
                SELECT e.line, e.position, e.text, e.attribute
                FROM all_errors e
                WHERE e.owner = NVL(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')) AND e.name = :name AND e.type = :type
                ORDER BY e.sequence
                """;
            command.BindByName = true;
            command.Parameters.Add("owner", OracleDbType.Varchar2, owner, ParameterDirection.Input);
            command.Parameters.Add("name", OracleDbType.Varchar2, name, ParameterDirection.Input);
            command.Parameters.Add("type", OracleDbType.Varchar2, type, ParameterDirection.Input);
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var line = Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
                    var position = Convert.ToInt32(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture);
                    var severity = string.Equals(reader.GetString(3), "ERROR", StringComparison.Ordinal) ? QueryMessageSeverity.Error : QueryMessageSeverity.Warning;
                    yield return new QueryMessage(
                        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{type} {name}, line {line}, column {position}: {reader.GetString(2).TrimEnd()}"),
                        severity,
                        line);
                }
            }
        }
    }

    /// <summary>The catalog type, owner and name of the stored unit a CREATE statement defines, if it is one.</summary>
    internal static (string Type, string? Owner, string Name)? StoredUnit(string sql)
    {
        var unit = PlStructureParser.Parse(sql).Root.Children?.FirstOrDefault();
        var type = unit?.Kind switch
        {
            PlNodeKind.PackageSpec => "PACKAGE",
            PlNodeKind.PackageBody => "PACKAGE BODY",
            PlNodeKind.Procedure => "PROCEDURE",
            PlNodeKind.Function => "FUNCTION",
            PlNodeKind.Trigger => "TRIGGER",
            PlNodeKind.TypeSpec => "TYPE",
            PlNodeKind.TypeBody => "TYPE BODY",
            _ => null,
        };
        if (type is null || unit!.Name is null || !sql.AsSpan(unit.Start).TrimStart().StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // "schema.name", each part possibly quoted. Unquoted names are stored in upper case.
        var parts = new List<string>();
        var text = unit.Name;
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '"')
            {
                var close = text.IndexOf('"', i + 1);
                close = close < 0 ? text.Length : close;
                parts.Add(text[(i + 1)..close]);
                i = close + 1;
            }
            else
            {
                var dot = text.IndexOf('.', i);
                dot = dot < 0 ? text.Length : dot;
                parts.Add(text[i..dot].ToUpperInvariant());
                i = dot;
            }

            if (i < text.Length && text[i] == '.')
            {
                i++;
            }
        }

        return parts.Count switch
        {
            1 => (type, null, parts[0]),
            2 => (type, parts[0], parts[1]),
            _ => null,
        };
    }

    /// <summary>ODP.NET reports a canceled statement as ORA-01013; the SDK contract promises <see cref="OperationCanceledException"/>.</summary>
    private static async Task<T> Translate<T>(Task<T> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation.ConfigureAwait(false);
        }
        catch (OracleException ex) when (cancellationToken.IsCancellationRequested || ex.Number == 1013)
        {
            throw new OperationCanceledException("The query was canceled.", ex, cancellationToken);
        }
    }
}
