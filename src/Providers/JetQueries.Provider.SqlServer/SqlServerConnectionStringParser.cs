using System.Globalization;
using JetQueries.Providers.Connections;
using JetQueries.Sdk;

namespace JetQueries.Provider.SqlServer;

/// <summary>
/// Reads a SqlClient connection string (<c>Server=…;Database=…</c>) or a JDBC URL
/// (<c>jdbc:sqlserver://host:port;databaseName=…</c>) into the provider's settings.
/// </summary>
internal static class SqlServerConnectionStringParser
{
    // Keywords of SqlClient that only tune the driver; the provider sets its own values for them.
    private static readonly HashSet<string> Tuning = new(StringComparer.Ordinal)
    {
        "APPLICATIONNAME", "APP", "PERSISTSECURITYINFO", "PERSISTSECURITY", "POOLING", "MULTIPLEACTIVERESULTSETS", "MARS",
        "WORKSTATIONID", "WSID", "MAXPOOLSIZE", "MINPOOLSIZE", "PACKETSIZE", "CONNECTRETRYCOUNT", "CONNECTRETRYINTERVAL",
        "MULTISUBNETFAILOVER", "CURRENTLANGUAGE", "LANGUAGE", "LOADBALANCETIMEOUT", "CONNECTIONLIFETIME", "ENLIST",
        "COMMANDTIMEOUT", "TRANSACTIONBINDING", "TYPESYSTEMVERSION", "POOLBLOCKINGPERIOD", "IPADDRESSPREFERENCE",
    };

    public static ParsedConnectionString Parse(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        var text = connectionString.Trim();
        if (text.Length == 0)
        {
            throw new FormatException("The connection string is empty.");
        }

        return ConnectionStringText.TryStrip(text, out var jdbc, "jdbc:sqlserver://") ? ParseJdbc(jdbc) : ParsePairs(text);
    }

    private static ParsedConnectionString ParsePairs(string text)
    {
        if (!text.Contains('=', StringComparison.Ordinal))
        {
            // Just a server name.
            return new ParsedConnectionString
            {
                Settings = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SqlServerConnectionSettings.Server] = text,
                    [SqlServerConnectionSettings.Authentication] = "sql",
                },
            };
        }

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var ignored = new List<string>();
        string? user = null;
        string? password = null;
        var windows = false;
        foreach (var (key, value) in ConnectionStringText.ReadPairs(text))
        {
            switch (ConnectionStringText.Normalize(key))
            {
                case "DATASOURCE" or "SERVER" or "ADDRESS" or "ADDR" or "NETWORKADDRESS":
                    settings[SqlServerConnectionSettings.Server] = value.Trim();
                    break;
                case "INITIALCATALOG" or "DATABASE":
                    settings[SqlServerConnectionSettings.Database] = value.Trim();
                    break;
                case "INTEGRATEDSECURITY" or "TRUSTEDCONNECTION":
                    windows = ConnectionStringText.Flag(value) ?? false;
                    break;
                case "USERID" or "UID" or "USER" or "USERNAME":
                    user = value;
                    break;
                case "PASSWORD" or "PWD":
                    password = value;
                    break;
                case "ENCRYPT":
                    Take(SqlServerConnectionSettings.Encrypt, EncryptText(value), key);
                    break;
                case "TRUSTSERVERCERTIFICATE":
                    Take(SqlServerConnectionSettings.TrustServerCertificate, FlagText(value), key);
                    break;
                case "APPLICATIONINTENT":
                    settings[SqlServerConnectionSettings.ReadOnlyIntent] = ReadOnlyText(value);
                    break;
                case "CONNECTTIMEOUT" or "CONNECTIONTIMEOUT" or "TIMEOUT":
                    Take(SqlServerConnectionSettings.ConnectTimeoutSeconds, ConnectionStringText.Seconds(value), key);
                    break;
                case "AUTHENTICATION":
                    var method = ConnectionStringText.Normalize(value);
                    if (method is "ACTIVEDIRECTORYINTEGRATED")
                    {
                        windows = true;
                    }
                    else if (method is not ("SQLPASSWORD" or "NOTSPECIFIED" or ""))
                    {
                        ignored.Add(key);
                    }

                    break;
                case var other when Tuning.Contains(other):
                    break;
                default:
                    ignored.Add(key);
                    break;
            }
        }

        if (!settings.ContainsKey(SqlServerConnectionSettings.Server))
        {
            throw new FormatException("The connection string has no server (Server or Data Source).");
        }

        settings[SqlServerConnectionSettings.Authentication] = windows ? "windows" : "sql";
        return new ParsedConnectionString
        {
            Settings = settings,
            UserName = windows ? null : user,
            Password = windows ? null : password,
            Ignored = ignored,
        };

        void Take(string setting, string? value, string key)
        {
            if (value is null)
            {
                ignored.Add(key);
            }
            else
            {
                settings[setting] = value;
            }
        }
    }

    // jdbc:sqlserver://[serverName[\instanceName][:portNumber]][;property=value[;property=value]]
    private static ParsedConnectionString ParseJdbc(string text)
    {
        var parts = text.Split(';');
        var server = parts[0].Trim();
        string? instance = null;
        int? port = null;
        if (server.Length > 0)
        {
            (server, port) = ConnectionStringText.SplitHostPort(server);
            var backslash = server.IndexOf('\\', StringComparison.Ordinal);
            if (backslash >= 0)
            {
                instance = server[(backslash + 1)..];
                server = server[..backslash];
            }
        }

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var ignored = new List<string>();
        string? user = null;
        string? password = null;
        var windows = false;
        foreach (var part in parts.Skip(1))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var key = part[..equals].Trim();
            var value = part[(equals + 1)..].Trim().Trim('{', '}');
            string? taken = null;
            switch (ConnectionStringText.Normalize(key))
            {
                case "SERVERNAME":
                    server = value;
                    break;
                case "INSTANCENAME":
                    instance = value;
                    break;
                case "PORTNUMBER" or "PORT":
                    port = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 65535
                        ? number
                        : throw new FormatException("The port must be a whole number from 1 to 65535.");
                    break;
                case "DATABASENAME" or "DATABASE":
                    settings[SqlServerConnectionSettings.Database] = value;
                    break;
                case "USER" or "USERNAME":
                    user = value;
                    break;
                case "PASSWORD":
                    password = value;
                    break;
                case "INTEGRATEDSECURITY":
                    windows = ConnectionStringText.Flag(value) ?? false;
                    break;
                case "ENCRYPT":
                    taken = Put(SqlServerConnectionSettings.Encrypt, EncryptText(value));
                    break;
                case "TRUSTSERVERCERTIFICATE":
                    taken = Put(SqlServerConnectionSettings.TrustServerCertificate, FlagText(value));
                    break;
                case "APPLICATIONINTENT":
                    settings[SqlServerConnectionSettings.ReadOnlyIntent] = ReadOnlyText(value);
                    break;
                case "LOGINTIMEOUT":
                    taken = Put(SqlServerConnectionSettings.ConnectTimeoutSeconds, ConnectionStringText.Seconds(value));
                    break;
                case "APPLICATIONNAME" or "WORKSTATIONID" or "SENDSTRINGPARAMETERSASUNICODE" or "MULTISUBNETFAILOVER":
                    break;
                default:
                    ignored.Add(key);
                    break;
            }

            if (taken == string.Empty)
            {
                ignored.Add(key);
            }
        }

        if (server.Length == 0)
        {
            throw new FormatException("The JDBC URL has no server.");
        }

        if (instance is { Length: > 0 })
        {
            server += "\\" + instance;
        }

        // A named instance with a port, or a port other than the default, is written host[\instance],port.
        if (port is { } p && (p != 1433 || instance is { Length: > 0 }))
        {
            server += "," + p.ToString(CultureInfo.InvariantCulture);
        }

        settings[SqlServerConnectionSettings.Server] = server;
        settings[SqlServerConnectionSettings.Authentication] = windows ? "windows" : "sql";
        return new ParsedConnectionString
        {
            Settings = settings,
            UserName = windows ? null : user,
            Password = windows ? null : password,
            Ignored = ignored,
        };

        // Empty string: the value was not understood.
        string Put(string setting, string? value)
        {
            if (value is null)
            {
                return string.Empty;
            }

            settings[setting] = value;
            return value;
        }
    }

    private static string? EncryptText(string value) => value.Trim().ToUpperInvariant() switch
    {
        "TRUE" or "YES" or "MANDATORY" => "true",
        "FALSE" or "NO" or "OPTIONAL" => "false",
        "STRICT" => "strict",
        _ => null,
    };

    private static string ReadOnlyText(string value) =>
        value.Trim().Equals("ReadOnly", StringComparison.OrdinalIgnoreCase) ? "true" : "false";

    private static string? FlagText(string value) => ConnectionStringText.Flag(value) switch
    {
        true => "true",
        false => "false",
        null => null,
    };
}
