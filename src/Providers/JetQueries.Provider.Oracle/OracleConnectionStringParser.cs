using System.Globalization;
using System.Text.RegularExpressions;
using JetQueries.Providers.Connections;
using JetQueries.Sdk;

namespace JetQueries.Provider.Oracle;

/// <summary>
/// Reads an ODP.NET connection string (<c>User Id=…;Data Source=host:port/service</c>), a JDBC thin URL
/// (<c>jdbc:oracle:thin:@//host:port/service</c>, <c>@host:port:SID</c>), an EZConnect address, a connect descriptor
/// (<c>(DESCRIPTION=…)</c>) or SQL*Plus style <c>user/password@host:port/service</c>.
/// </summary>
internal static partial class OracleConnectionStringParser
{
    private static readonly HashSet<string> Tuning = new(StringComparer.Ordinal)
    {
        "POOLING", "MINPOOLSIZE", "MAXPOOLSIZE", "INCRPOOLSIZE", "DECRPOOLSIZE", "CONNECTIONLIFETIME", "STATEMENTCACHESIZE",
        "PERSISTSECURITYINFO", "ENLIST", "SELFTUNING", "VALIDATECONNECTION", "METADATAPOOLING", "STATEMENTCACHEPURGE",
        "LOADBALANCING", "HAEVENTS",
    };

    public static ParsedConnectionString Parse(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        var text = connectionString.Trim();
        if (text.Length == 0)
        {
            throw new FormatException("The connection string is empty.");
        }

        if (ConnectionStringText.TryStrip(text, out var jdbc, "jdbc:oracle:thin:"))
        {
            return ParseUserAt(jdbc);
        }

        // A descriptor is all '=' and parentheses; key=value pairs never start with '('.
        return text.StartsWith('(') || !text.Contains('=', StringComparison.Ordinal) ? ParseUserAt(text) : ParsePairs(text);
    }

    // [user[/password]]@address, or just an address.
    private static ParsedConnectionString ParseUserAt(string text)
    {
        string? user = null;
        string? password = null;
        var address = text;
        var at = text.StartsWith('(') ? -1 : text.LastIndexOf('@');
        if (at >= 0)
        {
            var login = text[..at];
            address = text[(at + 1)..];
            var slash = login.IndexOf('/', StringComparison.Ordinal);
            user = slash < 0 ? login : login[..slash];
            password = slash < 0 ? null : login[(slash + 1)..];
        }

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        ReadAddress(address.Trim(), settings);
        return new ParsedConnectionString
        {
            Settings = settings,
            UserName = string.IsNullOrEmpty(user) ? null : user,
            Password = string.IsNullOrEmpty(password) ? null : password,
        };
    }

    private static ParsedConnectionString ParsePairs(string text)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var ignored = new List<string>();
        string? user = null;
        string? password = null;
        var hasAddress = false;
        var slashUser = false;

        // The TNS and wallet folders apply to a TNS alias only; with host:port/service they are not used.
        var folders = new List<(string Key, string Value)>();
        foreach (var (key, value) in ConnectionStringText.ReadPairs(text))
        {
            switch (ConnectionStringText.Normalize(key))
            {
                case "DATASOURCE" or "SERVER":
                    ReadAddress(value.Trim(), settings);
                    hasAddress = true;
                    break;
                case "USERID" or "UID" or "USER" or "USERNAME":
                    // "/" is operating-system authentication, which the provider does not offer.
                    if (value.Trim() == "/")
                    {
                        slashUser = true;
                    }
                    else
                    {
                        user = value;
                    }

                    break;
                case "PASSWORD" or "PWD":
                    password = value;
                    break;
                case "DBAPRIVILEGE":
                    var role = value.Trim().ToUpperInvariant();
                    if (role is "SYSDBA" or "SYSOPER")
                    {
                        settings[OracleConnectionSettings.Role] = role.ToLowerInvariant();
                    }
                    else
                    {
                        ignored.Add(key);
                    }

                    break;
                case "CONNECTIONTIMEOUT" or "CONNECTTIMEOUT":
                    if (ConnectionStringText.Seconds(value) is { } seconds)
                    {
                        settings[OracleConnectionSettings.ConnectTimeoutSeconds] = seconds;
                    }
                    else
                    {
                        ignored.Add(key);
                    }

                    break;
                case "TNSADMIN" or "WALLETLOCATION" when value.Trim().Length > 0:
                    folders.Add((key, value.Trim()));
                    break;
                case var other when Tuning.Contains(other):
                    break;
                default:
                    ignored.Add(key);
                    break;
            }
        }

        if (!hasAddress)
        {
            throw new FormatException("The connection string has no Data Source.");
        }

        // "User Id=/" with a TNS alias: the wallet's secure external password store; otherwise operating-system
        // authentication, which the provider does not offer.
        if (slashUser && OracleConnectionSettings.IsTns(settings))
        {
            settings[OracleConnectionSettings.WalletCredentials] = "true";
        }
        else if (slashUser)
        {
            ignored.Add("User Id");
        }

        foreach (var (key, value) in folders)
        {
            if (OracleConnectionSettings.IsTns(settings))
            {
                settings[ConnectionStringText.Normalize(key) == "TNSADMIN" ? OracleConnectionSettings.TnsAdmin : OracleConnectionSettings.WalletLocation] = value;
            }
            else
            {
                ignored.Add(key);
            }
        }

        return new ParsedConnectionString { Settings = settings, UserName = user, Password = password, Ignored = ignored };
    }

    // EZConnect ([//]host[:port][/service][:server]), the JDBC form host:port:SID, or a connect descriptor.
    private static void ReadAddress(string address, Dictionary<string, string> settings)
    {
        string host;
        string? port;
        string? service = null;
        string? sid = null;
        if (address.StartsWith('('))
        {
            host = Descriptor(address, "HOST") ?? throw new FormatException("The connect descriptor has no HOST.");
            port = Descriptor(address, "PORT");
            service = Descriptor(address, "SERVICE_NAME");
            sid = service is null ? Descriptor(address, "SID") : null;
        }
        else
        {
            var match = EasyConnect().Match(address);
            if (!match.Success)
            {
                throw new FormatException("The address is not host:port/service, host:port:SID or a connect descriptor.");
            }

            host = match.Groups["host"].Value.Trim('[', ']');
            port = match.Groups["port"].Success ? match.Groups["port"].Value : null;
            service = match.Groups["service"].Success ? match.Groups["service"].Value : null;

            // host:port:SID (JDBC); after a service name the same place holds the server type (dedicated…).
            sid = service is null && match.Groups["tail"].Success ? match.Groups["tail"].Value : null;
            // A bare name is a TNS alias: the driver finds it in tnsnames.ora.
            if (port is null && service is null && sid is null && !address.StartsWith("//", StringComparison.Ordinal))
            {
                settings[OracleConnectionSettings.ConnectionType] = OracleConnectionSettings.Tns;
                settings[OracleConnectionSettings.TnsAlias] = address;
                return;
            }
        }

        settings[OracleConnectionSettings.Host] = host;
        if (port is not null)
        {
            settings[OracleConnectionSettings.Port] = int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 65535
                ? number.ToString(CultureInfo.InvariantCulture)
                : throw new FormatException("The port must be a whole number from 1 to 65535.");
        }

        if (service is not null)
        {
            settings[OracleConnectionSettings.ServiceName] = service;
        }
        else if (sid is not null)
        {
            settings[OracleConnectionSettings.Sid] = sid;
        }
    }

    private static string? Descriptor(string descriptor, string name)
    {
        var match = Regex.Match(descriptor, @"\(\s*" + name + @"\s*=\s*([^)\s]+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^(//)?(?<host>\[[^\]]+\]|[^:/\s]+)(:(?<port>\d+))?(/(?<service>[^:/\s]+))?(:(?<tail>[^:/\s]+))?/?$", RegexOptions.CultureInvariant)]
    private static partial Regex EasyConnect();
}
