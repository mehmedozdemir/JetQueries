using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>
/// After a column is chosen from the completion list where a condition or an assignment is written (after WHERE,
/// AND, OR, ON, HAVING, WHEN, NOT, in UPDATE … SET), the editor adds <c> = ''</c> and puts the caret between the
/// quotes, when the column's values are written in quotes (text, dates, times, identifiers). A Unicode text column
/// (nvarchar, nchar, ntext, nvarchar2, nclob) gets <c>N''</c>, so that Turkish characters survive on SQL Server.
/// </summary>
public static class ValueSnippet
{
    private static readonly HashSet<string> ConditionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "AND", "OR", "ON", "HAVING", "WHEN", "NOT", "SET",
    };

    // Clause keywords that end a SET list when looking back from a comma.
    private static readonly HashSet<string> ClauseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SET", "SELECT", "FROM", "WHERE", "VALUES", "INTO", "ORDER", "GROUP", "BY", "HAVING", "JOIN", "ON", "RETURNING", "OUTPUT",
    };

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "STRING", "CITEXT", "NAME", "SYSNAME", "JSON", "JSONB", "XML", "UUID", "UNIQUEIDENTIFIER", "INTERVAL", "INET", "CIDR", "MACADDR",
    };

    /// <summary>
    /// What to insert after a column just put in at <paramref name="start"/>..<paramref name="end"/>, and where
    /// the caret goes within it; null when nothing is added.
    /// </summary>
    /// <param name="text">The editor's text, the column already in it.</param>
    /// <param name="start">Where the column's name starts.</param>
    /// <param name="end">Where it ends (the caret).</param>
    /// <param name="item">The completion that was chosen.</param>
    public static (string Text, int Caret)? After(string text, int start, int end, CompletionItem item)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind != CompletionItemKind.Column || DataTypeOf(item.Detail) is not { } type || Quoting(type) is not { } quote)
        {
            return null;
        }

        // Only at the end of what is written: "name = 'x'" or "name)" already have their comparison.
        var rest = end;
        while (rest < text.Length && text[rest] is ' ' or '\t')
        {
            rest++;
        }

        if (rest < text.Length && text[rest] is not ('\r' or '\n' or ')' or ';'))
        {
            return null;
        }

        return InCondition(text, start) ? (" = " + quote + "''", 3 + quote.Length + 1) : null;
    }

    /// <summary>The data type of a column completion's detail ("nvarchar(50) · c", "int · PK").</summary>
    public static string? DataTypeOf(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var dot = detail.IndexOf(" · ", StringComparison.Ordinal);
        return (dot < 0 ? detail : detail[..dot]).Trim();
    }

    /// <summary>"" for a quoted type, "N" for a Unicode text one; null when values are not quoted (numbers, bits, binary).</summary>
    public static string? Quoting(string dataType)
    {
        ArgumentNullException.ThrowIfNull(dataType);
        var type = dataType.Trim().ToUpperInvariant();
        var paren = type.IndexOf('(', StringComparison.Ordinal);
        var name = (paren < 0 ? type : type[..paren]).Trim();
        if (name is "NVARCHAR" or "NCHAR" or "NTEXT" or "NVARCHAR2" or "NCLOB" or "NATIONAL CHARACTER" or "NATIONAL CHARACTER VARYING")
        {
            return "N";
        }

        if (name.Contains("CHAR", StringComparison.Ordinal) || name.Contains("TEXT", StringComparison.Ordinal) || name.Contains("CLOB", StringComparison.Ordinal) ||
            TextTypes.Contains(name))
        {
            return string.Empty;
        }

        return ImportValues.KindOf(type) is ImportValues.Kind.Moment or ImportValues.Kind.MomentWithZone or ImportValues.Kind.TimeOfDay or ImportValues.Kind.Identifier
            ? string.Empty
            : null;
    }

    // Before the column (and its qualifier): a condition keyword, "(" opening one, or "," in a SET list.
    private static bool InCondition(string text, int start)
    {
        var words = Words(text[..start]);
        var i = words.Count - 1;

        // c.Name, dbo.Cards.Name: the qualifier is part of the column.
        while (i >= 1 && words[i].Text == "." && words[i - 1].IsName)
        {
            i -= 2;
        }

        while (i >= 0 && words[i].Text == "(")
        {
            i--;
        }

        if (i < 0)
        {
            return false;
        }

        var previous = words[i];
        if (previous.IsWord && ConditionWords.Contains(previous.Text))
        {
            return true;
        }

        if (previous.Text != ",")
        {
            return false;
        }

        // "SET a = 1, b": the nearest clause keyword at the same depth is SET.
        var depth = previous.Depth;
        for (var j = i - 1; j >= 0; j--)
        {
            if (words[j].IsWord && words[j].Depth == depth && ClauseWords.Contains(words[j].Text))
            {
                return words[j].Text.Equals("SET", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private readonly record struct Word(string Text, bool IsWord, bool IsName, int Depth);

    // Words and punctuation, without strings and comments.
    private static List<Word> Words(string text)
    {
        var words = new List<Word>();
        var depth = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && next == '-')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end;
            }
            else if (c == '/' && next == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
            }
            else if (c == '\'')
            {
                i++;
                while (i < text.Length && (text[i] != '\'' || (i + 1 < text.Length && text[i + 1] == '\'' && (i += 1) > 0)))
                {
                    i++;
                }

                i++;
                words.Add(new Word("''", false, false, depth));
            }
            else if (c is '"' or '[' or '`')
            {
                var close = c == '[' ? ']' : c;
                var end = text.IndexOf(close, i + 1);
                var stop = end < 0 ? text.Length : end + 1;
                words.Add(new Word(text[i..stop], false, true, depth));
                i = stop;
            }
            else if (char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$')
            {
                var begin = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '@' or '#' or '$'))
                {
                    i++;
                }

                words.Add(new Word(text[begin..i], true, true, depth));
            }
            else
            {
                if (c == ')')
                {
                    depth = Math.Max(0, depth - 1);
                }

                words.Add(new Word(c.ToString(), false, false, depth));
                if (c == '(')
                {
                    depth++;
                }

                i++;
            }
        }

        return words;
    }
}

/// <summary>What the editor shows when the mouse rests on a table, view or column.</summary>
/// <param name="Title">The object (<c>dbo.Cards</c>) or the column (<c>CardNo</c>).</param>
/// <param name="Kind">Table, view, column, procedure…</param>
/// <param name="Subtitle">A column's type and table; a table's column count.</param>
public sealed record SqlHoverInfo(string Title, string Kind, string? Subtitle)
{
    /// <summary>A table's or view's columns (at most <see cref="MaxColumns"/>).</summary>
    public IReadOnlyList<ColumnInfo> Columns { get; init; } = [];

    /// <summary>Columns not listed.</summary>
    public int MoreColumns { get; init; }

    /// <summary>The column pointed at, for its flags (primary key, nullable).</summary>
    public ColumnInfo? Column { get; init; }

    /// <summary>The most columns a table's card lists.</summary>
    public const int MaxColumns = 20;
}
