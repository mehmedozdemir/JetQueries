using System.Collections.Concurrent;
using System.Data;
using System.Runtime.CompilerServices;
using JetQueries.Sdk;
using Microsoft.Data.SqlClient;

namespace JetQueries.Provider.SqlServer;

/// <summary>
/// Runs one batch and streams what happens. Rows are read forward-only and handed over in batches; server
/// messages (PRINT, low-severity RAISERROR) and "n rows affected" notifications are interleaved in the order
/// SqlClient reports them.
/// </summary>
internal sealed class SqlServerQueryExecutor(SqlConnection connection) : IQueryExecutor
{
    public async IAsyncEnumerable<QueryEvent> ExecuteAsync(QueryRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var batchSize = Math.Max(1, request.BatchSize);

        // SqlClient raises both events on the thread that is reading, between our awaits; the queue only
        // carries them to the next point where this iterator may yield.
        var pending = new ConcurrentQueue<QueryEvent>();
        void OnInfoMessage(object sender, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError error in e.Errors)
            {
                pending.Enqueue(new QueryMessage(
                    error.Message,
                    error.Class == 0 ? QueryMessageSeverity.Info : QueryMessageSeverity.Warning,
                    error.LineNumber > 0 ? error.LineNumber : null));
            }
        }

        void OnStatementCompleted(object sender, StatementCompletedEventArgs e) => pending.Enqueue(new StatementCompleted(e.RecordCount));

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Executing the user's SQL text is this method's purpose; values travel as parameters.
            command.CommandText = request.Sql;
#pragma warning restore CA2100
            command.CommandTimeout = request.Timeout is { } timeout ? Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)) : 0;
            foreach (var parameter in request.Parameters)
            {
                command.Parameters.AddWithValue("@" + parameter.Name, parameter.Value ?? DBNull.Value);
            }

            connection.InfoMessage += OnInfoMessage;
            command.StatementCompleted += OnStatementCompleted;
            var finished = false;
            try
            {
                var reader = await Translate(command.ExecuteReaderAsync(CommandBehavior.Default, cancellationToken), cancellationToken).ConfigureAwait(false);
                try
                {
                    do
                    {
                        while (pending.TryDequeue(out var queued))
                        {
                            yield return queued;
                        }

                        // DML and DDL produce no columns; their completion arrives through StatementCompleted.
                        if (reader.FieldCount == 0)
                        {
                            continue;
                        }

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
                            reader.GetValues(values!);
                            for (var i = 0; i < values.Length; i++)
                            {
                                if (values[i] is DBNull)
                                {
                                    values[i] = null;
                                }
                            }

                            rows.Add(values);
                            rowCount++;
                            if (rows.Count == batchSize)
                            {
                                yield return new RowBatch(rows);
                                rows = new List<object?[]>(batchSize);
                                while (pending.TryDequeue(out var queued))
                                {
                                    yield return queued;
                                }
                            }
                        }

                        if (rows.Count > 0)
                        {
                            yield return new RowBatch(rows);
                        }

                        yield return new ResultSetCompleted(rowCount, truncated);
                    }
                    while (await Translate(reader.NextResultAsync(cancellationToken), cancellationToken).ConfigureAwait(false));

                    finished = true;
                }
                finally
                {
                    // Abandoned half-way (cancel, error, or the consumer stopped enumerating): tell the server
                    // to stop, otherwise disposing the reader would first read every remaining row.
                    if (!finished)
                    {
                        command.Cancel();
                    }

                    await reader.DisposeAsync().ConfigureAwait(false);
                }

                // Messages and counts that only arrive when the reader closes.
                while (pending.TryDequeue(out var queued))
                {
                    yield return queued;
                }
            }
            finally
            {
                command.StatementCompleted -= OnStatementCompleted;
                connection.InfoMessage -= OnInfoMessage;
            }
        }
    }

    /// <summary>
    /// SqlClient reports a canceled statement as a <see cref="SqlException"/> ("Operation cancelled by user");
    /// the SDK contract promises <see cref="OperationCanceledException"/>.
    /// </summary>
    private static async Task<T> Translate<T>(Task<T> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The query was canceled.", ex, cancellationToken);
        }
    }
}
