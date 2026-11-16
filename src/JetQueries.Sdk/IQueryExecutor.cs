namespace JetQueries.Sdk;

/// <summary>
/// Executes SQL and streams back everything that happens — result sets, rows, server messages — as one ordered
/// sequence of <see cref="QueryEvent"/>s.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0.</remarks>
public interface IQueryExecutor
{
    /// <summary>
    /// Executes <paramref name="request"/>. Nothing runs until the sequence is enumerated. Rows are streamed in
    /// batches as the driver delivers them; the implementation never buffers a whole result set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Event order per result set: one <see cref="ResultSetStarted"/>, zero or more <see cref="RowBatch"/>, one
    /// <see cref="ResultSetCompleted"/>. <see cref="QueryMessage"/> and <see cref="StatementCompleted"/> events
    /// may appear anywhere in between.
    /// </para>
    /// <para>
    /// Canceling <paramref name="cancellationToken"/> or disposing the enumerator early cancels the statement on
    /// the server where supported and ends the sequence with <see cref="OperationCanceledException"/>. Database
    /// errors surface as exceptions from the enumeration.
    /// </para>
    /// </remarks>
    /// <param name="request">The SQL to run and how to run it.</param>
    /// <param name="cancellationToken">Cancels the execution.</param>
    /// <returns>The ordered stream of execution events.</returns>
    IAsyncEnumerable<QueryEvent> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken);
}

/// <summary>A request to execute SQL.</summary>
/// <param name="Sql">The SQL text, exactly as it should be sent (client-side batch splitting has already happened).</param>
public sealed record QueryRequest(string Sql)
{
    /// <summary>Parameter values. Values are always sent as parameters, never spliced into the SQL text.</summary>
    public IReadOnlyList<QueryParameter> Parameters { get; init; } = [];

    /// <summary>Maximum number of rows to read per result set, or <see langword="null"/> for no limit.</summary>
    public int? MaxRows { get; init; }

    /// <summary>Preferred number of rows per <see cref="RowBatch"/>. Implementations may deliver smaller batches.</summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>Server-side command timeout, or <see langword="null"/> for none (the user cancels instead).</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>A named parameter value.</summary>
/// <param name="Name">Parameter name without the dialect's prefix (<c>id</c>, not <c>@id</c> or <c>:id</c>).</param>
/// <param name="Value">The value; <see langword="null"/> means SQL <c>NULL</c>.</param>
public sealed record QueryParameter(string Name, object? Value);

/// <summary>Base type of the events produced by <see cref="IQueryExecutor.ExecuteAsync"/>.</summary>
public abstract record QueryEvent
{
    private protected QueryEvent()
    {
    }
}

/// <summary>A new result set begins.</summary>
/// <param name="Columns">The columns of the result set, in order.</param>
public sealed record ResultSetStarted(IReadOnlyList<ResultColumn> Columns) : QueryEvent;

/// <summary>A batch of rows belonging to the current result set.</summary>
/// <param name="Rows">
/// The rows. Each row has one value per column of the current result set; SQL <c>NULL</c> is
/// <see langword="null"/> (not <see cref="DBNull"/>). The receiver owns the arrays.
/// </param>
public sealed record RowBatch(IReadOnlyList<object?[]> Rows) : QueryEvent;

/// <summary>The current result set has ended.</summary>
/// <param name="RowCount">Number of rows delivered for this result set.</param>
/// <param name="Truncated">Whether reading stopped early because <see cref="QueryRequest.MaxRows"/> was reached.</param>
public sealed record ResultSetCompleted(long RowCount, bool Truncated) : QueryEvent;

/// <summary>A statement finished without returning rows (for example DML or DDL).</summary>
/// <param name="RowsAffected">Number of affected rows, or <see langword="null"/> if the database did not report one.</param>
public sealed record StatementCompleted(long? RowsAffected) : QueryEvent;

/// <summary>
/// An informational message or warning from the server (<c>PRINT</c>, <c>RAISERROR</c> below error severity,
/// <c>DBMS_OUTPUT</c>, <c>RAISE NOTICE</c>).
/// </summary>
/// <param name="Text">The message text.</param>
/// <param name="Severity">How the message should be presented.</param>
/// <param name="Line">One-based line in <see cref="QueryRequest.Sql"/> the message refers to, if known.</param>
public sealed record QueryMessage(string Text, QueryMessageSeverity Severity, int? Line = null) : QueryEvent;

/// <summary>Severity of a <see cref="QueryMessage"/>.</summary>
public enum QueryMessageSeverity
{
    /// <summary>Informational output.</summary>
    Info,

    /// <summary>A warning that did not stop execution.</summary>
    Warning,

    /// <summary>An error that did not stop execution (later statements still ran).</summary>
    Error,
}

/// <summary>A column of a result set.</summary>
/// <param name="Name">Column name; may be empty for unnamed expressions.</param>
/// <param name="DataType">Data type name as reported by the database (for example <c>nvarchar</c>).</param>
/// <param name="ClrType">The .NET type of the values in this column.</param>
public sealed record ResultColumn(string Name, string DataType, Type ClrType);
