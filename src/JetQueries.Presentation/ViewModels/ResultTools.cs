using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace JetQueries.Presentation.ViewModels;

/// <summary>How selected cells are copied besides the plain tab-separated copy.</summary>
public enum ResultCopyFormat
{
    /// <summary>Comma-separated, quoted where needed, with a header line.</summary>
    Csv,

    /// <summary>An array of objects, one per row.</summary>
    Json,

    /// <summary>A Markdown table.</summary>
    Markdown,

    /// <summary>One INSERT per row.</summary>
    SqlInsert,

    /// <summary>The values as <c>(1, 'a', …)</c>, for an IN list.</summary>
    SqlInList,
}

/// <summary>Writes rows in the formats of <see cref="ResultCopyFormat"/>. Numbers and dates are written invariantly.</summary>
public static class ResultCopy
{
    /// <summary>The text.</summary>
    /// <param name="format">The format.</param>
    /// <param name="columns">The result's columns.</param>
    /// <param name="rows">The rows to write, in the order shown.</param>
    /// <param name="shown">The columns to write, in order.</param>
    /// <param name="tableName">The table name in INSERT statements.</param>
    public static string Format(
        ResultCopyFormat format, IReadOnlyList<ResultGridColumn> columns, IReadOnlyList<object?[]> rows, IReadOnlyList<int> shown, string tableName = "table_name")
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(shown);
        var text = new StringBuilder();
        switch (format)
        {
            case ResultCopyFormat.Csv:
                text.AppendJoin(',', shown.Select(c => Csv(columns[c].Header))).Append("\r\n");
                foreach (var row in rows)
                {
                    text.AppendJoin(',', shown.Select(c => row[c] is null ? string.Empty : Csv(QueryResultSet.FormatValue(row[c]) ?? string.Empty))).Append("\r\n");
                }

                break;
            case ResultCopyFormat.Json:
                var array = new JsonArray();
                foreach (var row in rows)
                {
                    var item = new JsonObject();
                    foreach (var c in shown)
                    {
                        item[columns[c].Header] = Json(row[c]);
                    }

                    array.Add(item);
                }

                text.Append(array.ToJsonString(JsonOptions));
                break;
            case ResultCopyFormat.Markdown:
                text.Append("| ").AppendJoin(" | ", shown.Select(c => Markdown(columns[c].Header))).Append(" |\n|");
                text.AppendJoin("|", shown.Select(c => columns[c].IsNumeric ? " ---: " : " --- ")).Append("|\n");
                foreach (var row in rows)
                {
                    text.Append("| ").AppendJoin(" | ", shown.Select(c => Markdown(QueryResultSet.FormatValue(row[c]) ?? "NULL"))).Append(" |\n");
                }

                break;
            case ResultCopyFormat.SqlInsert:
                var names = string.Join(", ", shown.Select(c => columns[c].Header));
                foreach (var row in rows)
                {
                    text.Append("INSERT INTO ").Append(tableName).Append(" (").Append(names).Append(") VALUES (")
                        .AppendJoin(", ", shown.Select(c => Sql(row[c]))).Append(");\n");
                }

                break;
            case ResultCopyFormat.SqlInList:
                var values = rows.SelectMany(row => shown.Select(c => Sql(row[c]))).Where(static v => v != "NULL").Distinct(StringComparer.Ordinal);
                text.Append('(').AppendJoin(", ", values).Append(')');
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        return text.ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Csv(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    private static string Markdown(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ');

    private static JsonValue? Json(object? value) => value switch
    {
        null => null,
        bool flag => JsonValue.Create(flag),
        int or long or short or byte => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        decimal number => JsonValue.Create(number),
        double number when double.IsFinite(number) => JsonValue.Create(number),
        float number when float.IsFinite(number) => JsonValue.Create(number),
        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
        _ => JsonValue.Create(QueryResultSet.FormatValue(value)),
    };

    /// <summary>A SQL literal: numbers as they are, text and dates quoted (quotes doubled), binary as 0x…, NULL.</summary>
    public static string Sql(object? value) => value switch
    {
        null => "NULL",
        bool flag => flag ? "1" : "0",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        int or long or short or byte or decimal or double or float => QueryResultSet.FormatValue(value)!,
        _ => "'" + (QueryResultSet.FormatValue(value) ?? string.Empty).Replace("'", "''", StringComparison.Ordinal) + "'",
    };
}

/// <summary>What the footer says about the selected cells.</summary>
/// <param name="Cells">Cells selected.</param>
/// <param name="Values">Cells that are not NULL.</param>
/// <param name="Numbers">Numeric cells among them.</param>
/// <param name="Sum">Sum of the numbers.</param>
/// <param name="Min">Smallest number.</param>
/// <param name="Max">Largest number.</param>
public sealed record SelectionStats(long Cells, long Values, long Numbers, decimal Sum, decimal Min, decimal Max)
{
    /// <summary>Average of the numbers.</summary>
    public decimal Average => Numbers == 0 ? 0 : Sum / Numbers;

    /// <summary>Counts the cells and, over the numeric ones, sums them (in decimal, so 0.1 + 0.2 is 0.3).</summary>
    /// <param name="rows">The selected rows, in the order shown.</param>
    /// <param name="columns">The selected columns.</param>
    /// <param name="cancellationToken">Stops a long count.</param>
    public static SelectionStats Compute(IReadOnlyList<object?[]> rows, IReadOnlyList<int> columns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(columns);
        long values = 0, numbers = 0;
        decimal sum = 0, min = decimal.MaxValue, max = decimal.MinValue;
        for (var r = 0; r < rows.Count; r++)
        {
            if ((r & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            foreach (var c in columns)
            {
                var value = rows[r][c];
                if (value is null)
                {
                    continue;
                }

                values++;
                decimal number;
                try
                {
                    switch (value)
                    {
                        case int or long or short or byte or decimal:
                            number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                            break;
                        case double d when double.IsFinite(d) && Math.Abs(d) < 7.9e27:
                            number = (decimal)d;
                            break;
                        case float f when float.IsFinite(f):
                            number = (decimal)f;
                            break;
                        default:
                            continue;
                    }

                    sum += number;
                }
                catch (OverflowException)
                {
                    continue;
                }

                numbers++;
                min = Math.Min(min, number);
                max = Math.Max(max, number);
            }
        }

        return new SelectionStats((long)rows.Count * columns.Count, values, numbers, sum, numbers == 0 ? 0 : min, numbers == 0 ? 0 : max);
    }
}

/// <summary>How the value panel shows a value.</summary>
public enum ValuePreviewKind
{
    /// <summary>NULL.</summary>
    Null,

    /// <summary>Plain text.</summary>
    Text,

    /// <summary>JSON, laid out.</summary>
    Json,

    /// <summary>XML, laid out.</summary>
    Xml,

    /// <summary>Binary, as a hex dump.</summary>
    Binary,

    /// <summary>An image (PNG, JPEG, GIF, BMP).</summary>
    Image,
}

/// <summary>A value as the value panel shows it.</summary>
/// <param name="Kind">How to show it.</param>
/// <param name="Text">The text (laid out for JSON and XML; a hex dump for binary).</param>
/// <param name="Bytes">The bytes of an image.</param>
/// <param name="Size">"12 KB", "1,204 characters"…</param>
public sealed record ValuePreview(ValuePreviewKind Kind, string Text, byte[]? Bytes, string Size)
{
    /// <summary>Binary beyond this is not dumped (the size still says how much there is).</summary>
    public const int MaxDump = 64 * 1024;

    /// <summary>Recognizes JSON and XML in text, images in binary.</summary>
    public static ValuePreview Describe(object? value)
    {
        switch (value)
        {
            case null:
                return new(ValuePreviewKind.Null, "NULL", null, string.Empty);
            case byte[] bytes:
                var size = SizeText(bytes.Length);
                return IsImage(bytes) ? new(ValuePreviewKind.Image, string.Empty, bytes, size) : new(ValuePreviewKind.Binary, Dump(bytes), null, size);
            default:
                var text = QueryResultSet.FormatValue(value) ?? string.Empty;
                var length = string.Format(CultureInfo.CurrentCulture, "{0:N0} {1}", text.Length, Resources.Strings.ValueCharacters);
                var trimmed = text.AsSpan().Trim();
                if (trimmed.Length > 1 && trimmed[0] is '{' or '[' && TryJson(text) is { } json)
                {
                    return new(ValuePreviewKind.Json, json, null, length);
                }

                if (trimmed.Length > 1 && trimmed[0] == '<' && TryXml(text) is { } xml)
                {
                    return new(ValuePreviewKind.Xml, xml, null, length);
                }

                return new(ValuePreviewKind.Text, text, null, length);
        }
    }

    private static string? TryJson(string text)
    {
        try
        {
            return JsonNode.Parse(text)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryXml(string text)
    {
        try
        {
            return XDocument.Parse(text).ToString();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static bool IsImage(byte[] bytes) =>
        bytes.AsSpan().StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47]) || bytes.AsSpan().StartsWith((byte[])[0xFF, 0xD8, 0xFF]) ||
        bytes.AsSpan().StartsWith("GIF8"u8) || bytes.AsSpan().StartsWith("BM"u8);

    private static string Dump(byte[] bytes)
    {
        var text = new StringBuilder();
        var count = Math.Min(bytes.Length, MaxDump);
        for (var offset = 0; offset < count; offset += 16)
        {
            var line = bytes.AsSpan(offset, Math.Min(16, count - offset));
            text.Append(offset.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                text.Append(i < line.Length ? line[i].ToString("X2", CultureInfo.InvariantCulture) + " " : "   ");
            }

            text.Append(' ');
            foreach (var b in line)
            {
                text.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            text.Append('\n');
        }

        if (bytes.Length > count)
        {
            text.Append('…');
        }

        return text.ToString();
    }

    private static string SizeText(long length) => length switch
    {
        < 1024 => string.Format(CultureInfo.CurrentCulture, "{0:N0} B", length),
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:N1} KB", length / 1024d),
        _ => string.Format(CultureInfo.CurrentCulture, "{0:N1} MB", length / 1024d / 1024d),
    };
}
