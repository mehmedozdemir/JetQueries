using System.Data.Common;
using System.Globalization;

namespace JetQueries.Providers.Connections;

/// <summary>The parts of a URL-style connection string (<c>scheme://user:password@host:port/path?key=value</c>).</summary>
/// <param name="User">The user name, decoded.</param>
/// <param name="Password">The password, decoded.</param>
/// <param name="Hosts">The host part as written (several hosts separated by commas are left together).</param>
/// <param name="Path">What follows the first '/' after the hosts, decoded; null when there is none.</param>
/// <param name="Query">The query parameters, decoded, in order.</param>
internal sealed record UrlParts(string? User, string? Password, string Hosts, string? Path, IReadOnlyList<KeyValuePair<string, string>> Query);

/// <summary>Reading connection strings without the driver's builder, which refuses keywords it does not know.</summary>
/// <remarks>Messages never repeat the text read: it may hold a password.</remarks>
internal static class ConnectionStringText
{
    /// <summary>The <c>key=value;key=value</c> pairs, in the order written.</summary>
    /// <exception cref="FormatException">The text is not of that form.</exception>
    public static IReadOnlyList<KeyValuePair<string, string>> ReadPairs(string text)
    {
        var raw = new DbConnectionStringBuilder();
        try
        {
            raw.ConnectionString = text;
        }
        catch (ArgumentException)
        {
            throw new FormatException("The text is not a connection string of the form key=value;key=value.");
        }

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (string key in raw.Keys)
        {
            pairs.Add(new(key, Convert.ToString(raw[key], CultureInfo.InvariantCulture) ?? string.Empty));
        }

        return pairs;
    }

    /// <summary>A keyword without spaces and underscores, upper case: <c>Initial Catalog</c> → <c>INITIALCATALOG</c>.</summary>
    public static string Normalize(string key) =>
        key.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    /// <summary>A yes / no value as drivers write it; null when it is neither.</summary>
    public static bool? Flag(string value) => value.Trim().ToUpperInvariant() switch
    {
        "TRUE" or "YES" or "1" or "SSPI" or "ON" => true,
        "FALSE" or "NO" or "0" or "OFF" => false,
        _ => null,
    };

    /// <summary>A whole number of seconds from 1 to 600, as text; null otherwise.</summary>
    public static string? Seconds(string value) =>
        int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 1 and <= 600
            ? seconds.ToString(CultureInfo.InvariantCulture)
            : null;

    /// <summary>Whether <paramref name="text"/> starts with one of <paramref name="prefixes"/>; the rest is returned.</summary>
    public static bool TryStrip(string text, out string rest, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                rest = text[prefix.Length..];
                return true;
            }
        }

        rest = text;
        return false;
    }

    /// <summary>Splits what follows <c>scheme://</c>.</summary>
    /// <exception cref="FormatException">There is no host.</exception>
    public static UrlParts ParseUrl(string afterScheme)
    {
        var text = afterScheme;
        var query = new List<KeyValuePair<string, string>>();
        var mark = text.IndexOf('?', StringComparison.Ordinal);
        if (mark >= 0)
        {
            foreach (var part in text[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = part.IndexOf('=', StringComparison.Ordinal);
                query.Add(equals < 0
                    ? new(Decode(part), string.Empty)
                    : new(Decode(part[..equals]), Decode(part[(equals + 1)..])));
            }

            text = text[..mark];
        }

        string? user = null;
        string? password = null;
        var at = text.LastIndexOf('@');
        var slash = text.IndexOf('/', at < 0 ? 0 : at);
        if (at >= 0 && (slash < 0 || at < slash))
        {
            var userInfo = text[..at];
            var colon = userInfo.IndexOf(':', StringComparison.Ordinal);
            user = Decode(colon < 0 ? userInfo : userInfo[..colon]);
            password = colon < 0 ? null : Decode(userInfo[(colon + 1)..]);
            text = text[(at + 1)..];
            slash = text.IndexOf('/', StringComparison.Ordinal);
        }

        var hosts = slash < 0 ? text : text[..slash];
        var path = slash < 0 ? null : Decode(text[(slash + 1)..]);
        if (hosts.Length == 0)
        {
            throw new FormatException("The address has no host.");
        }

        return new UrlParts(user, password, hosts, string.IsNullOrEmpty(path) ? null : path, query);
    }

    /// <summary>Splits <c>host:port</c> (an IPv6 address in brackets keeps its colons); port is null when not given.</summary>
    /// <exception cref="FormatException">The port is not a number from 1 to 65535.</exception>
    public static (string Host, int? Port) SplitHostPort(string text)
    {
        var value = text.Trim();
        var close = value.StartsWith('[') ? value.IndexOf(']', StringComparison.Ordinal) : -1;
        var colon = value.LastIndexOf(':');
        if (colon <= close || colon < 0)
        {
            return (value, null);
        }

        return int.TryParse(value.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? (value[..colon], port)
            : throw new FormatException("The port must be a whole number from 1 to 65535.");
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value);
}
