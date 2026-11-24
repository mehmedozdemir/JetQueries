using System.Text;
using System.Text.RegularExpressions;

namespace JetQueries.Provider.Oracle;

/// <summary>One alias of a tnsnames.ora file and where it points.</summary>
/// <param name="Alias">The alias, as written.</param>
/// <param name="Host">The first HOST of its descriptor, if any.</param>
/// <param name="Port">The first PORT, if any.</param>
/// <param name="Service">SERVICE_NAME, else SID, if any.</param>
internal sealed record TnsEntry(string Alias, string? Host, string? Port, string? Service)
{
    /// <summary>"host:port/service", for the list of aliases.</summary>
    public string Describe()
    {
        var where = Host is null ? null : Host + (Port is null ? string.Empty : ":" + Port) + (Service is null ? string.Empty : "/" + Service);
        return where is null ? Alias : Alias + " — " + where;
    }
}

/// <summary>
/// Reads the aliases of a tnsnames.ora file: <c>ALIAS[, ALIAS2] = (DESCRIPTION = …)</c>, with <c>#</c> comments.
/// Only what the connection form shows is taken from the descriptor; the driver reads the file itself.
/// </summary>
internal static partial class TnsNames
{
    [GeneratedRegex(@"\(\s*HOST\s*=\s*(?<v>[^)\s]+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"\(\s*PORT\s*=\s*(?<v>[^)\s]+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PortPattern();

    [GeneratedRegex(@"\(\s*(?:SERVICE_NAME|SID)\s*=\s*(?<v>[^)\s]+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServicePattern();

    /// <summary>The aliases in file order; a name defined twice keeps its first entry. IFILE lines are skipped.</summary>
    public static IReadOnlyList<TnsEntry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new List<TnsEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clean = WithoutComments(text);
        var i = 0;
        while (i < clean.Length)
        {
            // Top level: "names =" then a bracketed descriptor.
            var equals = clean.IndexOf('=', i);
            if (equals < 0)
            {
                break;
            }

            var names = clean[i..equals];
            var open = clean.IndexOf('(', equals);
            var nextEquals = clean.IndexOf('=', equals + 1);
            if (open < 0 || (nextEquals >= 0 && nextEquals < open))
            {
                // "IFILE = path" and other plain parameters: skip to the end of their line.
                var lineEnd = clean.IndexOf('\n', equals);
                i = lineEnd < 0 ? clean.Length : lineEnd + 1;
                continue;
            }

            var close = Matching(clean, open);
            var descriptor = clean[open..close];
            foreach (var name in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (name.Length > 0 && !name.Equals("IFILE", StringComparison.OrdinalIgnoreCase) && seen.Add(name))
                {
                    entries.Add(new TnsEntry(name, Value(HostPattern(), descriptor), Value(PortPattern(), descriptor), Value(ServicePattern(), descriptor)));
                }
            }

            i = close;
        }

        return entries;
    }

    private static string? Value(Regex pattern, string descriptor) => pattern.Match(descriptor) is { Success: true } match ? match.Groups["v"].Value : null;

    // The index after the bracket that closes the one at "open" (or the end of the text).
    private static int Matching(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                return i + 1;
            }
        }

        return text.Length;
    }

    private static string WithoutComments(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var line in text.Split('\n'))
        {
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            result.Append(hash < 0 ? line : line[..hash]).Append('\n');
        }

        return result.ToString();
    }
}
