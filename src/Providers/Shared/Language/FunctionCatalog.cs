using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;
using JetQueries.Sdk;

namespace JetQueries.Providers.Language;

/// <summary>
/// The built-in functions of a dialect with their signatures and, for parameters that take a fixed set of
/// values, those values. Written as text, one line per overload, and parsed once:
/// <code>
/// # a comment
/// @datepart: year = Year ~ Yıl; month = Month ~ Ay; day = Day ~ Gün
/// DATEADD(datepart @datepart, number int, date datetime) : datetime -- Adds a number of dateparts to a date ~ Tarihe birim ekler
/// ROUND(number numeric, length int, [function int]) : numeric -- Rounds ~ Yuvarlar
/// CONCAT(value any, value any, ...) : varchar -- Joins strings ~ Metinleri birleştirir
/// </code>
/// A parameter is "name type"; "[ … ]" makes it optional; "..." repeats the one before it; a type "@set"
/// takes the values of that set. Descriptions are "English ~ Türkçe"; the user's language picks one.
/// </summary>
internal sealed partial class FunctionCatalog
{
    private readonly FrozenDictionary<string, IReadOnlyList<CatalogFunction>> _functions;

    private FunctionCatalog(Dictionary<string, List<CatalogFunction>> functions) =>
        _functions = functions.ToFrozenDictionary(static p => p.Key, static p => (IReadOnlyList<CatalogFunction>)p.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every function name (as written in the catalog).</summary>
    public IEnumerable<string> Names => _functions.Values.Select(static f => f[0].Name);

    /// <summary>The overloads of a function; empty when the catalog does not know it.</summary>
    public IReadOnlyList<CatalogFunction> Find(string name) => _functions.TryGetValue(name, out var found) ? found : [];

    [GeneratedRegex(@"^(?<name>[A-Za-z_][\w$#]*(?:\.[A-Za-z_][\w$#]*)*)\s*\($", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionName();

    [GeneratedRegex(@"^\s*(?::\s*(?<ret>.+?))?\s*(?:--\s*(?<doc>.*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionTail();

    /// <summary>
    /// "NAME(params) : type -- doc": the parameter list ends at the bracket that closes the first one (a
    /// description may hold brackets of its own).
    /// </summary>
    private static (string Name, string Parameters, string? Returns, string Documentation)? SplitFunction(string line)
    {
        var open = line.IndexOf('(', StringComparison.Ordinal);
        if (open < 0 || !FunctionName().IsMatch(line[..(open + 1)]))
        {
            return null;
        }

        var depth = 0;
        var quoted = false;
        for (var i = open; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'')
            {
                quoted = !quoted;
            }
            else if (!quoted && c == '(')
            {
                depth++;
            }
            else if (!quoted && c == ')' && --depth == 0)
            {
                var tail = FunctionTail().Match(line[(i + 1)..]);
                if (!tail.Success)
                {
                    return null;
                }

                return (line[..open].Trim(), line[(open + 1)..i], tail.Groups["ret"].Success ? tail.Groups["ret"].Value.Trim() : null, tail.Groups["doc"].Value);
            }
        }

        return null;
    }

    /// <summary>Reads a catalog written as described above.</summary>
    /// <exception cref="FormatException">A line is not understood (a mistake in the provider's catalog).</exception>
    public static FunctionCatalog Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sets = new Dictionary<string, IReadOnlyList<CatalogValue>>(StringComparer.OrdinalIgnoreCase);
        var functions = new Dictionary<string, List<CatalogFunction>>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Split('\n');
        for (var number = 0; number < lines.Length; number++)
        {
            var line = lines[number].Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '@')
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon < 0)
                {
                    throw new FormatException($"Line {number + 1}: a value set needs '@name: …'.");
                }

                sets[line[1..colon].Trim()] = [.. SplitTop(line[(colon + 1)..], ';').Select(ParseValue).Where(static v => v.Text.Length > 0)];
                continue;
            }

            if (SplitFunction(line) is not { } match)
            {
                throw new FormatException($"Line {number + 1}: '{line}' is not a function.");
            }

            var parameters = new List<CatalogParameter>();
            var variadic = false;
            foreach (var part in SplitTop(match.Parameters, ','))
            {
                var p = part.Trim();
                if (p.Length == 0)
                {
                    continue;
                }

                if (p == "...")
                {
                    variadic = true;
                    continue;
                }

                var optional = p[0] == '[' && p[^1] == ']';
                if (optional)
                {
                    p = p[1..^1].Trim();
                }

                var space = p.IndexOf(' ', StringComparison.Ordinal);
                var name = space < 0 ? p : p[..space];
                var type = space < 0 ? null : p[(space + 1)..].Trim();
                var values = type is { Length: > 1 } && type[0] == '@'
                    ? sets.TryGetValue(type[1..], out var set) ? set : throw new FormatException($"Line {number + 1}: unknown value set '{type}'.")
                    : [];
                parameters.Add(new CatalogParameter(name, type is { Length: > 0 } && type[0] == '@' ? type[1..] : type, optional, values));
            }

            var function = new CatalogFunction(match.Name, parameters, variadic, match.Returns, Bilingual.Read(match.Documentation));
            if (!functions.TryGetValue(function.Name, out var overloads))
            {
                functions[function.Name] = overloads = [];
            }

            overloads.Add(function);
        }

        return new FunctionCatalog(functions);
    }

    private static CatalogValue ParseValue(string part)
    {
        var equals = part.IndexOf('=', StringComparison.Ordinal);

        // A quoted value may itself hold '='; the description starts at the first '=' after its closing quote.
        if (part.TrimStart() is ['\'', ..] quoted)
        {
            var close = quoted.IndexOf('\'', 1);
            equals = close < 0 ? -1 : quoted.IndexOf('=', close);
            part = quoted;
        }

        return equals < 0
            ? new CatalogValue(part.Trim(), Bilingual.Empty)
            : new CatalogValue(part[..equals].Trim(), Bilingual.Read(part[(equals + 1)..]));
    }

    // Splits at a separator outside quotes and parentheses.
    private static IEnumerable<string> SplitTop(string text, char separator)
    {
        var depth = 0;
        var quoted = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                quoted = !quoted;
            }
            else if (!quoted && c is '(' or '[')
            {
                depth++;
            }
            else if (!quoted && c is ')' or ']')
            {
                depth--;
            }
            else if (!quoted && depth == 0 && c == separator)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }

        yield return text[start..];
    }
}

/// <summary>A text in English and Turkish ("English ~ Türkçe"); the user's language picks one.</summary>
internal readonly record struct Bilingual(string English, string Turkish)
{
    public static Bilingual Empty { get; } = new(string.Empty, string.Empty);

    public static Bilingual Read(string text)
    {
        var tilde = text.IndexOf('~', StringComparison.Ordinal);
        return tilde < 0 ? new(text.Trim(), text.Trim()) : new(text[..tilde].Trim(), text[(tilde + 1)..].Trim());
    }

    /// <summary>In the user's language; null when empty.</summary>
    public string? Text
    {
        get
        {
            var text = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? Turkish : English;
            return text.Length == 0 ? null : text;
        }
    }
}

internal sealed record CatalogValue(string Text, Bilingual Description);

internal sealed record CatalogParameter(string Name, string? Type, bool IsOptional, IReadOnlyList<CatalogValue> Values);

/// <summary>One overload of a built-in function.</summary>
internal sealed record CatalogFunction(string Name, IReadOnlyList<CatalogParameter> Parameters, bool IsVariadic, string? ReturnType, Bilingual Documentation)
{
    /// <summary>"DATEADD(datepart, number, date)" — the list's short form.</summary>
    public string ShortSignature =>
        Name + "(" + string.Join(", ", Parameters.Select(static p => p.IsOptional ? "[" + p.Name + "]" : p.Name)) + (IsVariadic ? ", …" : string.Empty) + ")";

    /// <summary>As the SDK's signature.</summary>
    public SqlSignature ToSignature() => new(
        Name,
        [.. Parameters.Select(static p => new SqlParameterInfo(p.Name, p.Type, null, p.IsOptional)
        {
            Values = [.. p.Values.Select(static v => new SqlParameterValue(v.Text, v.Description.Text))],
        })],
        ReturnType,
        Documentation.Text)
    {
        IsVariadic = IsVariadic,
    };
}
