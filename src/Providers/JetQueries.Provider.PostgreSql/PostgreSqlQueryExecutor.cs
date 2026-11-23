using System.Collections;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using JetQueries.Sdk;
using Npgsql;
using NpgsqlTypes;

namespace JetQueries.Provider.PostgreSql;

/// <summary>
/// Runs one statement (the script splitter cuts scripts up, so each reports on its own) and streams what
/// happens: rows in batches, and the NOTICE / WARNING messages the server sends while it runs.
/// </summary>
internal sealed class PostgreSqlQueryExecutor(NpgsqlConnection connection) : IQueryExecutor
{
    public async IAsyncEnumerable<QueryEvent> ExecuteAsync(QueryRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var batchSize = Math.Max(1, request.BatchSize);

        // Npgsql raises notices on the thread that is reading, between our awaits; the queue carries them to
        // the next point where this iterator may yield.
        var pending = new ConcurrentQueue<QueryMessage>();
        void OnNotice(object sender, NpgsqlNoticeEventArgs e) => pending.Enqueue(ToMessage(e.Notice));

        connection.Notice += OnNotice;
        try
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
#pragma warning disable CA2100 // Executing the user's SQL text is this method's purpose; values travel as parameters.
                command.CommandText = request.Sql;
#pragma warning restore CA2100
                command.CommandTimeout = request.Timeout is { } timeout ? Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)) : 0;
                foreach (var parameter in request.Parameters)
                {
                    command.Parameters.Add(new NpgsqlParameter(parameter.Name, parameter.Value ?? DBNull.Value));
                }

                var reader = await Translate(() => command.ExecuteReaderAsync(CommandBehavior.Default, cancellationToken), request.Sql, cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    var any = false;
                    do
                    {
                        while (pending.TryDequeue(out var message))
                        {
                            yield return message;
                        }

                        if (reader.FieldCount == 0)
                        {
                            continue;
                        }

                        any = true;
                        var columns = new ResultColumn[reader.FieldCount];
                        for (var i = 0; i < columns.Length; i++)
                        {
                            columns[i] = new ResultColumn(reader.GetName(i), reader.GetDataTypeName(i), FieldType(reader, i));
                        }

                        yield return new ResultSetStarted(columns);

                        long rowCount = 0;
                        var truncated = false;
                        var rows = new List<object?[]>(batchSize);
                        while (await Translate(() => reader.ReadAsync(cancellationToken), request.Sql, cancellationToken).ConfigureAwait(false))
                        {
                            if (request.MaxRows is { } max && rowCount >= max)
                            {
                                truncated = true;
                                break;
                            }

                            var values = new object?[columns.Length];
                            for (var i = 0; i < values.Length; i++)
                            {
                                values[i] = Read(reader, i, columns[i].DataType);
                            }

                            rows.Add(values);
                            rowCount++;
                            if (rows.Count == batchSize)
                            {
                                yield return new RowBatch(rows);
                                rows = new List<object?[]>(batchSize);
                                while (pending.TryDequeue(out var message))
                                {
                                    yield return message;
                                }
                            }
                        }

                        if (rows.Count > 0)
                        {
                            yield return new RowBatch(rows);
                        }

                        yield return new ResultSetCompleted(rowCount, truncated);
                    }
                    while (await Translate(() => reader.NextResultAsync(cancellationToken), request.Sql, cancellationToken).ConfigureAwait(false));

                    while (pending.TryDequeue(out var message))
                    {
                        yield return message;
                    }

                    if (!any)
                    {
                        // Npgsql reports -1 for statements without a row count (DDL, SET, DO).
                        yield return new StatementCompleted(reader.RecordsAffected >= 0 ? reader.RecordsAffected : null);
                    }
                }
            }
        }
        finally
        {
            connection.Notice -= OnNotice;
        }

        while (pending.TryDequeue(out var message))
        {
            yield return message;
        }
    }

    private static QueryMessage ToMessage(PostgresNotice notice)
    {
        var text = new StringBuilder(notice.MessageText);
        if (!string.IsNullOrEmpty(notice.Detail))
        {
            text.Append("\nDetail: ").Append(notice.Detail);
        }

        if (!string.IsNullOrEmpty(notice.Hint))
        {
            text.Append("\nHint: ").Append(notice.Hint);
        }

        var severity = notice.InvariantSeverity is "WARNING" ? QueryMessageSeverity.Warning : QueryMessageSeverity.Info;
        return new QueryMessage(text.ToString(), severity);
    }

    private static Type FieldType(NpgsqlDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetFieldType(ordinal);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidCastException or ArgumentException)
        {
            return typeof(object);
        }
    }

    /// <summary>
    /// A cell as a value the grid can show. Values .NET cannot hold — a NUMERIC beyond <see cref="decimal"/>,
    /// an INTERVAL with months, a type without a .NET mapping — become text rather than failing the whole result.
    /// </summary>
    private static object? Read(NpgsqlDataReader reader, int ordinal, string dataType)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            return Simplify(reader.GetValue(ordinal), dataType);
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or NotSupportedException or ArgumentException)
        {
            try
            {
                return dataType switch
                {
                    "interval" => FormatInterval(reader.GetFieldValue<NpgsqlInterval>(ordinal)),
                    "numeric" => reader.GetFieldValue<BigInteger>(ordinal).ToString(CultureInfo.InvariantCulture),

                    // Enums and other types without a .NET mapping still have a text form.
                    _ => reader.GetFieldValue<string>(ordinal),
                };
            }
            catch (Exception inner) when (inner is InvalidCastException or OverflowException or NotSupportedException or ArgumentException)
            {
                return $"({dataType})";
            }
        }
    }

    /// <summary>Arrays, bit strings and hstore as PostgreSQL writes them; a UTC timestamptz with its offset.</summary>
    internal static object? Simplify(object? value, string dataType) => value switch
    {
        null or DBNull => null,
        DateTime { Kind: DateTimeKind.Utc } utc when dataType.StartsWith("timestamp with time zone", StringComparison.Ordinal) => new DateTimeOffset(utc),
        byte[] => value,
        string => value,
        BitArray bits => FormatBits(bits),
        IDictionary<string, string?> hstore => string.Join(", ", hstore.Select(static p => $"\"{p.Key}\"=>{(p.Value is null ? "NULL" : "\"" + p.Value + "\"")}")),
        Array array => FormatArray(array),
        _ => value,
    };

    private static string FormatBits(BitArray bits)
    {
        var text = new StringBuilder(bits.Length);
        for (var i = 0; i < bits.Length; i++)
        {
            text.Append(bits[i] ? '1' : '0');
        }

        return text.ToString();
    }

    private static string FormatArray(Array array)
    {
        var text = new StringBuilder("{");
        var first = true;
        foreach (var item in array)
        {
            text.Append(first ? string.Empty : ",");
            first = false;
            text.Append(Simplify(item, string.Empty) switch
            {
                null => "NULL",
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                var other => other.ToString(),
            });
        }

        return text.Append('}').ToString();
    }

    private static string FormatInterval(NpgsqlInterval interval)
    {
        var parts = new List<string>();
        if (interval.Months != 0)
        {
            var years = interval.Months / 12;
            var months = interval.Months % 12;
            if (years != 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{years} year{(Math.Abs(years) == 1 ? string.Empty : "s")}"));
            }

            if (months != 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{months} mon{(Math.Abs(months) == 1 ? string.Empty : "s")}"));
            }
        }

        if (interval.Days != 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{interval.Days} day{(Math.Abs(interval.Days) == 1 ? string.Empty : "s")}"));
        }

        if (interval.Time != 0 || parts.Count == 0)
        {
            parts.Add(TimeSpan.FromTicks(interval.Time * 10).ToString("c", CultureInfo.InvariantCulture));
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// The SDK contract promises <see cref="OperationCanceledException"/> for a canceled query; a server error
    /// becomes one exception whose message carries the detail, hint and position the server sent.
    /// </summary>
    private static async Task<T> Translate<T>(Func<Task<T>> operation, string sql, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.QueryCanceled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The query was canceled.", ex, cancellationToken);
        }
        catch (NpgsqlException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The query was canceled.", ex, cancellationToken);
        }
        catch (PostgresException ex)
        {
            throw new PostgreSqlQueryException(ex, sql);
        }
    }
}

/// <summary>A server error, with what the server said about it in the message.</summary>
#pragma warning disable CA1032 // Created only from a server error; the standard constructors would make an empty one.
internal sealed class PostgreSqlQueryException(PostgresException error, string sql) : DbException(Describe(error, sql), error)
#pragma warning restore CA1032
{
    public override string? SqlState => error.SqlState;

    internal static string Describe(PostgresException error, string sql)
    {
        var text = new StringBuilder();
        text.Append(error.InvariantSeverity).Append(' ').Append(error.SqlState).Append(": ").Append(error.MessageText);
        if (error.Position > 0 && sql.Length > 0)
        {
            var offset = Math.Min(error.Position - 1, sql.Length);
            var line = sql.AsSpan(0, offset).Count('\n') + 1;
            var column = offset - sql.LastIndexOf('\n', Math.Max(0, offset - 1));
            text.Append(CultureInfo.InvariantCulture, $" (statement line {line}, column {Math.Max(1, column)})");
        }

        if (!string.IsNullOrEmpty(error.Detail))
        {
            text.Append("\nDetail: ").Append(error.Detail);
        }

        if (!string.IsNullOrEmpty(error.Hint))
        {
            text.Append("\nHint: ").Append(error.Hint);
        }

        if (!string.IsNullOrEmpty(error.Where))
        {
            text.Append("\nWhere: ").Append(error.Where);
        }

        return text.ToString();
    }
}
