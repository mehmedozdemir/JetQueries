namespace JetQueries.Provider.Sqlite;

/// <summary>A CHECK constraint as written in a CREATE TABLE text.</summary>
/// <param name="Name">The CONSTRAINT name, if one was given.</param>
/// <param name="Column">The column it is declared on; null for a table constraint.</param>
/// <param name="Condition">The expression inside CHECK ( … ), as written.</param>
internal sealed record SqliteCheck(string? Name, string? Column, string Condition);

/// <summary>
/// Finds the CHECK constraints of a table in its CREATE TABLE text (SQLite keeps no catalog of them): column
/// constraints (<c>age INTEGER CHECK (age &gt;= 0)</c>) and table constraints (<c>CONSTRAINT ck CHECK (a &lt; b)</c>).
/// Quotes, brackets and comments are skipped; nothing is evaluated.
/// </summary>
internal static class SqliteCheckConstraints
{
    public static IReadOnlyList<SqliteCheck> Parse(string? createTable)
    {
        if (string.IsNullOrEmpty(createTable))
        {
            return [];
        }

        // The definitions are inside the first top-level parenthesis (after CREATE TABLE name).
        var open = NextTopLevel(createTable, 0, '(');
        if (open < 0)
        {
            return [];
        }

        var close = Matching(createTable, open);
        var checks = new List<SqliteCheck>();
        foreach (var item in SplitTopLevel(createTable, open + 1, close))
        {
            var words = Words(item);
            if (words.Count == 0)
            {
                continue;
            }

            var isTableConstraint = words[0].Text.ToUpperInvariant() is "CONSTRAINT" or "CHECK" or "PRIMARY" or "UNIQUE" or "FOREIGN";
            var column = isTableConstraint ? null : Unquote(words[0].Text);
            for (var i = 0; i < words.Count; i++)
            {
                if (!words[i].Text.Equals("CHECK", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var paren = NextTopLevel(item, words[i].End, '(');
                if (paren < 0)
                {
                    continue;
                }

                // CONSTRAINT name right before CHECK names it.
                var name = i >= 2 && words[i - 2].Text.Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase) ? Unquote(words[i - 1].Text) : null;
                var end = Matching(item, paren);
                checks.Add(new SqliteCheck(name, column, item[(paren + 1)..Math.Max(paren + 1, end)].Trim()));
            }
        }

        return checks;
    }

    private readonly record struct Word(string Text, int End);

    // The words at nesting depth 0 (names, keywords, quoted names), with where each ends.
    private static List<Word> Words(string text)
    {
        var words = new List<Word>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '(')
            {
                i = Matching(text, i) + 1;
            }
            else if (c is '"' or '`' or '[')
            {
                var end = SkipQuoted(text, i);
                words.Add(new Word(text[i..end], end));
                i = end;
            }
            else if (c == '\'')
            {
                i = SkipQuoted(text, i);
            }
            else if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                var line = text.IndexOf('\n', i);
                i = line < 0 ? text.Length : line + 1;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
            }
            else if (char.IsLetterOrDigit(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$'))
                {
                    i++;
                }

                words.Add(new Word(text[start..i], i));
            }
            else
            {
                i++;
            }
        }

        return words;
    }

    private static IEnumerable<string> SplitTopLevel(string text, int start, int end)
    {
        var from = start;
        var i = start;
        while (i < end)
        {
            var c = text[i];
            if (c == '(')
            {
                i = Matching(text, i) + 1;
                continue;
            }

            if (c is '\'' or '"' or '`' or '[')
            {
                i = SkipQuoted(text, i);
                continue;
            }

            if (c == '-' && i + 1 < end && text[i + 1] == '-')
            {
                var line = text.IndexOf('\n', i);
                i = line < 0 ? end : line + 1;
                continue;
            }

            if (c == '/' && i + 1 < end && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? end : close + 2;
                continue;
            }

            if (c == ',')
            {
                yield return text[from..i];
                from = i + 1;
            }

            i++;
        }

        yield return text[from..Math.Min(end, text.Length)];
    }

    // The next character at nesting depth 0, skipping quotes and comments; -1 when there is none.
    private static int NextTopLevel(string text, int start, char wanted)
    {
        var i = start;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == wanted)
            {
                return i;
            }

            if (c is '\'' or '"' or '`' or '[')
            {
                i = SkipQuoted(text, i);
            }
            else if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                var line = text.IndexOf('\n', i);
                i = line < 0 ? text.Length : line + 1;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
            }
            else
            {
                i++;
            }
        }

        return -1;
    }

    // The index of the parenthesis that closes the one at "open" (text length when it is never closed).
    private static int Matching(string text, int open)
    {
        var depth = 0;
        var i = open;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '\'' or '"' or '`' or '[')
            {
                i = SkipQuoted(text, i);
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return i;
            }

            i++;
        }

        return text.Length;
    }

    // After a quoted run starting at "start": '…' and "…" double their quote to escape it; [ … ] ends at ].
    private static int SkipQuoted(string text, int start)
    {
        var closing = text[start] == '[' ? ']' : text[start];
        var i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == closing)
            {
                if (closing != ']' && i + 1 < text.Length && text[i + 1] == closing)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return text.Length;
    }

    private static string Unquote(string name) =>
        name.Length >= 2 && name[0] is '"' or '`' or '[' ? name[1..^1].Replace(name[0] == '"' ? "\"\"" : "``", name[0] == '"' ? "\"" : "`", StringComparison.Ordinal) : name;
}
