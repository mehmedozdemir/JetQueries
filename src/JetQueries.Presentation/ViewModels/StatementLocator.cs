using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>
/// Finds the statement around the caret, as DBeaver's "execute statement" (Ctrl+Enter) does: statements end at a
/// semicolon, at an empty line, or at a batch separator line (<c>GO</c>, Oracle's <c>/</c>). Works on the
/// tokens of any dialect's language service, so strings and comments never end a statement. Blocks keep
/// together: a semicolon inside BEGIN … END or CASE … END does not end the statement; a procedure, function,
/// package, trigger or type, and a script starting with DECLARE or a BEGIN block, run to the batch separator.
/// </summary>
public static class StatementLocator
{
    private static readonly HashSet<string> Routines = new(StringComparer.OrdinalIgnoreCase) { "PROCEDURE", "PROC", "FUNCTION", "TRIGGER", "PACKAGE", "TYPE" };

    /// <summary>The span of the statement at <paramref name="offset"/>, trimmed; null when there is none (an empty text).</summary>
    /// <param name="text">The editor's text.</param>
    /// <param name="offset">The caret.</param>
    /// <param name="language">The dialect's tokenizer.</param>
    public static TextSpan? Find(string text, int offset, ISqlLanguageService language)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(language);
        var statements = Split(text, language);
        if (statements.Count == 0)
        {
            return null;
        }

        offset = Math.Clamp(offset, 0, text.Length);

        // The statement the caret is in or ends at ("…;|"); between statements, the one above.
        var found = statements.LastOrDefault(s => s.Start <= offset);
        return found == default ? statements[0] : found;
    }

    /// <summary>All statements of the text, in order, trimmed of space around them.</summary>
    public static IReadOnlyList<TextSpan> Split(string text, ISqlLanguageService language)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(language);
        var result = new List<TextSpan>();
        var start = -1;
        var end = 0;
        var depth = 0;
        var parens = 0;
        var toBatchEnd = false;
        var words = new List<string>();
        var hasCode = false;

        void Touch(int from, int to)
        {
            if (start < 0)
            {
                start = from;
            }

            end = Math.Max(end, to);
        }

        void Close()
        {
            // A run of comments alone is no statement.
            if (start >= 0 && end > start && hasCode)
            {
                result.Add(new TextSpan(start, end - start));
            }

            (start, end, depth, parens, toBatchEnd, hasCode) = (-1, 0, 0, 0, false, false);
            words.Clear();
        }

        var state = 0;
        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var newLine = text.IndexOf('\n', lineStart);
            var lineEnd = newLine < 0 ? text.Length : newLine;
            var line = text.AsSpan(lineStart, lineEnd - lineStart).TrimEnd('\r');
            var startState = state;
            var tokens = language.TokenizeLine(line, state);
            state = tokens.EndState;
            var trimmed = line.Trim();

            if (startState == 0 && (trimmed.Equals("GO", StringComparison.OrdinalIgnoreCase) || trimmed is "/" ||
                (trimmed.StartsWith("GO ", StringComparison.OrdinalIgnoreCase) && int.TryParse(trimmed[3..], out _))))
            {
                // A batch separator ends any statement and belongs to none.
                Close();
            }
            else if (startState == 0 && trimmed.IsEmpty)
            {
                if (!toBatchEnd && depth == 0)
                {
                    Close();
                }
            }
            else
            {
                // Strings, comments and quoted names are opaque; everything else is read character by character.
                var opaque = new List<TextSpan>();
                var comments = new HashSet<TextSpan>();
                foreach (var token in tokens.Tokens)
                {
                    if (token.Kind == SqlTokenKind.Comment)
                    {
                        comments.Add(token.Span);
                    }

                    if (token.Kind is SqlTokenKind.Comment or SqlTokenKind.StringLiteral ||
                        (token.Kind == SqlTokenKind.Identifier && token.Span.Start < line.Length && line[token.Span.Start] is '[' or '"' or '`'))
                    {
                        opaque.Add(token.Span);
                    }
                }

                var i = 0;
                while (i < line.Length)
                {
                    var span = opaque.FirstOrDefault(o => o.Start <= i && i < o.Start + o.Length);
                    if (span.Length > 0)
                    {
                        hasCode |= !comments.Contains(span);
                        Touch(lineStart + i, lineStart + Math.Min(line.Length, span.Start + span.Length));
                        i = span.Start + span.Length;
                        continue;
                    }

                    var c = line[i];
                    if (char.IsWhiteSpace(c))
                    {
                        i++;
                        continue;
                    }

                    if (char.IsLetter(c) || c == '_')
                    {
                        var wordStart = i;
                        while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is '_' or '$' or '#' or '@'))
                        {
                            i++;
                        }

                        hasCode = true;
                        Touch(lineStart + wordStart, lineStart + i);
                        Word(line[wordStart..i].ToString().ToUpperInvariant());
                        continue;
                    }

                    hasCode = true;
                    Touch(lineStart + i, lineStart + i + 1);
                    if (c == '(')
                    {
                        parens++;
                    }
                    else if (c == ')')
                    {
                        parens = Math.Max(0, parens - 1);
                    }
                    else if (c == ';' && !toBatchEnd && depth == 0 && parens == 0)
                    {
                        Close();
                    }

                    i++;
                }
            }

            if (newLine < 0)
            {
                break;
            }

            lineStart = newLine + 1;
        }

        Close();
        return result;

        void Word(string word)
        {
            words.Add(word);
            if (words.Count == 1 && word is "DECLARE")
            {
                toBatchEnd = true;
            }
            else if (words.Count <= 5 && words[0] is "CREATE" or "ALTER" && Routines.Contains(word))
            {
                toBatchEnd = true;
            }

            var previous = words.Count > 1 ? words[^2] : null;
            switch (word)
            {
                case "BEGIN":
                    depth++;
                    if (words.Count == 1)
                    {
                        // An anonymous block; BEGIN TRAN is undone below.
                        toBatchEnd = true;
                    }

                    break;
                case "TRAN" or "TRANSACTION" or "DISTRIBUTED" when previous == "BEGIN":
                    depth = Math.Max(0, depth - 1);
                    if (words.Count == 2)
                    {
                        toBatchEnd = false;
                    }

                    break;
                case "CASE" when previous != "END":
                    depth++;
                    break;
                case "END":
                    depth = Math.Max(0, depth - 1);
                    break;
                case "IF" or "LOOP" when previous == "END":
                    // END IF / END LOOP close what never opened a level.
                    depth++;
                    break;
                default:
                    break;
            }
        }
    }

}
