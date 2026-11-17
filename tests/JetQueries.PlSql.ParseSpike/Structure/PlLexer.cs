using System.Collections.Frozen;

namespace JetQueries.PlSql.ParseSpike.Structure;

internal enum PlTokenKind : byte
{
    Eof,
    Identifier,
    QuotedIdentifier,
    Number,
    String,
    Bind,
    Substitution,
    Directive,
    Semicolon,
    Comma,
    LParen,
    RParen,
    Dot,
    Percent,
    LabelOpen,
    LabelClose,
    Slash,
    Operator,
}

/// <summary>The words the structural parser reacts to. Everything else is an ordinary identifier.</summary>
internal enum PlKeyword : byte
{
    None,
    Create, Or, Replace, Editionable, Noneditionable, Package, Body, Procedure, Function, Trigger, Type, Library, Java,
    Is, As, Begin, End, Declare, Exception, If, Then, Elsif, Else, Case, When, Loop, While, For, Forall,
    Return, Cursor, Subtype, Pragma, Constant, Language, External, Member, Static, Constructor, Map, Order, Final,
    Instantiable, Overriding, Not, Object, Under, Self, Result, Select, Insert, Update, Delete, Merge, With, Commit,
    Rollback, Savepoint, Lock, Set, Compound, Call, Before, After, Instead, Wrapped,
    DirectiveIf, DirectiveThen, DirectiveElsif, DirectiveElse, DirectiveEnd, DirectiveError,
}

internal readonly record struct PlToken(PlTokenKind Kind, PlKeyword Keyword, int Start, int Length, bool AloneOnLine)
{
    public int End => Start + Length;
}

/// <summary>
/// Hand-written PL/SQL tokenizer. Never throws and never backtracks: an unterminated string or comment simply
/// runs to the end of the text. Comparisons are ordinal, so the Turkish locale cannot change keyword matching.
/// </summary>
internal sealed class PlLexer(string text)
{
    private static readonly FrozenDictionary<string, PlKeyword> Keywords = BuildKeywords();
    private static readonly FrozenDictionary<string, PlKeyword>.AlternateLookup<ReadOnlySpan<char>> KeywordLookup =
        Keywords.GetAlternateLookup<ReadOnlySpan<char>>();

    private readonly string _text = text;
    private PlTokenKind _previous = PlTokenKind.Eof;

    public int Position { get; set; }

    /// <summary>Skips white space and comments. Returns true when a line break was crossed (or at the start).</summary>
    public bool SkipTrivia()
    {
        var text = _text;
        var i = Position;
        var newLine = i == 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n')
            {
                newLine = true;
                i++;
            }
            else if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                newLine |= text.AsSpan(i, (end < 0 ? text.Length : end) - i).Contains('\n');
                i = end < 0 ? text.Length : end + 2;
            }
            else
            {
                break;
            }
        }

        Position = i;
        return newLine;
    }

    public PlToken Next()
    {
        var firstOnLine = SkipTrivia();
        var text = _text;
        var start = Position;
        if (start >= text.Length)
        {
            return new PlToken(PlTokenKind.Eof, PlKeyword.None, text.Length, 0, false);
        }

        var c = text[start];
        var i = start + 1;
        var kind = PlTokenKind.Operator;
        var keyword = PlKeyword.None;
        var alone = false;
        char next = i < text.Length ? text[i] : '\0';

        if (char.IsLetter(c) || c == '_')
        {
            if (next == '\'' && c is 'q' or 'Q' or 'n' or 'N' or 'u' or 'U' && next == '\'')
            {
                i = c is 'q' or 'Q' ? QuotedLiteral(i) : PlainString(i);
                kind = PlTokenKind.String;
            }
            else if (c is 'n' or 'N' && next is 'q' or 'Q' && i + 1 < text.Length && text[i + 1] == '\'')
            {
                i = QuotedLiteral(i + 1);
                kind = PlTokenKind.String;
            }
            else
            {
                while (i < text.Length && IsIdentifierPart(text[i]))
                {
                    i++;
                }

                kind = PlTokenKind.Identifier;

                // "x.end" and "c%type" are member and attribute names, not keywords.
                if (_previous is not (PlTokenKind.Dot or PlTokenKind.Percent))
                {
                    KeywordLookup.TryGetValue(text.AsSpan(start, i - start), out keyword);
                }
            }
        }
        else if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(next) && _previous is not (PlTokenKind.Identifier or PlTokenKind.QuotedIdentifier or PlTokenKind.RParen)))
        {
            i = Number(start);
            kind = PlTokenKind.Number;
        }
        else
        {
            switch (c)
            {
                case '\'':
                    i = PlainString(start);
                    kind = PlTokenKind.String;
                    break;
                case '"':
                    {
                        var end = text.IndexOf('"', i);
                        i = end < 0 ? text.Length : end + 1;
                        kind = PlTokenKind.QuotedIdentifier;
                        break;
                    }

                case ';': kind = PlTokenKind.Semicolon; break;
                case ',': kind = PlTokenKind.Comma; break;
                case '(': kind = PlTokenKind.LParen; break;
                case ')': kind = PlTokenKind.RParen; break;
                case '%': kind = PlTokenKind.Percent; break;
                case '.':
                    if (next == '.')
                    {
                        i++;
                    }
                    else
                    {
                        kind = PlTokenKind.Dot;
                    }

                    break;
                case '/':
                    kind = PlTokenKind.Slash;
                    alone = firstOnLine && RestOfLineIsBlank(i);
                    break;
                case ':':
                    if (next == '=')
                    {
                        i++;
                    }
                    else if (char.IsLetterOrDigit(next) || next == '_')
                    {
                        while (i < text.Length && IsIdentifierPart(text[i]))
                        {
                            i++;
                        }

                        kind = PlTokenKind.Bind;
                    }

                    break;
                case '&':
                    if (next == '&')
                    {
                        i++;
                    }

                    if (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    {
                        while (i < text.Length && IsIdentifierPart(text[i]))
                        {
                            i++;
                        }

                        // "&schema..pkg": the first dot ends the substitution variable.
                        if (i + 1 < text.Length && text[i] == '.' && text[i + 1] == '.')
                        {
                            i++;
                        }

                        kind = PlTokenKind.Substitution;
                    }

                    break;
                case '$':
                    while (i < text.Length && IsIdentifierPart(text[i]))
                    {
                        i++;
                    }

                    if (next == '$')
                    {
                        kind = PlTokenKind.Identifier;
                    }
                    else if (i > start + 1)
                    {
                        kind = PlTokenKind.Directive;
                        KeywordLookup.TryGetValue(text.AsSpan(start, i - start), out keyword);
                    }

                    break;
                case '<':
                    if (next == '<')
                    {
                        i++;
                        kind = PlTokenKind.LabelOpen;
                    }
                    else if (next is '=' or '>')
                    {
                        i++;
                    }

                    break;
                case '>':
                    if (next == '>')
                    {
                        i++;
                        kind = PlTokenKind.LabelClose;
                    }
                    else if (next == '=')
                    {
                        i++;
                    }

                    break;
                case '!' or '^' or '~' when next == '=':
                case '|' when next == '|':
                case '*' when next == '*':
                case '=' when next == '>':
                    i++;
                    break;
                default:
                    break;
            }
        }

        Position = i;
        _previous = kind;
        return new PlToken(kind, keyword, start, i - start, alone);
    }

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    private bool RestOfLineIsBlank(int from)
    {
        for (var i = from; i < _text.Length; i++)
        {
            var c = _text[i];
            if (c == '\n')
            {
                return true;
            }

            if (!char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>From the position of the opening quote (or its one-letter prefix) to after the closing quote.</summary>
    private int PlainString(int from)
    {
        var text = _text;
        var i = text.IndexOf('\'', from) + 1;
        while (true)
        {
            var end = text.IndexOf('\'', i);
            if (end < 0)
            {
                return text.Length;
            }

            if (end + 1 < text.Length && text[end + 1] == '\'')
            {
                i = end + 2;
                continue;
            }

            return end + 1;
        }
    }

    /// <summary>q'[ ... ]' literals; <paramref name="quote"/> is the position of the opening quote.</summary>
    private int QuotedLiteral(int quote)
    {
        var text = _text;
        if (quote + 1 >= text.Length)
        {
            return text.Length;
        }

        var close = text[quote + 1] switch
        {
            '[' => ']',
            '{' => '}',
            '(' => ')',
            '<' => '>',
            var other => other,
        };

        for (var i = quote + 2; i + 1 < text.Length; i++)
        {
            if (text[i] == close && text[i + 1] == '\'')
            {
                return i + 2;
            }
        }

        return text.Length;
    }

    private int Number(int start)
    {
        var text = _text;
        var i = start;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        // "1..10" is a range, not "1." followed by ".10".
        if (i < text.Length && text[i] == '.' && !(i + 1 < text.Length && text[i + 1] == '.'))
        {
            i++;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }
        }

        if (i < text.Length && text[i] is 'e' or 'E')
        {
            var j = i + 1;
            if (j < text.Length && text[j] is '+' or '-')
            {
                j++;
            }

            if (j < text.Length && char.IsAsciiDigit(text[j]))
            {
                i = j;
                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }
            }
        }

        if (i < text.Length && text[i] is 'f' or 'F' or 'd' or 'D' && !(i + 1 < text.Length && IsIdentifierPart(text[i + 1])))
        {
            i++;
        }

        return i;
    }

    private static FrozenDictionary<string, PlKeyword> BuildKeywords()
    {
        var map = new Dictionary<string, PlKeyword>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyword in Enum.GetValues<PlKeyword>())
        {
            if (keyword is PlKeyword.None or >= PlKeyword.DirectiveIf)
            {
                continue;
            }

            map[keyword.ToString()] = keyword;
        }

        map["$IF"] = PlKeyword.DirectiveIf;
        map["$THEN"] = PlKeyword.DirectiveThen;
        map["$ELSIF"] = PlKeyword.DirectiveElsif;
        map["$ELSE"] = PlKeyword.DirectiveElse;
        map["$END"] = PlKeyword.DirectiveEnd;
        map["$ERROR"] = PlKeyword.DirectiveError;
        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
