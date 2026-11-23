namespace JetQueries.Sdk;

/// <summary>
/// A connection found in another tool (DBeaver, SSMS…), described without any tool's or provider's own setting
/// names. Passwords are never part of it.
/// </summary>
public sealed record ImportedEndpoint
{
    /// <summary>Server host name or address (SQL Server: may hold an instance, <c>host\instance</c>).</summary>
    public string? Host { get; init; }

    /// <summary>TCP port; null for the default.</summary>
    public int? Port { get; init; }

    /// <summary>Database (SQL Server, PostgreSQL).</summary>
    public string? Database { get; init; }

    /// <summary>Oracle service name.</summary>
    public string? ServiceName { get; init; }

    /// <summary>Oracle SID.</summary>
    public string? Sid { get; init; }

    /// <summary>A database file (SQLite).</summary>
    public string? File { get; init; }

    /// <summary>Windows (integrated) authentication instead of a user name and password.</summary>
    public bool? IntegratedSecurity { get; init; }

    /// <summary>Encrypt the connection.</summary>
    public bool? Encrypt { get; init; }

    /// <summary>Trust the server's certificate without validation.</summary>
    public bool? TrustServerCertificate { get; init; }
}

/// <summary>
/// Optional feature of an <see cref="IDatabaseProvider"/>: the provider's own connection settings for a
/// connection imported from another tool.
/// </summary>
/// <remarks>Preview: part of SDK contract version 0.</remarks>
public interface IConnectionImport
{
    /// <summary>The settings (as <see cref="IConnectionSettingsProvider"/> defines them) for <paramref name="endpoint"/>.</summary>
    /// <param name="endpoint">What the other tool knew.</param>
    IReadOnlyDictionary<string, string> ToSettings(ImportedEndpoint endpoint);
}
