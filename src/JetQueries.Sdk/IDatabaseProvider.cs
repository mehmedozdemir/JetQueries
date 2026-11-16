namespace JetQueries.Sdk;

/// <summary>
/// Entry point of a database provider extension: describes the database family and opens connections to it.
/// Implementations must be thread-safe and must never block; every I/O member is asynchronous and cancelable.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0.</remarks>
public interface IDatabaseProvider
{
    /// <summary>
    /// Stable, lower-case identifier of the provider (for example <c>sqlserver</c> or <c>oracle</c>).
    /// Stored in connection profiles; compared with <see cref="StringComparison.Ordinal"/>.
    /// </summary>
    string Id { get; }

    /// <summary>Name shown to the user (for example <c>SQL Server</c>).</summary>
    string DisplayName { get; }

    /// <summary>Optional features this provider supports. The host hides UI for missing capabilities.</summary>
    ProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Language service for this provider's SQL dialect, or <see langword="null"/> if the provider has none
    /// (the editor then falls back to plain text).
    /// </summary>
    ISqlLanguageService? LanguageService { get; }

    /// <summary>
    /// Quotes a single identifier part for safe use in SQL text (for example <c>[my table]</c> or
    /// <c>"MY TABLE"</c>), escaping embedded quote characters. Callers must use this instead of concatenating
    /// raw identifiers.
    /// </summary>
    /// <param name="identifier">One unquoted identifier part (not a multi-part name).</param>
    /// <returns>The quoted identifier.</returns>
    string QuoteIdentifier(string identifier);

    /// <summary>
    /// Opens a connection. The host opens separate connections for query execution and for metadata, so a long
    /// query never blocks IntelliSense.
    /// </summary>
    /// <param name="profile">Non-secret connection settings.</param>
    /// <param name="credential">
    /// Secrets resolved by the host from the OS credential store, or <see langword="null"/> for integrated
    /// authentication. Providers must not persist or log them.
    /// </param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>An open connection, owned by the caller.</returns>
    Task<IDatabaseConnection> OpenConnectionAsync(
        ConnectionProfile profile,
        ConnectionCredential? credential,
        CancellationToken cancellationToken);
}

/// <summary>An open connection to a database. Dispose it to close the connection.</summary>
/// <remarks>
/// A connection serves one operation at a time: callers must not start a second query or metadata call while a
/// previous one on the same connection is still being enumerated.
/// </remarks>
public interface IDatabaseConnection : IAsyncDisposable
{
    /// <summary>Executes SQL on this connection.</summary>
    IQueryExecutor Executor { get; }

    /// <summary>Reads schema metadata through this connection.</summary>
    IMetadataProvider Metadata { get; }
}

/// <summary>Optional features of a database provider.</summary>
[Flags]
public enum ProviderCapabilities
{
    /// <summary>No optional features.</summary>
    None = 0,

    /// <summary>One server hosts several databases the user can switch between.</summary>
    MultipleDatabases = 1 << 0,

    /// <summary>Objects live in schemas (or owners).</summary>
    Schemas = 1 << 1,

    /// <summary>One execution can return several result sets.</summary>
    MultipleResultSets = 1 << 2,

    /// <summary>Explicit transactions (manual commit/rollback) are supported.</summary>
    Transactions = 1 << 3,

    /// <summary>A running statement can be canceled on the server, not just abandoned by the client.</summary>
    ServerSideCancel = 1 << 4,

    /// <summary>Stored procedures and functions exist.</summary>
    Routines = 1 << 5,

    /// <summary>Packages (Oracle-style groups of routines) exist.</summary>
    Packages = 1 << 6,

    /// <summary>Sequences exist.</summary>
    Sequences = 1 << 7,
}

/// <summary>
/// The shareable, non-secret part of a connection definition. Safe to store, export and sync: it must never
/// contain passwords, tokens or full connection strings with credentials.
/// </summary>
/// <param name="Id">Stable identifier of the profile; also the key of its secret in the OS credential store.</param>
/// <param name="Name">Display name chosen by the user.</param>
/// <param name="ProviderId">The <see cref="IDatabaseProvider.Id"/> this profile belongs to.</param>
/// <param name="Settings">
/// Provider-defined, non-secret settings (host, port, database, authentication mode, …). Keys are compared
/// ordinally and ignore case.
/// </param>
public sealed record ConnectionProfile(
    Guid Id,
    string Name,
    string ProviderId,
    IReadOnlyDictionary<string, string> Settings);

/// <summary>
/// Secrets for opening a connection. Lives in memory only; the host reads it from the OS credential store just
/// before connecting.
/// </summary>
/// <param name="userName">User name, or <see langword="null"/> if not applicable.</param>
/// <param name="password">Password or token, or <see langword="null"/> if not applicable.</param>
public sealed class ConnectionCredential(string? userName, string? password)
{
    /// <summary>User name, or <see langword="null"/> if not applicable.</summary>
    public string? UserName { get; } = userName;

    /// <summary>Password or token, or <see langword="null"/> if not applicable. Never log this value.</summary>
    public string? Password { get; } = password;

    /// <summary>Returns a description that never includes the password.</summary>
    /// <returns>A log-safe description.</returns>
    public override string ToString() => $"{nameof(ConnectionCredential)} {{ UserName = {UserName}, Password = *** }}";
}
