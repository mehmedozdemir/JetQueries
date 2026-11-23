using System.Text.RegularExpressions;
using JetQueries.Sdk;

namespace JetQueries.Providers.Metadata;

/// <summary>Rewriting stored definitions into the "change it" form, and DROP statements.</summary>
internal static partial class DdlText
{
    // Leading blanks and comments, then CREATE [OR ALTER | OR REPLACE] [EDITIONABLE | NONEDITIONABLE].
    [GeneratedRegex(@"\A(?<lead>(?:\s+|--[^\n]*\n|/\*.*?\*/)*)CREATE(?:\s+OR\s+(?:ALTER|REPLACE))?(?:\s+(?:EDITIONABLE|NONEDITIONABLE))?\b",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingCreate();

    /// <summary>The definition with its leading <c>CREATE …</c> replaced by <paramref name="head"/>; null when it does not start with CREATE.</summary>
    public static string? ReplaceCreate(string definition, string head)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var match = LeadingCreate().Match(definition);
        return match.Success ? match.Groups["lead"].Value + head + definition[match.Length..] : null;
    }

    /// <summary>The keyword of a kind in DROP statements (TABLE, VIEW, …); null for kinds that are not dropped by name.</summary>
    public static string? DropKeyword(DatabaseObjectKinds kind) => kind switch
    {
        DatabaseObjectKinds.Table => "TABLE",
        DatabaseObjectKinds.View => "VIEW",
        DatabaseObjectKinds.Procedure => "PROCEDURE",
        DatabaseObjectKinds.Function => "FUNCTION",
        DatabaseObjectKinds.Package => "PACKAGE",
        DatabaseObjectKinds.PackageBody => "PACKAGE BODY",
        DatabaseObjectKinds.Sequence => "SEQUENCE",
        DatabaseObjectKinds.Synonym => "SYNONYM",
        DatabaseObjectKinds.Trigger => "TRIGGER",
        DatabaseObjectKinds.Type => "TYPE",
        _ => null,
    };
}
