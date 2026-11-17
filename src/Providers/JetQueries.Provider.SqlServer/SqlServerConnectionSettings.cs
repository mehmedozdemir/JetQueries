using System.Globalization;
using JetQueries.Sdk;
using Microsoft.Data.SqlClient;

namespace JetQueries.Provider.SqlServer;

/// <summary>
/// Turns a connection profile into a SqlClient connection string. Setting names (compared ordinally,
/// values case-insensitively):
/// <list type="bullet">
/// <item><c>server</c> — required; <c>host</c>, <c>host,port</c> or <c>host\instance</c>.</item>
/// <item><c>database</c> — initial database; the login's default when missing.</item>
/// <item><c>authentication</c> — <c>sql</c> (default; user name and password from the credential) or <c>windows</c>.</item>
/// <item><c>encrypt</c> — <c>true</c> (default), <c>false</c> or <c>strict</c>.</item>
/// <item><c>trustServerCertificate</c> — <c>true</c> to accept a self-signed certificate; default <c>false</c>.</item>
/// <item><c>readOnlyIntent</c> — <c>true</c> to connect to a readable secondary.</item>
/// <item><c>connectTimeoutSeconds</c> — default 15.</item>
/// </list>
/// </summary>
internal static class SqlServerConnectionSettings
{
    public const string Server = "server";
    public const string Database = "database";
    public const string Authentication = "authentication";
    public const string Encrypt = "encrypt";
    public const string TrustServerCertificate = "trustServerCertificate";
    public const string ReadOnlyIntent = "readOnlyIntent";
    public const string ConnectTimeoutSeconds = "connectTimeoutSeconds";

    /// <summary>Builds the connection string. The result contains the password and must never be logged or stored.</summary>
    /// <exception cref="ArgumentException">A setting is missing or has a value that is not understood.</exception>
    public static string BuildConnectionString(ConnectionProfile profile, ConnectionCredential? credential)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var settings = profile.Settings;

        if (!settings.TryGetValue(Server, out var server) || string.IsNullOrWhiteSpace(server))
        {
            throw new ArgumentException("The connection has no server.", nameof(profile));
        }

        // Every value goes through the builder, which escapes it: nothing a user types can add a keyword.
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server.Trim(),
            ApplicationName = "JetQueries",
            PersistSecurityInfo = false,
            MultipleActiveResultSets = false,
            ConnectTimeout = 15,
        };

        if (settings.TryGetValue(Database, out var database) && !string.IsNullOrWhiteSpace(database))
        {
            builder.InitialCatalog = database.Trim();
        }

        var authentication = settings.GetValueOrDefault(Authentication, "sql");
        if (Is(authentication, "windows"))
        {
            builder.IntegratedSecurity = true;
        }
        else if (Is(authentication, "sql"))
        {
            if (string.IsNullOrEmpty(credential?.UserName))
            {
                throw new ArgumentException("SQL Server authentication needs a user name.", nameof(credential));
            }

            builder.UserID = credential.UserName;
            builder.Password = credential.Password ?? string.Empty;
        }
        else
        {
            throw new ArgumentException($"Unknown authentication '{authentication}'. Use 'sql' or 'windows'.", nameof(profile));
        }

        var encrypt = settings.GetValueOrDefault(Encrypt, "true");
        builder.Encrypt = encrypt.Trim().ToUpperInvariant() switch
        {
            "TRUE" => SqlConnectionEncryptOption.Mandatory,
            "FALSE" => SqlConnectionEncryptOption.Optional,
            "STRICT" => SqlConnectionEncryptOption.Strict,
            _ => throw new ArgumentException($"Unknown encrypt value '{encrypt}'. Use 'true', 'false' or 'strict'.", nameof(profile)),
        };

        builder.TrustServerCertificate = Flag(settings, TrustServerCertificate);
        if (Flag(settings, ReadOnlyIntent))
        {
            builder.ApplicationIntent = ApplicationIntent.ReadOnly;
        }

        if (settings.TryGetValue(ConnectTimeoutSeconds, out var timeout))
        {
            if (!int.TryParse(timeout, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 600)
            {
                throw new ArgumentException($"'{ConnectTimeoutSeconds}' must be a whole number from 1 to 600.", nameof(profile));
            }

            builder.ConnectTimeout = seconds;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// The TCP endpoint of a server setting, or null when it cannot be known without asking the network: a
    /// named instance listens on a port only the SQL Browser service knows, and shared memory or named pipes
    /// have no TCP port at all.
    /// </summary>
    public static NetworkEndpoint? ParseEndpoint(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return null;
        }

        var value = server.Trim();
        if (value.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
        }
        else if (value.Contains(':', StringComparison.Ordinal) && !value.StartsWith('['))
        {
            // lpc:, np:, admin: or a bare IPv6 address: leave it to the driver.
            return null;
        }

        var port = 1433;
        var comma = value.LastIndexOf(',');
        if (comma >= 0)
        {
            if (!int.TryParse(value.AsSpan(comma + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
            {
                return null;
            }

            value = value[..comma];
        }
        else if (value.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        var backslash = value.IndexOf('\\', StringComparison.Ordinal);
        var host = (backslash >= 0 ? value[..backslash] : value).Trim().Trim('[', ']');
        if (host is "." or "(local)" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            host = "localhost";
        }

        return host.Length == 0 ? null : new NetworkEndpoint(host, port);
    }

    private static bool Is(string value, string expected) => string.Equals(value.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool Flag(IReadOnlyDictionary<string, string> settings, string name)
    {
        if (!settings.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return bool.TryParse(value.Trim(), out var flag)
            ? flag
            : throw new ArgumentException($"'{name}' must be 'true' or 'false'.", nameof(settings));
    }
}
