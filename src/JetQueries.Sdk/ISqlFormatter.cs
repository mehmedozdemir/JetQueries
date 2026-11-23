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

/// <summary>How keywords such as SELECT, FROM, JOIN are written by <see cref="ISqlFormatter"/>.</summary>
public enum SqlKeywordCase
{
    /// <summary>SELECT, FROM.</summary>
    Upper,

    /// <summary>select, from.</summary>
    Lower,

    /// <summary>As they were typed.</summary>
    AsWritten,
}

/// <summary>How <see cref="ISqlFormatter"/> lays SQL out.</summary>
/// <param name="KeywordCase">How keywords are written.</param>
/// <param name="IndentSize">Spaces per indentation level (ignored with <see cref="UseTabs"/>).</param>
public sealed record SqlFormatOptions(SqlKeywordCase KeywordCase = SqlKeywordCase.Upper, int IndentSize = 4)
{
    /// <summary>Upper-case keywords, four spaces, content under its clause, a condition per line.</summary>
    public static SqlFormatOptions Default { get; } = new();

    /// <summary>Indent with one tab per level instead of spaces.</summary>
    public bool UseTabs { get; init; }

    /// <summary>
    /// A clause's content starts on the line below it, indented (<c>SELECT</c> / <c>    a,</c>); when false it
    /// starts on the clause's own line (<c>SELECT a,</c>).
    /// </summary>
    public bool ContentOnNewLine { get; init; } = true;

    /// <summary>Each AND / OR of a WHERE, HAVING or ON condition starts a new line.</summary>
    public bool ConditionsOnNewLine { get; init; } = true;
}
