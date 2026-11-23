namespace JetQueries.Sdk;

/// <summary>
/// Optional feature of an <see cref="IDatabaseConnection"/>: an explicit transaction that the user ends with
/// Commit or Rollback (a query window's manual-commit mode). Without one, each statement commits on its own, as
/// every driver does by default. The host serializes calls with the connection's queries.
/// </summary>
/// <remarks>Preview: part of SDK contract version 0.</remarks>
public interface ITransactionControl
{
    /// <summary>Starts a transaction unless one is already open; what runs next stays uncommitted until Commit.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task BeginAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether a transaction is open on the connection, as the server sees it now: one started by
    /// <see cref="BeginAsync"/>, or by the user's own SQL (<c>BEGIN TRAN</c>).
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<bool> HasOpenTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Commits the open transaction, if any.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>Rolls the open transaction back, if any.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task RollbackAsync(CancellationToken cancellationToken);
}
