using System.Globalization;
using System.Text;

namespace JetQueries.Presentation.ViewModels;

/// <summary>How a result set is shown (DBeaver's Grid / Text / Record).</summary>
public enum ResultViewMode
{
    /// <summary>The virtual grid.</summary>
    Grid,

    /// <summary>A plain-text table with aligned columns, to read or copy.</summary>
    Text,

    /// <summary>One row at a time, its columns listed downwards.</summary>
    Record,
}

/// <summary>One column of the row shown in the record view.</summary>
/// <param name="Name">The column name.</param>
/// <param name="Value">The value as the grid shows it; empty for NULL.</param>
/// <param name="DataType">The database's type name.</param>
/// <param name="IsNull">The value is NULL.</param>
public sealed record ResultRecordField(string Name, string Value, string DataType, bool IsNull);

/// <summary>Writes rows as a plain-text table, as DBeaver's text view does.</summary>
public static class ResultTextFormatter
{
    /// <summary>Most rows written to the text view; the rest are mentioned in a last line.</summary>
    public const int MaxRows = 100_000;

    /// <summary>Widest a column becomes; longer values are cut with "…".</summary>
    public const int MaxColumnWidth = 80;

    /// <summary>What a NULL looks like in the text view.</summary>
    public const string NullText = "[NULL]";

    /// <summary>The table: a header, a line of dashes, then one line per row; numbers aligned right.</summary>
    /// <param name="columns">The result's columns.</param>
    /// <param name="rows">The rows in the order shown.</param>
    /// <param name="visible">The columns to write, in order; null for all.</param>
    /// <param name="cancellationToken">Stops a long build.</param>
    public static string Format(
        IReadOnlyList<ResultGridColumn> columns, IReadOnlyList<object?[]> rows, IReadOnlyList<int>? visible, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        var shown = visible ?? [.. Enumerable.Range(0, columns.Count)];
        var count = Math.Min(rows.Count, MaxRows);

        var cells = new string[count][];
        var widths = shown.Select(c => Math.Min(MaxColumnWidth, columns[c].Header.Length)).ToArray();
        for (var r = 0; r < count; r++)
        {
            if ((r & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var line = new string[shown.Count];
            for (var i = 0; i < shown.Count; i++)
            {
                line[i] = Cell(rows[r][shown[i]]);
                widths[i] = Math.Max(widths[i], Math.Min(MaxColumnWidth, line[i].Length));
            }

            cells[r] = line;
        }

        var text = new StringBuilder((count + 3) * (widths.Sum() + (shown.Count * 3)));
        AppendLine(text, [.. shown.Select(c => columns[c].Header)], widths, shown.Select(_ => false).ToArray());
        text.AppendJoin("-+-", widths.Select(static w => new string('-', w))).Append('\n');
        var right = shown.Select(c => columns[c].IsNumeric).ToArray();
        foreach (var line in cells)
        {
            AppendLine(text, line, widths, right);
        }

        if (rows.Count > count)
        {
            text.Append(CultureInfo.CurrentCulture, $"… {rows.Count - count:N0}\n");
        }

        return text.ToString();
    }

    private static string Cell(object? value)
    {
        var text = QueryResultSet.FormatValue(value);
        if (text is null)
        {
            return NullText;
        }

        // A value spanning lines would break the table: its line breaks are shown as a space.
        return text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal)
            ? text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ')
            : text;
    }

    private static void AppendLine(StringBuilder text, string[] values, int[] widths, bool[] right)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                text.Append(" | ");
            }

            var value = values[i].Length > widths[i] ? string.Concat(values[i].AsSpan(0, widths[i] - 1), "…") : values[i];
            var last = i == values.Length - 1;
            if (right[i])
            {
                text.Append(value.PadLeft(widths[i]));
            }
            else
            {
                text.Append(last ? value : value.PadRight(widths[i]));
            }
        }

        text.Append('\n');
    }
}
