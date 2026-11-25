using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace JetQueries.Presentation.ViewModels;

/// <summary>The file formats a result can be exported to.</summary>
public enum ResultExportFormat
{
    /// <summary>Delimited text (comma, semicolon or tab), quoted where needed.</summary>
    Csv,

    /// <summary>An Excel workbook (.xlsx) with one sheet; numbers and dates as such.</summary>
    Excel,

    /// <summary>An array of objects, one per row.</summary>
    Json,

    /// <summary>One INSERT statement per row.</summary>
    SqlInsert,

    /// <summary>A Markdown table.</summary>
    Markdown,

    /// <summary>An HTML page with a table.</summary>
    Html,
}

/// <summary>How a result is written to a file.</summary>
public sealed record ResultExportOptions
{
    public required ResultExportFormat Format { get; init; }

    /// <summary>The CSV separator: ",", ";" or a tab.</summary>
    public string Delimiter { get; init; } = ",";

    /// <summary>Whether CSV starts with the column names.</summary>
    public bool Header { get; init; } = true;

    /// <summary>What a NULL is written as in CSV and Markdown.</summary>
    public string NullText { get; init; } = string.Empty;

    /// <summary>The table in INSERT statements (as written: quote it if it needs quotes).</summary>
    public string TableName { get; init; } = "table_name";

    /// <summary>How an Excel workbook looks (header, filter, table, widths, number and date formats).</summary>
    public ExcelExportOptions Excel { get; init; } = new();

    /// <summary>A byte order mark before UTF-8 text, so that Excel opens Turkish characters in a CSV right.</summary>
    public bool ByteOrderMark { get; init; } = true;

    /// <summary>The usual file extension of the format.</summary>
    public static string Extension(ResultExportFormat format) => format switch
    {
        ResultExportFormat.Csv => ".csv",
        ResultExportFormat.Excel => ".xlsx",
        ResultExportFormat.Json => ".json",
        ResultExportFormat.SqlInsert => ".sql",
        ResultExportFormat.Markdown => ".md",
        _ => ".html",
    };
}

/// <summary>
/// Writes rows to a file as they come (a whole result read again from the server does not have to fit in
/// memory). Numbers and dates are written invariantly; Excel gets real numbers and dates.
/// </summary>
public static partial class ResultExporter
{
    /// <summary>Rows an Excel sheet holds besides its header; more are left out and counted.</summary>
    public const int ExcelMaxRows = 1_048_575;

    /// <summary>Writes the rows; returns how many were written.</summary>
    /// <param name="stream">Where to write (left open).</param>
    /// <param name="options">The format and its options.</param>
    /// <param name="headers">The column names, in order.</param>
    /// <param name="rows">The rows, each with a value per column in the same order.</param>
    /// <param name="progress">Rows written so far, now and then.</param>
    /// <param name="cancellationToken">Stops writing (the file is left incomplete).</param>
    public static async Task<long> WriteAsync(
        Stream stream, ResultExportOptions options, IReadOnlyList<string> headers, IAsyncEnumerable<object?[]> rows, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);
        if (options.Format == ResultExportFormat.Excel)
        {
            return await WriteExcelAsync(stream, options.Excel, headers, rows, progress, cancellationToken).ConfigureAwait(false);
        }

        var writer = new StreamWriter(stream, new UTF8Encoding(options.ByteOrderMark && options.Format == ResultExportFormat.Csv), 1 << 16, leaveOpen: true) { NewLine = "\r\n" };
        await using (writer.ConfigureAwait(false))
        {
            long count = 0;
            await Begin(writer, options, headers).ConfigureAwait(false);
            await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await Row(writer, options, headers, row, count).ConfigureAwait(false);
                if (++count % 5000 == 0)
                {
                    progress?.Report(count);
                }
            }

            await End(writer, options, count).ConfigureAwait(false);
            progress?.Report(count);
            return count;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static async Task Begin(StreamWriter writer, ResultExportOptions options, IReadOnlyList<string> headers)
    {
        switch (options.Format)
        {
            case ResultExportFormat.Csv when options.Header:
                await writer.WriteLineAsync(string.Join(options.Delimiter, headers.Select(h => Delimited(h, options.Delimiter)))).ConfigureAwait(false);
                break;
            case ResultExportFormat.Json:
                await writer.WriteAsync('[').ConfigureAwait(false);
                break;
            case ResultExportFormat.Markdown:
                await writer.WriteLineAsync("| " + string.Join(" | ", headers.Select(Markdown)) + " |").ConfigureAwait(false);
                await writer.WriteLineAsync("|" + string.Concat(headers.Select(static _ => " --- |"))).ConfigureAwait(false);
                break;
            case ResultExportFormat.Html:
                await writer.WriteLineAsync("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>JetQueries</title>").ConfigureAwait(false);
                await writer.WriteLineAsync("<style>body{font-family:Segoe UI,sans-serif;font-size:13px}table{border-collapse:collapse}th,td{border:1px solid #ccc;padding:3px 8px;text-align:left}th{background:#f0f0f0}td.n{text-align:right}td.null{color:#999}</style>").ConfigureAwait(false);
                await writer.WriteLineAsync("</head><body><table><thead><tr>" + string.Concat(headers.Select(static h => "<th>" + Html(h) + "</th>")) + "</tr></thead><tbody>").ConfigureAwait(false);
                break;
        }
    }

    private static async Task Row(StreamWriter writer, ResultExportOptions options, IReadOnlyList<string> headers, object?[] row, long index)
    {
        switch (options.Format)
        {
            case ResultExportFormat.Csv:
                await writer.WriteLineAsync(string.Join(options.Delimiter, row.Select(v => v is null ? options.NullText : Delimited(Text(v), options.Delimiter)))).ConfigureAwait(false);
                break;
            case ResultExportFormat.Json:
                var item = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < headers.Count; i++)
                {
                    item[headers[i]] = JsonValue(row[i]);
                }

                await writer.WriteAsync((index == 0 ? "\n  " : ",\n  ") + JsonSerializer.Serialize(item, JsonOptions)).ConfigureAwait(false);
                break;
            case ResultExportFormat.SqlInsert:
                await writer.WriteLineAsync("INSERT INTO " + options.TableName + " (" + string.Join(", ", headers) + ") VALUES (" + string.Join(", ", row.Select(ResultCopy.Sql)) + ");").ConfigureAwait(false);
                break;
            case ResultExportFormat.Markdown:
                await writer.WriteLineAsync("| " + string.Join(" | ", row.Select(v => Markdown(v is null ? (options.NullText.Length > 0 ? options.NullText : "NULL") : Text(v)))) + " |").ConfigureAwait(false);
                break;
            case ResultExportFormat.Html:
                await writer.WriteLineAsync("<tr>" + string.Concat(row.Select(static v => v is null
                    ? "<td class=\"null\">NULL</td>"
                    : ResultSetView.IsNumber(v) ? "<td class=\"n\">" + Html(Text(v)) + "</td>" : "<td>" + Html(Text(v)) + "</td>")) + "</tr>").ConfigureAwait(false);
                break;
        }
    }

    private static async Task End(StreamWriter writer, ResultExportOptions options, long count)
    {
        switch (options.Format)
        {
            case ResultExportFormat.Json:
                await writer.WriteAsync(count == 0 ? "]\n" : "\n]\n").ConfigureAwait(false);
                break;
            case ResultExportFormat.Html:
                await writer.WriteLineAsync("</tbody></table></body></html>").ConfigureAwait(false);
                break;
        }
    }

    private static string Text(object value) => QueryResultSet.FormatValue(value) ?? string.Empty;

    private static string Delimited(string value, string delimiter) =>
        value.Contains(delimiter, StringComparison.Ordinal) || value.AsSpan().IndexOfAny("\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;

    private static string Markdown(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ');

    private static string Html(string value) => System.Net.WebUtility.HtmlEncode(value).Replace("\n", "<br>", StringComparison.Ordinal);

    private static object? JsonValue(object? value) => value switch
    {
        null => null,
        bool or int or long or short or byte or decimal => value,
        double number when double.IsFinite(number) => number,
        float number when float.IsFinite(number) => number,
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => Text(value),
    };
}
