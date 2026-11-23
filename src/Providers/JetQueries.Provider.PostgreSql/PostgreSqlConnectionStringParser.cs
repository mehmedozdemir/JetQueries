using System.Globalization;
using System.Text;
using JetQueries.Providers.Connections;
using JetQueries.Sdk;

namespace JetQueries.Provider.PostgreSql;

/// <summary>
/// Reads an Npgsql connection string (<c>Host=…;Database=…</c>), a libpq keyword string
/// (<c>host=… dbname=…</c>) or a URL (<c>postgresql://user@host:5432/db?sslmode=require</c>, also with
/// <c>jdbc:</c> in front).
/// </summary>
internal static partial class PostgreSqlConnectionStringParser
{
    private static readonly HashSet<string> Tuning = new(StringComparer.Ordinal)
    {
        "POOLING", "MINIMUMPOOLSIZE", "MINPOOLSIZE", "MAXIMUMPOOLSIZE", "MAXPOOLSIZE", "APPLICATIONNAME", "COMMANDTIMEOUT",
        "INCLUDEERRORDETAIL", "INCLUDEERRORDETAILS", "KEEPALIVE", "TCPKEEPALIVE", "PERSISTSECURITYINFO", "CONNECTIONIDLELIFETIME",
        "CONNECTIONPRUNINGINTERVAL", "ENLIST", "NORESETONCLOSE", "MAXAUTOPREPARE", "AUTOPREPAREMINUSAGES",
        "READBUFFERSIZE", "WRITEBUFFERSIZE", "CLIENTENCODING", "ENCODING", "TIMEZONE", "SEARCHPATH",
    };

    public static ParsedConnectionString Parse(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        var text = connectionString.Trim();
        if (text.Length == 0)
        {
            throw new FormatException("The connection string is empty.");
        }

        if (ConnectionStringText.TryStrip(text, out var url, "jdbc:postgresql://", "postgresql://", "postgres://"))
        {
            return ParseUrl(url);
        }

        if (!text.Contains('=', StringComparison.Ordinal))
        {
            // Just host[:port].
            var (host, port) = ConnectionStringText.SplitHostPort(text);
            var address = new List<KeyValuePair<string, string>> { new("host", host) };
            if (port is { } number)
            {
                address.Add(new("port", number.ToString(CultureInfo.InvariantCulture)));
            }

            return Result(address);
        }

        // libpq separates with spaces, Npgsql with semicolons.
        return Result(text.Contains(';', StringComparison.Ordinal) || !LooksLikeLibpq(text) ? ConnectionStringText.ReadPairs(text) : ReadLibpq(text));
    }

    private static ParsedConnectionString ParseUrl(string text)
    {
        var parts = ConnectionStringText.ParseUrl(text);
        var pairs = new List<KeyValuePair<string, string>>();

        // Several hosts (host1:5432,host2:5433) keep the names; the first port is used.
        var hosts = parts.Hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ConnectionStringText.SplitHostPort).ToList();
        pairs.Add(new("host", string.Join(',', hosts.Select(static h => h.Host))));
        if (hosts.Count > 0 && hosts[0].Port is { } port)
        {
            pairs.Add(new("port", port.ToString(CultureInfo.InvariantCulture)));
        }

        if (parts.Path is { } database)
        {
            pairs.Add(new("dbname", database));
        }

        if (parts.User is { Length: > 0 } user)
        {
            pairs.Add(new("user", user));
        }

        if (parts.Password is { } password)
        {
            pairs.Add(new("password", password));
        }

        foreach (var (key, value) in parts.Query)
        {
            // JDBC writes ssl=true for "require".
            pairs.Add(ConnectionStringText.Normalize(key) == "SSL" ? new("sslmode", ConnectionStringText.Flag(value) == true ? "require" : "disable") : new(key, value));
        }

        return Result(pairs);
    }

    private static ParsedConnectionString Result(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var ignored = new List<string>();
        string? user = null;
        string? password = null;
        foreach (var (key, value) in pairs)
        {
            switch (ConnectionStringText.Normalize(key))
            {
                case "HOST" or "SERVER" or "HOSTADDR":
                    settings[PostgreSqlConnectionSettings.Host] = value.Trim();
                    break;
                case "PORT":
                    settings[PostgreSqlConnectionSettings.Port] =
                        int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
                            ? port.ToString(CultureInfo.InvariantCulture)
                            : throw new FormatException("The port must be a whole number from 1 to 65535.");
                    break;
                case "DATABASE" or "DB" or "DBNAME":
                    settings[PostgreSqlConnectionSettings.Database] = value.Trim();
                    break;
                case "USERNAME" or "USERID" or "USER" or "UID" or "USERNAMEID":
                    user = value;
                    break;
                case "PASSWORD" or "PSW" or "PWD":
                    password = value;
                    break;
                case "SSLMODE":
                    if (SslModeText(value) is { } mode)
                    {
                        settings[PostgreSqlConnectionSettings.SslMode] = mode;
                    }
                    else
                    {
                        ignored.Add(key);
                    }

                    break;
                case "TIMEOUT" or "CONNECTTIMEOUT" or "CONNECTIONTIMEOUT":
                    if (ConnectionStringText.Seconds(value) is { } seconds)
                    {
                        settings[PostgreSqlConnectionSettings.ConnectTimeoutSeconds] = seconds;
                    }
                    else
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

        if (!settings.ContainsKey(PostgreSqlConnectionSettings.Host))
        {
            throw new FormatException("The connection string has no host.");
        }

        return new ParsedConnectionString { Settings = settings, UserName = user, Password = password, Ignored = ignored };
    }

    private static string? SslModeText(string value) => value.Trim().ToUpperInvariant().Replace("_", "-", StringComparison.Ordinal) switch
    {
        "PREFER" or "ALLOW" => "prefer",
        "DISABLE" => "disable",
        "REQUIRE" => "require",
        "VERIFY-CA" or "VERIFYCA" => "verify-ca",
        "VERIFY-FULL" or "VERIFYFULL" => "verify-full",
        _ => null,
    };

    // "host=a port=5432": every space-separated word is key=value.
    private static bool LooksLikeLibpq(string text) => LibpqStart().IsMatch(text);

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*\w+\s*=\s*('(?:[^'\\]|\\.)*'|\S+)\s+\w+\s*=", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex LibpqStart();

    // key=value separated by spaces; a value may be in single quotes with \' and \\ escapes.
    private static List<KeyValuePair<string, string>> ReadLibpq(string text)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            var start = i;
            while (i < text.Length && text[i] != '=' && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            var key = text[start..i];
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (key.Length == 0 || i >= text.Length || text[i] != '=')
            {
                throw new FormatException("The text is not a connection string of the form key=value.");
            }

            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            var value = new StringBuilder();
            if (i < text.Length && text[i] == '\'')
            {
                i++;
                while (i < text.Length && text[i] != '\'')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                    }

                    value.Append(text[i++]);
                }

                if (i >= text.Length)
                {
                    throw new FormatException("A quoted value is not closed.");
                }

                i++;
            }
            else
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                {
                    value.Append(text[i++]);
                }
            }

            pairs.Add(new(key, value.ToString()));
        }

        return pairs;
    }
}
