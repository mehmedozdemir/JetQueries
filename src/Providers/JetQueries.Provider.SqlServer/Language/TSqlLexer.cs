using System.Collections.Frozen;
using JetQueries.Sdk;

namespace JetQueries.Provider.SqlServer.Language;

/// <summary>A token of a whole document: kind, position and whether it is a bracketed or quoted name.</summary>
internal readonly record struct TSqlToken(SqlTokenKind Kind, int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// Hand-written T-SQL tokenizer. It works a line at a time with a small carried state, which is what syntax
/// colouring needs, and never fails: unfinished strings and comments simply run on.
/// </summary>
internal static class TSqlLexer
{
    // State: bits 0-15 nesting depth of /* */ comments (T-SQL comments nest), bit 16 inside a '...' string.
    private const int InString = 1 << 16;
    private const int DepthMask = 0xFFFF;

    private static readonly FrozenSet<string> KeywordSet = FrozenSet.ToFrozenSet(
        [
            "ADD", "ALL", "ALTER", "AND", "ANY", "APPLY", "AS", "ASC", "AUTHORIZATION", "BACKUP", "BEGIN", "BETWEEN", "BREAK", "BROWSE",
            "BULK", "BY", "CASCADE", "CASE", "CATCH", "CHECK", "CHECKPOINT", "CLOSE", "CLUSTERED", "COLLATE", "COLUMN", "COMMIT",
            "COMPUTE", "CONSTRAINT", "CONTAINS", "CONTINUE", "CREATE", "CROSS", "CURRENT", "CURSOR", "DATABASE", "DBCC", "DEALLOCATE",
            "DECLARE", "DEFAULT", "DELETE", "DENY", "DESC", "DISTINCT", "DISTRIBUTED", "DROP", "ELSE", "END", "ESCAPE", "EXCEPT", "EXEC",
            "EXECUTE", "EXISTS", "EXIT", "EXTERNAL", "FETCH", "FILE", "FILLFACTOR", "FIRST", "FOR", "FOREIGN", "FROM", "FULL", "FUNCTION",
            "GO", "GOTO", "GRANT", "GROUP", "HAVING", "HOLDLOCK", "IDENTITY", "IDENTITY_INSERT", "IF", "IN", "INCLUDE", "INDEX", "INNER",
            "INSERT", "INSTEAD", "INTERSECT", "INTO", "IS", "JOIN", "KEY", "KILL", "LAST", "LEFT", "LIKE", "MATCHED", "MERGE", "NEXT",
            "NOCHECK", "NOCOUNT", "NOLOCK", "NONCLUSTERED", "NOT", "NULL", "OF", "OFF", "OFFSET", "ON", "ONLY", "OPEN", "OPTION", "OR",
            "ORDER", "OUTER", "OUTPUT", "OVER", "PARTITION", "PERCENT", "PIVOT", "PRIMARY", "PRINT", "PROC", "PROCEDURE", "PUBLIC",
            "RAISERROR", "READ", "RECOMPILE", "REFERENCES", "RETURN", "RETURNS", "REVERT", "REVOKE", "RIGHT", "ROLLBACK", "ROW", "ROWS",
            "SAVE", "SCHEMA", "SELECT", "SEQUENCE", "SET", "SOME", "STATISTICS", "TABLE", "TABLESAMPLE", "THEN", "THROW", "TIES", "TO",
            "TOP", "TRAN", "TRANSACTION", "TRIGGER", "TRUNCATE", "TRY", "TYPE", "UNION", "UNIQUE", "UNPIVOT", "UPDATE", "USE", "USING",
            "VALUES", "VIEW", "WAITFOR", "WHEN", "WHERE", "WHILE", "WITH", "WITHIN",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> FunctionSet = FrozenSet.ToFrozenSet(
        [
            "ABS", "AVG", "CAST", "CEILING", "CHARINDEX", "CHOOSE", "COALESCE", "CONCAT", "CONCAT_WS", "CONVERT", "COUNT", "COUNT_BIG",
            "DATEADD", "DATEDIFF", "DATEFROMPARTS", "DATENAME", "DATEPART", "DATETRUNC", "DAY", "DB_ID", "DB_NAME", "DENSE_RANK",
            "EOMONTH", "ERROR_LINE", "ERROR_MESSAGE", "ERROR_NUMBER", "FIRST_VALUE", "FLOOR", "FORMAT", "GETDATE", "GETUTCDATE",
            "HASHBYTES", "IIF", "ISDATE", "ISJSON", "ISNULL", "ISNUMERIC", "JSON_QUERY", "JSON_VALUE", "LAG", "LAST_VALUE", "LEAD", "LEN",
            "LOWER", "LTRIM", "MAX", "MIN", "MONTH", "NEWID", "NEWSEQUENTIALID", "NTILE", "NULLIF", "OBJECT_ID", "OBJECT_NAME",
            "OPENJSON", "PARSE", "PATINDEX", "POWER", "QUOTENAME", "RAND", "RANK", "REPLACE", "REPLICATE", "REVERSE", "ROUND",
            "ROW_NUMBER", "RTRIM", "SCHEMA_NAME", "SCOPE_IDENTITY", "SIGN", "SQRT", "STRING_AGG", "STRING_SPLIT", "STUFF", "SUBSTRING",
            "SUM", "SUSER_SNAME", "SYSDATETIME", "SYSDATETIMEOFFSET", "SYSUTCDATETIME", "TRIM", "TRY_CAST", "TRY_CONVERT", "TRY_PARSE",
            "UPPER", "USER_NAME", "XACT_STATE", "YEAR",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> TypeSet = FrozenSet.ToFrozenSet(
        [
            "BIGINT", "BINARY", "BIT", "CHAR", "DATE", "DATETIME", "DATETIME2", "DATETIMEOFFSET", "DECIMAL", "FLOAT", "GEOGRAPHY",
            "GEOMETRY", "HIERARCHYID", "IMAGE", "INT", "MONEY", "NCHAR", "NTEXT", "NUMERIC", "NVARCHAR", "REAL", "ROWVERSION",
            "SMALLDATETIME", "SMALLINT", "SMALLMONEY", "SQL_VARIANT", "SYSNAME", "TEXT", "TIME", "TIMESTAMP", "TINYINT",
            "UNIQUEIDENTIFIER", "VARBINARY", "VARCHAR", "XML",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Keywords = KeywordSet.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Functions = FunctionSet.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Types = TypeSet.GetAlternateLookup<ReadOnlySpan<char>>();

    public static IReadOnlyCollection<string> AllKeywords => KeywordSet;

    public static IReadOnlyCollection<string> AllFunctions => FunctionSet;

    public static bool IsKeyword(ReadOnlySpan<char> word) => Keywords.Contains(word);

    /// <summary>Tokenizes one line, appending to <paramref name="tokens"/> with offsets shifted by <paramref name="lineStart"/>.</summary>
    /// <returns>The state to pass to the next line.</returns>
    public static int TokenizeLine(ReadOnlySpan<char> line, int state, int lineStart, List<TSqlToken> tokens)
    {
        var i = 0;
        var depth = state & DepthMask;
        var inString = (state & InString) != 0;

        while (i < line.Length)
        {
            var start = i;
            if (inString)
            {
                i = EndOfQuoted(line, i, '\'', out var closed);
                inString = !closed;
                tokens.Add(new TSqlToken(SqlTokenKind.StringLiteral, lineStart + start, i - start));
                continue;
            }

            if (depth > 0)
            {
                i = EndOfComment(line, i, ref depth);
                tokens.Add(new TSqlToken(SqlTokenKind.Comment, lineStart + start, i - start));
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
                depth = 1;
                i = EndOfComment(line, i + 2, ref depth);
                kind = SqlTokenKind.Comment;
            }
            else if (c == '\'' || (c is 'N' or 'n' && next == '\''))
            {
                i = EndOfQuoted(line, c == '\'' ? i + 1 : i + 2, '\'', out var closed);
                inString = !closed;
                kind = SqlTokenKind.StringLiteral;
            }
            else if (c == '[')
            {
                i = EndOfQuoted(line, i + 1, ']', out _);
                kind = SqlTokenKind.Identifier;
            }
            else if (c == '"')
            {
                i = EndOfQuoted(line, i + 1, '"', out _);
                kind = SqlTokenKind.Identifier;
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(next)))
            {
                i++;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '.'))
                {
                    i++;
                }

                kind = SqlTokenKind.NumberLiteral;
            }
            else if (c == '@')
            {
                i++;
                while (i < line.Length && IsNamePart(line[i]))
                {
                    i++;
                }

                kind = SqlTokenKind.Parameter;
            }
            else if (char.IsLetter(c) || c is '_' or '#')
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

            tokens.Add(new TSqlToken(kind, lineStart + start, i - start));
        }

        return depth | (inString ? InString : 0);
    }

    /// <summary>Tokenizes a whole document. Comments are included; white space is not.</summary>
    public static List<TSqlToken> Tokenize(string text)
    {
        var tokens = new List<TSqlToken>(Math.Max(16, text.Length / 5));
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

    public static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '@';

    /// <summary>
    /// Finds the end of a quoted run whose content starts at <paramref name="from"/>; a doubled closing
    /// character is an escape. <paramref name="closed"/> is false when the line ends first.
    /// </summary>
    private static int EndOfQuoted(ReadOnlySpan<char> line, int from, char close, out bool closed)
    {
        var i = from;
        while (i < line.Length)
        {
            if (line[i] == close)
            {
                if (i + 1 < line.Length && line[i + 1] == close)
                {
                    i += 2;
                    continue;
                }

                closed = true;
                return i + 1;
            }

            i++;
        }

        closed = false;
        return line.Length;
    }

    /// <summary>Scans a block comment from inside it. Comments nest: /* a /* b */ still a */.</summary>
    /// <returns>The position after the comment, or the end of the line if it is still open (depth stays above zero).</returns>
    private static int EndOfComment(ReadOnlySpan<char> line, int from, ref int depth)
    {
        var i = from;
        while (i < line.Length && depth > 0)
        {
            if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/')
            {
                depth--;
                i += 2;
            }
            else if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                depth++;
                i += 2;
            }
            else
            {
                i++;
            }
        }

        return i;
    }
}
