using System.Globalization;
using JetQueries.Sdk;

namespace JetQueries.Providers.Metadata;

/// <summary>Pieces the providers' "Execute routine" scripts share: reading the user's values safely.</summary>
internal static class RoutineScriptText
{
    /// <summary>A plain decimal or scientific number (invariant: "-12.5", "1e3"); nothing else is written unquoted.</summary>
    public static bool IsNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        return value.All(static c => char.IsAsciiDigit(c) || c is '+' or '-' or '.' or 'e' or 'E') &&
               double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>A string literal: single quotes, the quotes inside doubled.</summary>
    public static string Quote(string text, string prefix = "") => prefix + "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>A parameter name without SQL Server's "@".</summary>
    public static string Plain(string name) => name.TrimStart('@');

    /// <summary>The argument given for a parameter (by name, ignoring case and "@"); NULL when none was given.</summary>
    public static RoutineArgument ArgumentFor(IReadOnlyList<RoutineArgument> arguments, SqlParameterInfo parameter) =>
        arguments.FirstOrDefault(a => string.Equals(Plain(a.Name), Plain(parameter.Name), StringComparison.OrdinalIgnoreCase))
        ?? new RoutineArgument(parameter.Name, RoutineArgumentKind.Null);

    /// <summary>The base type in upper case, without length or precision: "NVARCHAR" for "nvarchar(50)".</summary>
    public static string BaseType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return string.Empty;
        }

        var paren = dataType.IndexOf('(', StringComparison.Ordinal);
        return (paren < 0 ? dataType : dataType[..paren]).Trim().ToUpperInvariant();
    }

    /// <summary>Whether the parameter returns a value (OUT or IN OUT).</summary>
    public static bool IsOutput(SqlParameterInfo parameter) => parameter.Direction is "OUT" or "IN OUT" or "OUTPUT" or "INOUT";

    /// <summary>A name made only of letters, digits and "_", usable as part of a variable name.</summary>
    public static string Identifier(string name) => new([.. Plain(name).Select(static c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_')]);
}
