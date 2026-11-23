using System.Collections.Frozen;
using JetQueries.Sdk;

namespace JetQueries.Providers.Language;

/// <summary>The lexical rules of one SQL dialect that the generic tokenizer follows.</summary>
internal sealed record GenericSqlSyntax
{
    public required FrozenSet<string> Keywords { get; init; }

    public required FrozenSet<string> Functions { get; init; }

    public required FrozenSet<string> Types { get; init; }

    /// <summary>Words that must be quoted when used as a name.</summary>
    public required FrozenSet<string> Reserved { get; init; }

    /// <summary><c>/* /* */ */</c> nests (PostgreSQL); otherwise the first <c>*/</c> ends the comment.</summary>
    public bool NestedComments { get; init; }

    /// <summary><c>$$ … $$</c> and <c>$tag$ … $tag$</c> strings (PostgreSQL).</summary>
    public bool DollarQuotes { get; init; }

    /// <summary><c>E'…'</c> strings with backslash escapes (PostgreSQL).</summary>
    public bool EscapeStrings { get; init; }

    /// <summary><c>[name]</c> identifiers (SQLite).</summary>
    public bool BracketIdentifiers { get; init; }

    /// <summary><c>`name`</c> identifiers (SQLite).</summary>
    public bool BacktickIdentifiers { get; init; }

    /// <summary>Characters that start a parameter: <c>$1</c>, <c>:name</c>, <c>@name</c>, <c>?</c>.</summary>
    public string ParameterPrefixes { get; init; } = string.Empty;
}

/// <summary>
/// Tokenizer for the dialects without a parser of their own (PostgreSQL, SQLite): a line at a time with a small
/// carried state, every character classified, never failing.
/// </summary>
internal sealed class GenericSqlTokenizer(GenericSqlSyntax syntax)
{
    // State: bits 0-7 the depth of nested /* */ comments; bits 8-9 the kind of string being continued;
    // bits 10-30 the id of a dollar-quote tag.
    private const int CommentMask = 0xFF;
    private const int StandardString = 1 << 8;
    private const int EscapeString = 2 << 8;
    private const int DollarString = 3 << 8;
    private const int StringMask = 3 << 8;
    private const int TagShift = 10;

    private readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _keywords = syntax.Keywords.GetAlternateLookup<ReadOnlySpan<char>>();
    private readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _functions = syntax.Functions.GetAlternateLookup<ReadOnlySpan<char>>();
    private readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _types = syntax.Types.GetAlternateLookup<ReadOnlySpan<char>>();
    private readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _reserved = syntax.Reserved.GetAlternateLookup<ReadOnlySpan<char>>();

    // Dollar-quote tags seen so far, so that a line state (an int) can name one. "" ($$) is always 0.
    private readonly Lock _tagLock = new();
    private readonly List<string> _tags = [string.Empty];

    public GenericSqlSyntax Syntax => syntax;

    public bool IsReserved(ReadOnlySpan<char> word) => _reserved.Contains(word);

    public static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>Tokenizes one line, appending to <paramref name="tokens"/> with offsets shifted by <paramref name="lineStart"/>.</summary>
    /// <returns>The state to pass to the next line.</returns>
    public int TokenizeLine(ReadOnlySpan<char> line, int state, int lineStart, List<LanguageToken> tokens)
    {
        var i = 0;
        while (i < line.Length)
        {
            var start = i;
            if ((state & CommentMask) != 0)
            {
                i = EndOfComment(line, i, ref state);
                tokens.Add(new LanguageToken(SqlTokenKind.Comment, lineStart + start, i - start));
                continue;
            }

            if ((state & StringMask) != 0)
            {
                i = ContinueString(line, i, ref state);
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
            var afterName = i > 0 && IsNamePart(line[i - 1]);
            SqlTokenKind kind;
            if (c == '-' && next == '-')
            {
                i = line.Length;
                kind = SqlTokenKind.Comment;
            }
            else if (c == '/' && next == '*')
            {
                state = 1;
                i = EndOfComment(line, i + 2, ref state);
                kind = SqlTokenKind.Comment;
            }
            else if (c == '\'')
            {
                state = StandardString;
                i = ContinueString(line, i + 1, ref state);
                kind = SqlTokenKind.StringLiteral;
            }
            else if (!afterName && next == '\'' && c is 'e' or 'E' && syntax.EscapeStrings)
            {
                state = EscapeString;
                i = ContinueString(line, i + 2, ref state);
                kind = SqlTokenKind.StringLiteral;
            }
            else if (!afterName && next == '\'' && c is 'b' or 'B' or 'x' or 'X' or 'n' or 'N')
            {
                // Bit strings, hexadecimal (blob) literals and national strings.
                state = StandardString;
                i = ContinueString(line, i + 2, ref state);
                kind = SqlTokenKind.StringLiteral;
            }
            else if (!afterName && c is 'u' or 'U' && next == '&' && i + 2 < line.Length && line[i + 2] == '\'')
            {
                state = StandardString;
                i = ContinueString(line, i + 3, ref state);
                kind = SqlTokenKind.StringLiteral;
            }
            else if (c == '$' && syntax.DollarQuotes && !afterName && DollarTag(line, i) is { } tag)
            {
                state = DollarString | (TagId(line.Slice(i + 1, tag)) << TagShift);
                i = ContinueString(line, i + tag + 2, ref state);
                kind = SqlTokenKind.StringLiteral;
            }
            else if (c == '"' || (c == '`' && syntax.BacktickIdentifiers))
            {
                i = EndOfQuoted(line, i + 1, c);
                kind = SqlTokenKind.Identifier;
            }
            else if (c == '[' && syntax.BracketIdentifiers)
            {
                var end = line[(i + 1)..].IndexOf(']');
                i = end < 0 ? line.Length : i + end + 2;
                kind = SqlTokenKind.Identifier;
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(next) && !afterName && !(i > 0 && line[i - 1] == '.')))
            {
                i = EndOfNumber(line, i);
                kind = SqlTokenKind.NumberLiteral;
            }
            else if (c == ':' && next == ':')
            {
                // A cast, not a parameter.
                i += 2;
                kind = SqlTokenKind.Operator;
            }
            else if (syntax.ParameterPrefixes.Contains(c, StringComparison.Ordinal) && (c == '?' || IsNamePart(next)) && !(c == '$' && afterName))
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
                i++;
                while (i < line.Length && IsNamePart(line[i]))
                {
                    i++;
                }

                var word = line[start..i];
                kind = _keywords.Contains(word) ? SqlTokenKind.Keyword
                    : _types.Contains(word) ? SqlTokenKind.DataType
                    : _functions.Contains(word) ? SqlTokenKind.Function
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
    public List<LanguageToken> Tokenize(string text) => Tokenize(text, out _);

    /// <summary>Tokenizes a whole document and reports where a comment or string that never ends begins.</summary>
    /// <param name="text">The document.</param>
    /// <param name="unterminatedAt">Offset of the comment or string still open at the end, or -1.</param>
    public List<LanguageToken> Tokenize(string text, out int unterminatedAt)
    {
        var tokens = new List<LanguageToken>(Math.Max(16, text.Length / 5));
        var state = 0;
        var lineStart = 0;
        unterminatedAt = -1;
        while (lineStart <= text.Length)
        {
            var newLine = text.IndexOf('\n', lineStart);
            var lineEnd = newLine < 0 ? text.Length : newLine;
            var line = text.AsSpan(lineStart, lineEnd - lineStart);
            if (line.Length > 0 && line[^1] == '\r')
            {
                line = line[..^1];
            }

            var before = state;
            var count = tokens.Count;
            state = TokenizeLine(line, state, lineStart, tokens);

            // A construct still open at the end of a line was opened by the line's last token, unless that
            // token is the continuation of one opened earlier (then it is the only token and starts the line).
            if (state != 0 && tokens.Count > count)
            {
                var last = tokens[^1];
                if (before == 0 || tokens.Count - count > 1 || last.Start != lineStart)
                {
                    unterminatedAt = last.Start;
                }
            }
            else if (state == 0)
            {
                unterminatedAt = -1;
            }

            if (newLine < 0)
            {
                break;
            }

            lineStart = newLine + 1;
        }

        if (state == 0)
        {
            unterminatedAt = -1;
        }

        return tokens;
    }

    /// <summary>The span of the comment or string that is never closed, if any, as a diagnostic.</summary>
    public IReadOnlyList<SqlDiagnostic> Diagnose(string text)
    {
        Tokenize(text, out var at);
        if (at < 0)
        {
            return [];
        }

        var message = text.AsSpan(at).StartsWith("/*", StringComparison.Ordinal) ? "This comment is never closed." : "This string is never closed.";
        var lineEnd = text.IndexOf('\n', at);
        var length = (lineEnd < 0 ? text.Length : lineEnd) - at;
        return [new SqlDiagnostic(new TextSpan(at, Math.Max(1, Math.Min(length, text.Length - at))), message, SqlDiagnosticSeverity.Error)];
    }

    private int EndOfComment(ReadOnlySpan<char> line, int i, ref int state)
    {
        var depth = state & CommentMask;
        while (i < line.Length)
        {
            if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/')
            {
                i += 2;
                if (--depth == 0)
                {
                    state = 0;
                    return i;
                }
            }
            else if (syntax.NestedComments && line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                i += 2;
                depth = Math.Min(depth + 1, CommentMask);
            }
            else
            {
                i++;
            }
        }

        state = depth;
        return i;
    }

    private int ContinueString(ReadOnlySpan<char> line, int i, ref int state)
    {
        switch (state & StringMask)
        {
            case DollarString:
                {
                    string tag;
                    lock (_tagLock)
                    {
                        tag = _tags[state >> TagShift];
                    }

                    var close = "$" + tag + "$";
                    var end = line[i..].IndexOf(close, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        return line.Length;
                    }

                    state = 0;
                    return i + end + close.Length;
                }

            case EscapeString:
                for (; i < line.Length; i++)
                {
                    if (line[i] == '\\')
                    {
                        i++;
                    }
                    else if (line[i] == '\'')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '\'')
                        {
                            i++;
                            continue;
                        }

                        state = 0;
                        return i + 1;
                    }
                }

                return line.Length;

            default:
                for (; i < line.Length; i++)
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

                    state = 0;
                    return i + 1;
                }

                return line.Length;
        }
    }

    /// <summary>The length of the tag of a dollar quote starting at <paramref name="i"/>, or null if none starts there.</summary>
    private static int? DollarTag(ReadOnlySpan<char> line, int i)
    {
        var j = i + 1;
        if (j < line.Length && char.IsAsciiDigit(line[j]))
        {
            return null;
        }

        while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_'))
        {
            j++;
        }

        return j < line.Length && line[j] == '$' ? j - i - 1 : null;
    }

    private int TagId(ReadOnlySpan<char> tag)
    {
        lock (_tagLock)
        {
            for (var i = 0; i < _tags.Count; i++)
            {
                if (tag.SequenceEqual(_tags[i]))
                {
                    return i;
                }
            }

            // The state has 21 bits for the id; beyond that, a tag is still coloured but continues as "$$".
            if (_tags.Count >= 1 << 20)
            {
                return 0;
            }

            _tags.Add(tag.ToString());
            return _tags.Count - 1;
        }
    }

    private static int EndOfQuoted(ReadOnlySpan<char> line, int i, char quote)
    {
        for (; i < line.Length; i++)
        {
            if (line[i] != quote)
            {
                continue;
            }

            if (i + 1 < line.Length && line[i + 1] == quote)
            {
                i++;
                continue;
            }

            return i + 1;
        }

        return line.Length;
    }

    private static int EndOfNumber(ReadOnlySpan<char> line, int i)
    {
        var start = i;
        i++;
        while (i < line.Length)
        {
            var c = line[i];
            if (char.IsLetterOrDigit(c) || c == '_' || (c == '.' && !(i + 1 < line.Length && line[i + 1] == '.')))
            {
                i++;
            }
            else if (c is '+' or '-' && line[i - 1] is 'e' or 'E' && i + 1 < line.Length && char.IsAsciiDigit(line[i + 1]) &&
                     !line[start..i].ContainsAny('x', 'X'))
            {
                // 1e-5; but in 0x1E-5 the minus is a minus.
                i++;
            }
            else
            {
                break;
            }
        }

        return i;
    }
}
