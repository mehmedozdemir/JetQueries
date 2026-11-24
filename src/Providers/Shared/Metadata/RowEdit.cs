using System.Globalization;
using System.Text;

namespace JetQueries.Providers.Metadata;

/// <summary>UPDATE and DELETE of one row by its key, with parameters <c>s{i}</c> and <c>k{i}</c>, for the data editor.</summary>
internal static class RowEdit
{
    /// <summary><c>UPDATE t SET a = @s0 WHERE id = @k0</c>.</summary>
    public static string Update(Func<string, string> quote, char prefix, string table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(keyColumns);
        if (columns.Count == 0 || keyColumns.Count == 0)
        {
            throw new ArgumentException("An update needs columns to set and a key.", nameof(columns));
        }

        var text = new StringBuilder("UPDATE ").Append(table).Append("\nSET ");
        for (var i = 0; i < columns.Count; i++)
        {
            text.Append(i == 0 ? string.Empty : ", ").Append(quote(columns[i])).Append(" = ").Append(prefix).Append(Name('s', i));
        }

        return Where(text, quote, prefix, keyColumns).ToString();
    }

    /// <summary><c>DELETE FROM t WHERE id = @k0</c>.</summary>
    public static string Delete(Func<string, string> quote, char prefix, string table, IReadOnlyList<string> keyColumns)
    {
        ArgumentNullException.ThrowIfNull(keyColumns);
        if (keyColumns.Count == 0)
        {
            throw new ArgumentException("A delete needs a key.", nameof(keyColumns));
        }

        return Where(new StringBuilder("DELETE FROM ").Append(table), quote, prefix, keyColumns).ToString();
    }

    /// <summary>The parameter of a value: <c>s0</c>, <c>k1</c>.</summary>
    public static string Name(char kind, int index) => string.Create(CultureInfo.InvariantCulture, $"{kind}{index}");

    private static StringBuilder Where(StringBuilder text, Func<string, string> quote, char prefix, IReadOnlyList<string> keyColumns)
    {
        text.Append("\nWHERE ");
        for (var i = 0; i < keyColumns.Count; i++)
        {
            text.Append(i == 0 ? string.Empty : " AND ").Append(quote(keyColumns[i])).Append(" = ").Append(prefix).Append(Name('k', i));
        }

        return text;
    }
}
