using System.Data;
using System.Runtime.CompilerServices;
using JetQueries.Provider.Sqlite.Language;
using JetQueries.Sdk;
using Microsoft.Data.Sqlite;

namespace JetQueries.Provider.Sqlite;

/// <summary>
/// Runs one statement (the script splitter cuts scripts up) and streams its rows. SQLite runs inside this
/// process: cancel interrupts the statement through <c>sqlite3_interrupt</c>, which stops even a long sort or
/// a recursive query between steps.
/// </summary>
internal sealed class SqliteQueryExecutor(SqliteConnection connection) : IQueryExecutor
{
    private const int SqliteInterrupt = 9;

    public async IAsyncEnumerable<QueryEvent> ExecuteAsync(QueryRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var batchSize = Math.Max(1, request.BatchSize);

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

            var registration = cancellationToken.Register(static state => SQLitePCL.raw.sqlite3_interrupt(((SqliteConnection)state!).Handle), connection);
            await using (registration.ConfigureAwait(false))
            {
                var reader = await Translate(() => command.ExecuteReaderAsync(CommandBehavior.Default, cancellationToken), cancellationToken).ConfigureAwait(false);
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
                            columns[i] = new ResultColumn(reader.GetName(i), DeclaredType(reader, i), FieldType(reader, i));
                        }

                        yield return new ResultSetStarted(columns);

                        long rowCount = 0;
                        var truncated = false;
                        var rows = new List<object?[]>(batchSize);
                        while (await Translate(() => reader.ReadAsync(cancellationToken), cancellationToken).ConfigureAwait(false))
                        {
                            if (request.MaxRows is { } max && rowCount >= max)
                            {
                                truncated = true;
                                break;
                            }

                            var values = new object?[columns.Length];
                            for (var i = 0; i < values.Length; i++)
                            {
                                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
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
                    while (await Translate(() => reader.NextResultAsync(cancellationToken), cancellationToken).ConfigureAwait(false));

                    if (!any)
                    {
                        // SQLite's change counter is only meaningful after INSERT, UPDATE and DELETE; after DDL it
                        // still holds the count of an earlier statement.
                        yield return new StatementCompleted(IsDml(request.Sql) && reader.RecordsAffected >= 0 ? reader.RecordsAffected : null);
                    }
                }
            }
        }
    }

    internal static bool IsDml(string sql)
    {
        var text = sql;
        foreach (var token in SqliteSyntax.Tokenizer.Tokenize(text))
        {
            if (token.Kind == SqlTokenKind.Comment)
            {
                continue;
            }

            var word = text.AsSpan(token.Start, token.Length);
            return word.Equals("INSERT", StringComparison.OrdinalIgnoreCase) || word.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) ||
                   word.Equals("DELETE", StringComparison.OrdinalIgnoreCase) || word.Equals("REPLACE", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>The column's declared type; an expression has none, and SQLite then has only the value's storage class.</summary>
    private static string DeclaredType(SqliteDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetDataTypeName(ordinal);
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static Type FieldType(SqliteDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetFieldType(ordinal);
        }
        catch (InvalidOperationException)
        {
            return typeof(object);
        }
    }

    /// <summary>An interrupted statement fails with SQLITE_INTERRUPT; the SDK contract promises <see cref="OperationCanceledException"/>.</summary>
    private static async Task<T> Translate<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (SqliteException ex) when (cancellationToken.IsCancellationRequested || ex.SqliteErrorCode == SqliteInterrupt)
        {
            throw new OperationCanceledException("The query was canceled.", ex, cancellationToken);
        }
    }
}
