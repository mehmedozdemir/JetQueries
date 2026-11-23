using System.Text;

namespace JetQueries.Presentation.ViewModels;

/// <summary>A piece of SQL typed by its shortcut and Tab (<c>sel</c> + Tab → <c>SELECT * FROM |</c>).</summary>
/// <param name="Shortcut">Letters, digits and _; unique, ignoring case.</param>
/// <param name="Description">What it is, in the snippet list.</param>
/// <param name="Text">The SQL; <c>$0</c> marks where the caret goes (the end without it).</param>
public sealed record SqlSnippet(string Shortcut, string Description, string Text);

/// <summary>The snippets the user keeps (the <c>snippets</c> settings section).</summary>
public sealed record SnippetSettings
{
    /// <summary>The settings section (file) name.</summary>
    public const string Section = "snippets";

    /// <summary>The snippets, in the order listed.</summary>
    public IReadOnlyList<SqlSnippet> Snippets { get; init; } = SqlSnippets.Defaults;
}

/// <summary>The snippets in effect.</summary>
public interface ISnippetService
{
    /// <summary>The snippets, in the order listed.</summary>
    IReadOnlyList<SqlSnippet> Current { get; }

    /// <summary>Puts <paramref name="snippets"/> into effect.</summary>
    void Apply(IReadOnlyList<SqlSnippet> snippets);

    /// <summary>The snippet with this shortcut (ignoring case); null when none.</summary>
    SqlSnippet? Find(string shortcut);
}

/// <summary>Keeps the snippets in memory; loading and saving go through the settings store.</summary>
public sealed class SnippetService : ISnippetService
{
    private IReadOnlyList<SqlSnippet> _current = SqlSnippets.Defaults;

    /// <inheritdoc />
    public IReadOnlyList<SqlSnippet> Current => _current;

    /// <inheritdoc />
    public void Apply(IReadOnlyList<SqlSnippet> snippets)
    {
        ArgumentNullException.ThrowIfNull(snippets);
        _current = [.. snippets.Where(static s => SqlSnippets.IsValidShortcut(s.Shortcut))];
    }

    /// <inheritdoc />
    public SqlSnippet? Find(string shortcut) =>
        _current.FirstOrDefault(s => s.Shortcut.Equals(shortcut, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The built-in snippets and how a snippet is written into the editor.</summary>
public static class SqlSnippets
{
    /// <summary>Snippets every dialect understands; the user can change them in the settings.</summary>
    public static IReadOnlyList<SqlSnippet> Defaults { get; } =
    [
        new("sel", "SELECT * FROM", "SELECT *\nFROM $0"),
        new("selw", "SELECT * FROM … WHERE", "SELECT *\nFROM $0\nWHERE "),
        new("selc", "SELECT COUNT(*) FROM", "SELECT COUNT(*)\nFROM $0"),
        new("ins", "INSERT INTO … VALUES", "INSERT INTO $0 ()\nVALUES ();"),
        new("upd", "UPDATE … SET … WHERE", "UPDATE $0\nSET \nWHERE ;"),
        new("del", "DELETE FROM … WHERE", "DELETE FROM $0\nWHERE ;"),
        new("ij", "INNER JOIN … ON", "INNER JOIN $0 ON "),
        new("lj", "LEFT JOIN … ON", "LEFT JOIN $0 ON "),
        new("gb", "GROUP BY", "GROUP BY $0"),
        new("ob", "ORDER BY", "ORDER BY $0"),
        new("cte", "WITH … AS (…) SELECT", "WITH $0 AS (\n    SELECT *\n    FROM \n)\nSELECT *\nFROM "),
        new("case", "CASE WHEN … END", "CASE\n    WHEN $0 THEN \n    ELSE \nEND"),
        new("ex", "WHERE EXISTS (…)", "WHERE EXISTS (\n    SELECT 1\n    FROM $0\n)"),
    ];

    /// <summary>A shortcut is a word: letters, digits and _, starting with a letter.</summary>
    public static bool IsValidShortcut(string? shortcut) =>
        !string.IsNullOrEmpty(shortcut) && char.IsLetter(shortcut[0]) && shortcut.All(static c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>
    /// The text to insert and where the caret goes in it: every line after the first takes the indentation of the
    /// line it is typed on, line breaks are the document's.
    /// </summary>
    /// <param name="snippet">The snippet's text.</param>
    /// <param name="indent">The indentation of the line it is typed on.</param>
    /// <param name="newLine">The document's line break.</param>
    public static (string Text, int Caret) Expand(string snippet, string indent, string newLine)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        var lines = snippet.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var text = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                text.Append(newLine).Append(indent);
            }

            text.Append(lines[i]);
        }

        var result = text.ToString();
        var caret = result.IndexOf("$0", StringComparison.Ordinal);
        return caret < 0 ? (result, result.Length) : (result.Remove(caret, 2), caret);
    }
}
