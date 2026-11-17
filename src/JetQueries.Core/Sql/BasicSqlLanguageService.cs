using System.Collections.Frozen;
using JetQueries.Sdk;

namespace JetQueries.Core.Sql;

/// <summary>
/// The language service used when a provider has none of its own: it only tells keywords, strings, comments,
/// numbers and names apart, with a keyword list common to the supported dialects. No completion, no
/// diagnostics, no navigation.
/// </summary>
public sealed class BasicSqlLanguageService : ISqlLanguageService
{
    /// <summary>Line state: not inside anything that spans lines.</summary>
    public const int StateNormal = 0;

    /// <summary>Line state: inside a <c>/* … */</c> comment.</summary>
    public const int StateBlockComment = 1;

    private static readonly FrozenSet<string> Keywords = FrozenSet.ToFrozenSet(
        [
            "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "BEGIN", "BETWEEN", "BY", "CALL", "CASCADE", "CASE", "CHECK", "COLUMN",
            "COMMIT", "CONSTRAINT", "CREATE", "CROSS", "CURSOR", "DATABASE", "DECLARE", "DEFAULT", "DELETE", "DENY", "DESC", "DISTINCT",
            "DROP", "ELSE", "ELSIF", "END", "EXCEPT", "EXEC", "EXECUTE", "EXISTS", "FETCH", "FOR", "FOREIGN", "FROM", "FULL", "FUNCTION",
            "GO", "GRANT", "GROUP", "HAVING", "IF", "IN", "INDEX", "INNER", "INSERT", "INTERSECT", "INTO", "IS", "JOIN", "KEY", "LEFT",
            "LIKE", "LIMIT", "LOOP", "MERGE", "MINUS", "NOT", "NULL", "OFFSET", "ON", "OR", "ORDER", "OUTER", "OVER", "PACKAGE",
            "PARTITION", "PRIMARY", "PRINT", "PROCEDURE", "REFERENCES", "RETURN", "RETURNS", "REVOKE", "RIGHT", "ROLLBACK", "ROWS",
            "SCHEMA", "SELECT", "SEQUENCE", "SET", "TABLE", "THEN", "TOP", "TRANSACTION", "TRIGGER", "TRUNCATE", "UNION", "UNIQUE",
            "UPDATE", "USE", "USING", "VALUES", "VIEW", "WHEN", "WHERE", "WHILE", "WITH",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> KeywordLookup = Keywords.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The shared instance; the service has no state.</summary>
    public static BasicSqlLanguageService Instance { get; } = new();

    /// <inheritdoc />
    public SqlLineTokens TokenizeLine(ReadOnlySpan<char> line, int startState)
    {
        var tokens = new List<SqlToken>();
        var i = 0;
        var state = startState;

        if (state == StateBlockComment)
        {
            var end = line.IndexOf("*/", StringComparison.Ordinal);
            if (end < 0)
            {
                if (line.Length > 0)
                {
                    tokens.Add(new SqlToken(new TextSpan(0, line.Length), SqlTokenKind.Comment));
                }

                return new SqlLineTokens(tokens, StateBlockComment);
            }

            tokens.Add(new SqlToken(new TextSpan(0, end + 2), SqlTokenKind.Comment));
            i = end + 2;
            state = StateNormal;
        }

        while (i < line.Length)
        {
            var c = line[i];
            var start = i;
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            SqlTokenKind kind;
            if (c == '-' && i + 1 < line.Length && line[i + 1] == '-')
            {
                i = line.Length;
                kind = SqlTokenKind.Comment;
            }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                var end = line[(i + 2)..].IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                {
                    i = line.Length;
                    state = StateBlockComment;
                }
                else
                {
                    i += end + 4;
                }

                kind = SqlTokenKind.Comment;
            }
            else if (c == '\'' || (c is 'N' or 'n' && i + 1 < line.Length && line[i + 1] == '\''))
            {
                // A string ends at the line end at the latest: an unfinished one must not colour the rest of the file.
                i = Quoted(line, c == '\'' ? i : i + 1, '\'');
                kind = SqlTokenKind.StringLiteral;
            }
            else if (c == '"')
            {
                i = Quoted(line, i, '"');
                kind = SqlTokenKind.Identifier;
            }
            else if (c == '[')
            {
                i = Quoted(line, i, ']');
                kind = SqlTokenKind.Identifier;
            }
            else if (char.IsAsciiDigit(c))
            {
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '.'))
                {
                    i++;
                }

                kind = SqlTokenKind.NumberLiteral;
            }
            else if (c is '@' or ':' && i + 1 < line.Length && (char.IsLetter(line[i + 1]) || line[i + 1] is '_' or '@'))
            {
                i++;
                while (i < line.Length && IsNamePart(line[i]))
                {
                    i++;
                }

                kind = SqlTokenKind.Parameter;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                while (i < line.Length && IsNamePart(line[i]))
                {
                    i++;
                }

                kind = KeywordLookup.Contains(line[start..i]) ? SqlTokenKind.Keyword : SqlTokenKind.Identifier;
            }
            else
            {
                i++;
                kind = c is '(' or ')' or ',' or ';' or '.' ? SqlTokenKind.Punctuation : SqlTokenKind.Operator;
            }

            tokens.Add(new SqlToken(new TextSpan(start, i - start), kind));
        }

        return new SqlLineTokens(tokens, state);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SqlDiagnostic>> GetDiagnosticsAsync(SqlDocument document, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SqlDiagnostic>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(SqlDocument document, int offset, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CompletionItem>>([]);

    /// <inheritdoc />
    public Task<SqlDefinition?> GetDefinitionAsync(SqlDocument document, int offset, CancellationToken cancellationToken) =>
        Task.FromResult<SqlDefinition?>(null);

    private static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '@';

    /// <summary>End (exclusive) of a quoted run starting at <paramref name="open"/>; a doubled closing quote is an escape.</summary>
    private static int Quoted(ReadOnlySpan<char> line, int open, char close)
    {
        var i = open + 1;
        while (i < line.Length)
        {
            if (line[i] == close)
            {
                if (i + 1 < line.Length && line[i + 1] == close)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return line.Length;
    }
}
