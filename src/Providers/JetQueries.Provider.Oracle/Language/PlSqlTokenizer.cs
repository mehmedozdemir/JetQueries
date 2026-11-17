using System.Collections.Frozen;
using JetQueries.Providers.Language;
using JetQueries.Sdk;

namespace JetQueries.Provider.Oracle.Language;

/// <summary>
/// Tokenizer for Oracle SQL and PL/SQL as the editor needs it: a line at a time with a small carried state,
/// every character classified, never failing. (The structural parser has its own lexer, which drops comments
/// and knows the SQL*Plus line rules; this one keeps comments and knows colours.)
/// </summary>
internal static class PlSqlTokenizer
{
    // State: bit 0 inside /* */, bit 1 inside a string; bits 8-23 the closing character of a q'[…]' literal
    // (0 for an ordinary string, which ends at a single quote).
    private const int InComment = 1;
    private const int InString = 2;

    private static readonly FrozenSet<string> KeywordSet = FrozenSet.ToFrozenSet(
        [
            "ACCESS", "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUTHID", "BEGIN", "BETWEEN", "BODY", "BULK", "BY", "CASCADE", "CASE",
            "CHECK", "CLOSE", "CLUSTER", "COLLECT", "COLUMN", "COMMENT", "COMMIT", "COMPRESS", "CONNECT", "CONSTANT", "CONSTRAINT", "CONTINUE",
            "CREATE", "CROSS", "CURRENT", "CURSOR", "DECLARE", "DEFAULT", "DELETE", "DESC", "DETERMINISTIC", "DISTINCT", "DROP", "ELSE",
            "ELSIF", "END", "EXCEPTION", "EXCLUSIVE", "EXECUTE", "EXISTS", "EXIT", "FETCH", "FOR", "FORALL", "FOREIGN", "FROM", "FULL",
            "FUNCTION", "GOTO", "GRANT", "GROUP", "HAVING", "IDENTIFIED", "IF", "IMMEDIATE", "IN", "INDEX", "INNER", "INSERT", "INTERSECT",
            "INTO", "IS", "JOIN", "KEY", "LEFT", "LIKE", "LIMIT", "LOCK", "LOOP", "MATCHED", "MERGE", "MINUS", "MODE", "MODIFY", "NOCOPY",
            "NOT", "NOWAIT", "NULL", "OF", "OFFSET", "ON", "ONLY", "OPEN", "OPTION", "OR", "ORDER", "OTHERS", "OUT", "OUTER", "OVER",
            "PACKAGE", "PARTITION", "PIPELINED", "PRAGMA", "PRIMARY", "PRIOR", "PROCEDURE", "PUBLIC", "RAISE", "RECORD", "REFERENCES",
            "RENAME", "REPLACE", "RESULT_CACHE", "RETURN", "RETURNING", "REVOKE", "RIGHT", "ROLLBACK", "ROW", "ROWS", "ROWTYPE",
            "SAVEPOINT", "SELECT", "SEQUENCE", "SET", "SHARE", "START", "SUBTYPE", "SYNONYM", "TABLE", "THEN", "TO", "TRIGGER", "TRUNCATE",
            "TYPE", "UNION", "UNIQUE", "UPDATE", "USING", "VALUES", "VIEW", "WHEN", "WHERE", "WHILE", "WITH",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> FunctionSet = FrozenSet.ToFrozenSet(
        [
            "ABS", "ADD_MONTHS", "AVG", "CAST", "CEIL", "COALESCE", "CONCAT", "COUNT", "CURRENT_DATE", "CURRENT_TIMESTAMP", "DECODE",
            "DENSE_RANK", "EXTRACT", "FIRST_VALUE", "FLOOR", "GREATEST", "INITCAP", "INSTR", "JSON_OBJECT", "JSON_VALUE", "LAG",
            "LAST_DAY", "LAST_VALUE", "LEAD", "LEAST", "LENGTH", "LISTAGG", "LOWER", "LPAD", "LTRIM", "MAX", "MIN", "MOD", "MONTHS_BETWEEN",
            "NEXT_DAY", "NULLIF", "NVL", "NVL2", "POWER", "RANK", "REGEXP_INSTR", "REGEXP_LIKE", "REGEXP_REPLACE", "REGEXP_SUBSTR",
            "REPLACE", "ROUND", "ROW_NUMBER", "RPAD", "RTRIM", "SIGN", "SQLCODE", "SQLERRM", "SQRT", "SUBSTR", "SUM", "SYSDATE",
            "SYSTIMESTAMP", "SYS_CONTEXT", "SYS_GUID", "TO_CHAR", "TO_CLOB", "TO_DATE", "TO_NUMBER", "TO_TIMESTAMP", "TRANSLATE", "TRIM",
            "TRUNC", "UPPER", "USER",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> TypeSet = FrozenSet.ToFrozenSet(
        [
            "BINARY_DOUBLE", "BINARY_FLOAT", "BINARY_INTEGER", "BLOB", "BOOLEAN", "CHAR", "CLOB", "DATE", "DECIMAL", "FLOAT", "INTEGER",
            "INTERVAL", "LONG", "NCHAR", "NCLOB", "NUMBER", "NVARCHAR2", "PLS_INTEGER", "RAW", "ROWID", "SIMPLE_INTEGER", "SMALLINT",
            "TIMESTAMP", "VARCHAR", "VARCHAR2", "XMLTYPE",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Keywords = KeywordSet.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Functions = FunctionSet.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Types = TypeSet.GetAlternateLookup<ReadOnlySpan<char>>();

    public static IReadOnlyCollection<string> AllKeywords => KeywordSet;

    public static IReadOnlyCollection<string> AllFunctions => FunctionSet;

    public static bool IsReserved(ReadOnlySpan<char> word) => Keywords.Contains(word) || Types.Contains(word);

    /// <summary>Tokenizes one line, appending to <paramref name="tokens"/> with offsets shifted by <paramref name="lineStart"/>.</summary>
    /// <returns>The state to pass to the next line.</returns>
    public static int TokenizeLine(ReadOnlySpan<char> line, int state, int lineStart, List<LanguageToken> tokens)
    {
        var i = 0;
        while (i < line.Length)
        {
            var start = i;
            if ((state & InComment) != 0)
            {
                var end = line[i..].IndexOf("*/", StringComparison.Ordinal);
                i = end < 0 ? line.Length : i + end + 2;
                state = end < 0 ? state : 0;
                tokens.Add(new LanguageToken(SqlTokenKind.Comment, lineStart + start, i - start));
                continue;
            }

            if ((state & InString) != 0)
            {
                i = EndOfString(line, i, (char)(state >> 8), out var closed);
                state = closed ? 0 : state;
                tokens.Add(new LanguageToken(SqlTokenKind.StringLiteral, lineStart + start, i - start));
                continue;
            }

            var c = line[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var next = i + 1 < line.Length ? line[i + 1] : '\0';
            SqlTokenKind kind;
            if (c == '-' && next == '-')
            {
                i = line.Length;
                kind = SqlTokenKind.Comment;
            }
            else if (c == '/' && next == '*')
            {
                var end = line[(i + 2)..].IndexOf("*/", StringComparison.Ordinal);
                i = end < 0 ? line.Length : i + end + 4;
                state = end < 0 ? InComment : 0;
                kind = SqlTokenKind.Comment;
            }
            else if (c is 'q' or 'Q' && next == '\'' && i + 2 < line.Length)
            {
                // q'[ … ]' — the text ends at the closing bracket followed by a quote, and may contain quotes.
                var close = line[i + 2] switch
                {
                    '[' => ']',
                    '{' => '}',
                    '(' => ')',
                    '<' => '>',
                    var other => other,
                };
                i = EndOfString(line, i + 3, close, out var closed);
                state = closed ? 0 : InString | (close << 8);
                kind = SqlTokenKind.StringLiteral;
            }
            else if (c == '\'' || (c is 'n' or 'N' && next == '\''))
            {
                i = EndOfString(line, c == '\'' ? i + 1 : i + 2, '\0', out var closed);
                state = closed ? 0 : InString;
                kind = SqlTokenKind.StringLiteral;
            }
            else if (c == '"')
            {
                var end = line[(i + 1)..].IndexOf('"');
                i = end < 0 ? line.Length : i + end + 2;
                kind = SqlTokenKind.Identifier;
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(next) && !(i > 0 && line[i - 1] == '.')))
            {
                // ".5" is a number, but the second dot of "1..10" is not the start of ".10".
                i++;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || (line[i] == '.' && !(i + 1 < line.Length && line[i + 1] == '.'))))
                {
                    i++;
                }

                kind = SqlTokenKind.NumberLiteral;
            }
            else if (c is ':' or '&' && (char.IsLetterOrDigit(next) || next is '_' or '&'))
            {
                // :bind variables and &substitution variables.
                i++;
                while (i < line.Length && (IsNamePart(line[i]) || line[i] == '&'))
                {
                    i++;
                }

                kind = SqlTokenKind.Parameter;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                i++;
                while (i < line.Length && IsNamePart(line[i]))
                {
                    i++;
                }

                var word = line[start..i];
                kind = Keywords.Contains(word) ? SqlTokenKind.Keyword
                    : Types.Contains(word) ? SqlTokenKind.DataType
                    : Functions.Contains(word) ? SqlTokenKind.Function
                    : SqlTokenKind.Identifier;
            }
            else
            {
                i++;
                kind = c is '(' or ')' or ',' or ';' or '.' ? SqlTokenKind.Punctuation : SqlTokenKind.Operator;
            }

            tokens.Add(new LanguageToken(kind, lineStart + start, i - start));
        }

        return state;
    }

    /// <summary>Tokenizes a whole document. Comments are included; white space is not.</summary>
    public static List<LanguageToken> Tokenize(string text)
    {
        var tokens = new List<LanguageToken>(Math.Max(16, text.Length / 5));
        var state = 0;
        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var newLine = text.IndexOf('\n', lineStart);
            var lineEnd = newLine < 0 ? text.Length : newLine;
            var line = text.AsSpan(lineStart, lineEnd - lineStart);
            if (line.Length > 0 && line[^1] == '\r')
            {
                line = line[..^1];
            }

            state = TokenizeLine(line, state, lineStart, tokens);
            if (newLine < 0)
            {
                break;
            }

            lineStart = newLine + 1;
        }

        return tokens;
    }

    public static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    /// <summary>
    /// End of a string whose content starts at <paramref name="from"/>. For an ordinary string
    /// (<paramref name="close"/> is 0) a doubled quote is an escape; for a q-literal the end is the closing
    /// character followed by a quote.
    /// </summary>
    private static int EndOfString(ReadOnlySpan<char> line, int from, char close, out bool closed)
    {
        for (var i = from; i < line.Length; i++)
        {
            if (close == '\0')
            {
                if (line[i] != '\'')
                {
                    continue;
                }

                if (i + 1 < line.Length && line[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                closed = true;
                return i + 1;
            }

            if (line[i] == close && i + 1 < line.Length && line[i + 1] == '\'')
            {
                closed = true;
                return i + 2;
            }
        }

        closed = false;
        return line.Length;
    }
}
