namespace JetQueries.Sdk;

/// <summary>
/// Optional feature of an <see cref="ISqlLanguageService"/>: lays SQL out on lines (clauses, select lists,
/// conditions, subqueries) without changing what it means.
/// </summary>
/// <remarks>Preview: part of SDK contract version 0.</remarks>
public interface ISqlFormatter
{
    /// <summary>
    /// Formats <paramref name="sql"/>. Comments, literals and identifiers are kept as written; only the space between
    /// tokens and, if asked, the case of keywords change. Text the formatter does not understand is returned as is.
    /// </summary>
    /// <param name="sql">A script, a statement or a selected part of one.</param>
    /// <param name="options">How to lay it out.</param>
    string Format(string sql, SqlFormatOptions options);
}

/// <summary>How <see cref="ISqlFormatter"/> lays SQL out.</summary>
/// <param name="UppercaseKeywords">Write keywords such as SELECT, FROM, JOIN in upper case.</param>
/// <param name="IndentSize">Spaces per indentation level.</param>
public sealed record SqlFormatOptions(bool UppercaseKeywords = true, int IndentSize = 4)
{
    /// <summary>Upper-case keywords, four spaces.</summary>
    public static SqlFormatOptions Default { get; } = new();
}
