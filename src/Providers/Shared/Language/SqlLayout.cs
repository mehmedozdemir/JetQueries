using System.Text;
using JetQueries.Sdk;

namespace JetQueries.Providers.Language;

/// <summary>
/// Lays SQL out on lines, on tokens alone, for every dialect: each clause on its own line with its content indented
/// below it, one select-list item per line, AND / OR conditions on their own lines, subqueries and CASE indented.
/// Only the space between tokens and the case of keywords change: comments, literals and identifiers stay as
/// written, and two tokens written together stay together. Procedural code (procedures, functions, packages,
/// triggers, blocks) and statements other than queries and DML are kept as written. A result that would not
/// tokenize back to the same tokens is never returned: the text comes back unchanged instead.
/// </summary>
internal static class SqlLayout
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "BETWEEN", "EXISTS", "AS", "ON", "JOIN", "INNER",
        "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "GROUP", "BY", "ORDER", "HAVING", "UNION", "ALL", "DISTINCT", "TOP", "INSERT",
        "INTO", "VALUES", "UPDATE", "SET", "DELETE", "CASE", "WHEN", "THEN", "ELSE", "END", "ASC", "DESC", "WITH", "LIMIT", "OFFSET",
        "FETCH", "MERGE", "USING", "EXCEPT", "INTERSECT", "MINUS", "RETURNING", "APPLY", "OVER", "PARTITION", "ESCAPE", "NULLS",
    };

    private static readonly string[][] BlockClauses =
    [
        ["GROUP", "BY"], ["ORDER", "BY"], ["INSERT", "INTO"], ["DELETE", "FROM"], ["MERGE", "INTO"],
        ["SELECT"], ["FROM"], ["WHERE"], ["HAVING"], ["SET"], ["VALUES"], ["INSERT"], ["UPDATE"], ["DELETE"], ["MERGE"],
        ["RETURNING"], ["QUALIFY"],
    ];

    private static readonly string[][] LineClauses =
    [
        ["LEFT", "OUTER", "JOIN"], ["RIGHT", "OUTER", "JOIN"], ["FULL", "OUTER", "JOIN"], ["LEFT", "JOIN"], ["RIGHT", "JOIN"],
        ["FULL", "JOIN"], ["INNER", "JOIN"], ["CROSS", "JOIN"], ["NATURAL", "JOIN"], ["CROSS", "APPLY"], ["OUTER", "APPLY"], ["JOIN"],
        ["LIMIT"], ["OFFSET"], ["FETCH"], ["CONNECT", "BY"], ["START", "WITH"], ["FOR", "UPDATE"], ["FOR", "XML"], ["FOR", "JSON"],
        ["FOR", "SHARE"], ["FOR", "BROWSE"], ["WHEN", "NOT", "MATCHED"], ["WHEN", "MATCHED"], ["OPTION"],
    ];

    private static readonly string[][] SetOperators = [["UNION", "ALL"], ["UNION", "DISTINCT"], ["UNION"], ["EXCEPT"], ["INTERSECT"], ["MINUS"]];

    private static readonly HashSet<string> Formatted = new(StringComparer.OrdinalIgnoreCase) { "SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "MERGE" };

    private static readonly HashSet<string> Procedural = new(StringComparer.OrdinalIgnoreCase) { "DECLARE", "BEGIN", "DO", "IF", "WHILE" };

    private static readonly HashSet<string> Routine = new(StringComparer.OrdinalIgnoreCase) { "PROCEDURE", "PROC", "FUNCTION", "TRIGGER", "PACKAGE", "TYPE" };

    private static readonly HashSet<string> StatementStart = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE", "ALTER", "DROP", "EXEC", "EXECUTE", "USE", "DECLARE",
        "PRINT", "GRANT", "REVOKE", "TRUNCATE", "BEGIN", "IF", "COMMIT", "ROLLBACK", "SET",
    };

    private static readonly HashSet<string> Modifiers = new(StringComparer.OrdinalIgnoreCase) { "DISTINCT", "ALL", "UNIQUE", "RECURSIVE", "TOP" };

    /// <param name="sql">The text to lay out.</param>
    /// <param name="tokenize">The dialect's tokenizer (comments included, white space not).</param>
    /// <param name="options">How to lay it out.</param>
    public static string Format(string sql, Func<string, List<LanguageToken>> tokenize, SqlFormatOptions options)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(tokenize);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return sql;
        }

        var parts = Parts(sql, tokenize(sql));
        string formatted;
        try
        {
            formatted = new Layout(sql, parts, options).Run();
        }
        catch (InvalidOperationException)
        {
            return sql;
        }

        // The safety net: the same tokens in the same order (keywords may differ in case only).
        var after = Parts(formatted, tokenize(formatted));
        if (after.Count != parts.Count)
        {
            return sql;
        }

        for (var i = 0; i < parts.Count; i++)
        {
            if (!parts[i].Text.Equals(after[i].Text, parts[i].IsWord ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return sql;
            }
        }

        return formatted;
    }

    /// <summary>The tokens as pieces of text, a string or block comment that spans lines joined back into one.</summary>
    private static List<Part> Parts(string sql, List<LanguageToken> tokens)
    {
        var parts = new List<Part>(tokens.Count);
        var i = 0;
        var last = 0;
        while (i < tokens.Count)
        {
            var token = tokens[i];
            var start = token.Start;
            var end = token.End;
            while (i + 1 < tokens.Count && Continues(sql.AsSpan(start, end - start), token.Kind) && tokens[i + 1].Kind == token.Kind)
            {
                end = tokens[++i].End;
            }

            // Anything the tokenizer skipped that is not white space is kept as a piece of its own.
            var gap = sql.AsSpan(last, start - last);
            if (!gap.IsWhiteSpace())
            {
                throw new InvalidOperationException("Untokenized text.");
            }

            var text = sql[start..end];
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(new Part(token.Kind, start, end, text, sql.AsSpan(last, start - last).Contains('\n')));
            }

            last = end;
            i++;
        }

        return parts;
    }

    private static bool Continues(ReadOnlySpan<char> text, SqlTokenKind kind) => kind switch
    {
        SqlTokenKind.Comment => text.StartsWith("/*", StringComparison.Ordinal) && !text.EndsWith("*/", StringComparison.Ordinal),
        SqlTokenKind.StringLiteral => !IsClosed(text),
        _ => false,
    };

    private static bool IsClosed(ReadOnlySpan<char> text)
    {
        var open = text.IndexOfAny('\'', '"', '$');
        return open < 0 || (text.Length >= open + 2 && text[^1] == text[open]);
    }

    private readonly record struct Part(SqlTokenKind Kind, int Start, int End, string Text, bool NewLineBefore)
    {
        public bool IsWord => Text.Length > 0 && (char.IsLetter(Text[0]) || Text[0] == '_') && Kind != SqlTokenKind.StringLiteral && Kind != SqlTokenKind.Comment
            && Text.All(static c => char.IsLetterOrDigit(c) || c is '_' or '$' or '#');

        public bool IsLineComment => Kind == SqlTokenKind.Comment && Text.StartsWith("--", StringComparison.Ordinal);
    }

    private enum FrameKind
    {
        Query,
        Case,
    }

    private sealed class Frame(FrameKind kind, int depth, int baseIndent)
    {
        public FrameKind Kind { get; } = kind;

        public int Depth { get; } = depth;

        public int Base { get; } = baseIndent;

        public int Content => Base + 1;

        public bool Start { get; set; } = true;

        public bool Between { get; set; }
    }

    private sealed class Layout(string sql, List<Part> parts, SqlFormatOptions options)
    {
        private readonly StringBuilder _out = new(sql.Length + (sql.Length / 4));
        private readonly Stack<Frame> _frames = new();
        private bool _lineStart = true;
        private int _lineIndent;
        private int? _pending;
        private bool _blankPending;
        private int _depth;
        private bool _modifiers;
        private int _topArgument = -1;

        public string Run()
        {
            var i = 0;
            while (i < parts.Count)
            {
                i = _frames.Count == 0 ? Statement(i) : Token(i);
            }

            // Lines end as they did in the text.
            var text = _out.ToString().TrimEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
            if (sql.EndsWith('\n'))
            {
                text += "\n";
            }

            return sql.Contains("\r\n", StringComparison.Ordinal) ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text;
        }

        // ---- statements ----------------------------------------------------------------------------------------

        private int Statement(int i)
        {
            var part = parts[i];
            if (part.Kind == SqlTokenKind.Comment)
            {
                // A comment after a statement on its line stays there; one on its own line opens the next statement.
                if (part.NewLineBefore || _out.Length == 0)
                {
                    Separate();
                    NewLine(0);
                }
                else
                {
                    _pending = null;
                }

                Write(part.Text, part);
                if (part.IsLineComment)
                {
                    _pending = 0;
                }

                return i + 1;
            }

            if (IsSeparator(i))
            {
                _blankPending = false;
                NewLine(0);
                Write(part.Text, part);
                _blankPending = true;
                return i + 1;
            }

            var word = part.IsWord ? part.Text : null;
            if ((word is not null && Formatted.Contains(word)) || (part.Text == "(" && NextWord(i) is { } next && Formatted.Contains(next)))
            {
                Separate();
                NewLine(0);
                _frames.Push(new Frame(FrameKind.Query, 0, 0));
                _depth = 0;
                return Token(i);
            }

            var toBatchEnd = word is not null && (Procedural.Contains(word) || ((word.Equals("CREATE", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("ALTER", StringComparison.OrdinalIgnoreCase)) && Enumerable.Range(i + 1, 4).Any(j => j < parts.Count && parts[j].IsWord && Routine.Contains(parts[j].Text))));
            return Verbatim(i, toBatchEnd);
        }

        /// <summary>Copies a statement as written: to its semicolon (or the next statement), or to the end of the batch.</summary>
        private int Verbatim(int i, bool toBatchEnd)
        {
            var depth = 0;
            var end = i;
            var endsStatement = false;
            for (var j = i; j < parts.Count; j++)
            {
                if (IsSeparator(j) && j > i)
                {
                    break;
                }

                if (!toBatchEnd && j > i && depth == 0 && parts[j].NewLineBefore && parts[j].IsWord && StatementStart.Contains(parts[j].Text))
                {
                    break;
                }

                end = j;
                var text = parts[j].Text;
                depth = Math.Max(0, depth + (text == "(" ? 1 : text == ")" ? -1 : 0));
                if (!toBatchEnd && text == ";" && depth == 0)
                {
                    endsStatement = true;
                    break;
                }
            }

            Separate();
            NewLine(0);
            Write(sql[parts[i].Start..parts[end].End], parts[i]);
            _pending = 0;
            _blankPending = endsStatement || toBatchEnd;
            return end + 1;
        }

        private void EndStatement()
        {
            _frames.Clear();
            _depth = 0;
            _pending = null;
            _modifiers = false;
            _topArgument = -1;
            _blankPending = true;
        }

        // ---- tokens of a query or DML statement ----------------------------------------------------------------

        private int Token(int i)
        {
            var part = parts[i];
            var frame = _frames.Peek();
            var atLevel = frame.Kind == FrameKind.Query && _depth == frame.Depth;

            if (part.Kind == SqlTokenKind.Comment)
            {
                return Comment(i);
            }

            if (IsSeparator(i))
            {
                EndStatement();
                return i;
            }

            // A new statement on its own line ends one written without a semicolon (T-SQL).
            if (_frames.Count == 1 && _depth == 0 && part.NewLineBefore && part.IsWord && StatementStart.Contains(part.Text) && !Formatted.Contains(part.Text) &&
                !part.Text.Equals("SET", StringComparison.OrdinalIgnoreCase))
            {
                EndStatement();
                return i;
            }

            var word = part.IsWord && !AfterDot(i) ? part.Text.ToUpperInvariant() : null;

            // TOP (n) / TOP n and what goes with it stay on the SELECT line.
            if (_topArgument >= 0)
            {
                Hold(() => Write(Case(i), part));
                if (part.Text == "(")
                {
                    _depth++;
                }
                else if (part.Text == ")")
                {
                    _depth--;
                }

                if (_depth == _topArgument)
                {
                    _topArgument = -1;
                }

                return i + 1;
            }

            if (_modifiers && word is not null && (Modifiers.Contains(word) || word is "PERCENT" ||
                (word == "WITH" && NextWord(i) is { } ties && ties.Equals("TIES", StringComparison.OrdinalIgnoreCase)) || word == "TIES"))
            {
                Hold(() => Write(Case(i), part));
                if (word == "TOP")
                {
                    _topArgument = _depth;
                }

                frame.Start = false;
                return i + 1;
            }

            _modifiers = false;

            if (part.Text == ";" && _depth == 0)
            {
                _pending = null;
                Write(";", part);
                EndStatement();
                return i + 1;
            }

            if (atLevel && word is not null)
            {
                if (word == "WITH" && frame.Start)
                {
                    return Block(i, 1);
                }

                if (Match(i, SetOperators) is { } setLength)
                {
                    NewLine(frame.Base);
                    WriteWords(i, setLength);
                    _pending = frame.Base;
                    return i + setLength;
                }

                if (Match(i, BlockClauses) is { } blockLength)
                {
                    return Block(i, blockLength);
                }

                if (Match(i, LineClauses) is { } lineLength)
                {
                    NewLine(frame.Base);
                    WriteWords(i, lineLength);
                    frame.Start = false;
                    return i + lineLength;
                }

                if (word is "AND" or "OR")
                {
                    if (word == "AND" && frame.Between)
                    {
                        frame.Between = false;
                    }
                    else
                    {
                        NewLine(frame.Content);
                    }
                }
                else if (word == "BETWEEN")
                {
                    frame.Between = true;
                }
            }

            if (word == "CASE")
            {
                Write(Case(i), part);
                _frames.Push(new Frame(FrameKind.Case, _depth, _lineIndent));
                return i + 1;
            }

            if (frame.Kind == FrameKind.Case && _depth == frame.Depth && word is "WHEN" or "ELSE" or "END")
            {
                if (word == "END")
                {
                    _frames.Pop();
                    NewLine(frame.Base);
                }
                else
                {
                    NewLine(frame.Content);
                }

                Write(Case(i), part);
                return i + 1;
            }

            if (part.Text == "(")
            {
                Write("(", part);
                _depth++;
                if (NextWord(i) is { } inner && (inner.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || inner.Equals("WITH", StringComparison.OrdinalIgnoreCase)))
                {
                    _frames.Push(new Frame(FrameKind.Query, _depth, _lineIndent + 1));
                }

                frame.Start = false;
                return i + 1;
            }

            if (part.Text == ")")
            {
                if (frame.Kind == FrameKind.Query && _frames.Count > 1 && _depth == frame.Depth)
                {
                    _frames.Pop();
                    _pending = null;
                    NewLine(frame.Base - 1);
                }

                Write(")", part);
                _depth = Math.Max(0, _depth - 1);
                return i + 1;
            }

            if (part.Text == "," && atLevel)
            {
                Write(",", part);
                _pending = frame.Content;
                return i + 1;
            }

            Write(Case(i), part);
            frame.Start = false;
            return i + 1;
        }

        private int Block(int i, int length)
        {
            var frame = _frames.Peek();
            _pending = null;
            NewLine(frame.Base);
            WriteWords(i, length);
            _pending = frame.Content;
            _modifiers = true;
            frame.Start = false;
            frame.Between = false;
            return i + length;
        }

        private int Comment(int i)
        {
            var part = parts[i];
            if (part.NewLineBefore)
            {
                NewLine(_pending ?? _lineIndent);
                _pending = null;
            }

            Write(part.Text, part);
            if (part.IsLineComment)
            {
                _pending ??= _lineIndent;
            }

            return i + 1;
        }

        // ---- reading ---------------------------------------------------------------------------------------------

        private bool IsSeparator(int i)
        {
            var part = parts[i];
            if (!(part.Text.Equals("GO", StringComparison.OrdinalIgnoreCase) || part.Text == "/"))
            {
                return false;
            }

            var lineStart = sql.LastIndexOf('\n', Math.Max(0, part.Start - 1)) + 1;
            var lineEnd = sql.IndexOf('\n', part.End);
            var line = sql.AsSpan(lineStart, (lineEnd < 0 ? sql.Length : lineEnd) - lineStart).Trim();
            return line.Equals(part.Text, StringComparison.OrdinalIgnoreCase) || (part.Text != "/" && line.StartsWith(part.Text + " ", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[(part.Text.Length + 1)..], out _));
        }

        private bool AfterDot(int i) => (i > 0 && parts[i - 1].Text == ".") || (i + 1 < parts.Count && parts[i + 1].Text == ".");

        private string? NextWord(int i)
        {
            for (var j = i + 1; j < parts.Count; j++)
            {
                if (parts[j].Kind != SqlTokenKind.Comment)
                {
                    return parts[j].IsWord ? parts[j].Text : null;
                }
            }

            return null;
        }

        private int? Match(int i, string[][] phrases)
        {
            foreach (var phrase in phrases)
            {
                var ok = true;
                for (var w = 0; w < phrase.Length && ok; w++)
                {
                    var j = i + w;
                    ok = j < parts.Count && parts[j].IsWord && parts[j].Text.Equals(phrase[w], StringComparison.OrdinalIgnoreCase);
                }

                if (ok)
                {
                    return phrase.Length;
                }
            }

            return null;
        }

        private string Case(int i) =>
            options.UppercaseKeywords && parts[i].IsWord && Reserved.Contains(parts[i].Text) && !AfterDot(i) ? parts[i].Text.ToUpperInvariant() : parts[i].Text;

        /// <summary>Writes on the current line, keeping a line break that is due for what follows.</summary>
        private void Hold(Action write)
        {
            var pending = _pending;
            _pending = null;
            write();
            _pending = pending;
        }

        // ---- writing ---------------------------------------------------------------------------------------------

        private void WriteWords(int i, int count)
        {
            for (var w = 0; w < count; w++)
            {
                // Words read as a clause are keywords there, whatever list they are on.
                Write(options.UppercaseKeywords ? parts[i + w].Text.ToUpperInvariant() : parts[i + w].Text, parts[i + w]);
            }
        }

        private void Separate()
        {
            if (_blankPending && _out.Length > 0)
            {
                NewLine(0);
                _out.Append('\n');
            }

            _blankPending = false;
        }

        private void NewLine(int indent)
        {
            _pending = null;
            if (_out.Length > 0 && !_lineStart)
            {
                while (_out.Length > 0 && _out[^1] == ' ')
                {
                    _out.Length--;
                }

                _out.Append('\n');
            }

            _lineStart = true;
            _lineIndent = Math.Max(0, indent);
        }

        /// <summary>Writes one token: on a new line when one is due, otherwise after a space if the text had one there.</summary>
        private void Write(string text, Part part)
        {
            if (_pending is { } indent)
            {
                NewLine(indent);
            }

            if (_lineStart)
            {
                if (_out.Length > 0 || _lineIndent > 0)
                {
                    _out.Append(' ', _lineIndent * options.IndentSize);
                }

                _lineStart = false;
            }
            else if (part.Start > 0 && char.IsWhiteSpace(sql[part.Start - 1]))
            {
                _out.Append(' ');
            }

            _out.Append(text);
        }
    }
}
