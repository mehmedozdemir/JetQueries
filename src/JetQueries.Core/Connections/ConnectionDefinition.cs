using JetQueries.Sdk;

namespace JetQueries.Core.Connections;

/// <summary>
/// A saved connection: everything about it except the password. Definitions are plain data that can be
/// exported or synchronized; secrets live in the <see cref="ICredentialStore"/>, keyed by <see cref="Id"/>.
/// </summary>
public sealed record ConnectionDefinition
{
    /// <summary>Stable identity; also the key of the password and of cached metadata.</summary>
    public required Guid Id { get; init; }

    /// <summary>Name shown in the explorer.</summary>
    public required string Name { get; init; }

    /// <summary>The <see cref="IDatabaseProvider.Id"/> of the provider that opens it.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Provider-specific, non-secret settings (server, database, …).</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

    /// <summary>Folder path in the explorer, parts separated by <c>/</c>; empty for the top level.</summary>
    public string Folder { get; init; } = string.Empty;

    /// <summary>What kind of environment this is; drives the colour marking.</summary>
    public ConnectionEnvironment Environment { get; init; }

    /// <summary>Whether the application should refuse statements that change data on this connection.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>User name, or <see langword="null"/> for integrated authentication. Not a secret.</summary>
    public string? UserName { get; init; }

    /// <summary>Whether the password is kept in the operating system's credential store or asked for each time.</summary>
    public bool SavePassword { get; init; }

    /// <summary>An SSH tunnel the connection goes through (a bastion host); null for a direct connection.</summary>
    public SshTunnelSettings? Ssh { get; init; }

    /// <summary>
    /// The databases (schemas on Oracle) the explorer and the database lists show; null or empty shows all. The
    /// current database of a query is shown even when it is not in the list.
    /// </summary>
    public IReadOnlyList<string>? VisibleDatabases { get; init; }

    /// <summary>Whether a database is shown (always, when no list is chosen).</summary>
    public bool ShowsDatabase(string name) =>
        VisibleDatabases is not { Count: > 0 } list || list.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The non-secret part handed to the provider.</summary>
    public ConnectionProfile ToProfile() => new(Id, Name, ProviderId, Settings);
}

/// <summary>The kind of environment a connection points to.</summary>
public enum ConnectionEnvironment
{
    /// <summary>Not marked.</summary>
    Unspecified,

    /// <summary>A developer's own or shared development database.</summary>
    Development,

    /// <summary>Test, QA or staging.</summary>
    Test,

    /// <summary>Production: destructive statements are confirmed first.</summary>
    Production,
}

/// <summary>Persists connection definitions. Never stores secrets.</summary>
public interface IConnectionStore
{
    /// <summary>All saved connections, ordered by folder and name.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<ConnectionDefinition>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Adds the connection, or replaces the one with the same <see cref="ConnectionDefinition.Id"/>.</summary>
    /// <param name="definition">The connection to save.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SaveAsync(ConnectionDefinition definition, CancellationToken cancellationToken);

    /// <summary>Removes a connection. Does nothing if it does not exist.</summary>
    /// <param name="id">The connection to remove.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Where connection definitions are kept.</summary>
public sealed class ConnectionStoreOptions
{
    /// <summary>Path of the JSON file; <see langword="null"/> keeps definitions in memory only.</summary>
    public string? FilePath { get; set; }
}

/// <summary>
/// Keeps connection passwords in the operating system's protected store (Windows Credential Manager; macOS
/// Keychain and libsecret later). Passwords are never written to JetQueries' own files or logs.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Whether passwords survive a restart. <see langword="false"/> on platforms without a supported store:
    /// passwords are then remembered for the current session only.
    /// </summary>
    bool IsPersistent { get; }

    /// <summary>Reads the password of a connection.</summary>
    /// <param name="connectionId">The connection's identity.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The password, or <see langword="null"/> if none is stored.</returns>
    Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Stores or replaces the password of a connection.</summary>
    /// <param name="connectionId">The connection's identity.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SetPasswordAsync(Guid connectionId, string password, CancellationToken cancellationToken);

    /// <summary>Removes the stored password of a connection, if any.</summary>
    /// <param name="connectionId">The connection's identity.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task DeletePasswordAsync(Guid connectionId, CancellationToken cancellationToken);
}
