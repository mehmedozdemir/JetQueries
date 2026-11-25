using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace JetQueries.Presentation.ViewModels;

/// <summary>The colour of the header row in an Excel export.</summary>
public enum ExcelHeaderColor
{
    /// <summary>No fill.</summary>
    None,

    /// <summary>Blue, white text.</summary>
    Blue,

    /// <summary>Green, white text.</summary>
    Green,

    /// <summary>Orange, white text.</summary>
    Orange,

    /// <summary>Light grey, dark text.</summary>
    Gray,

    /// <summary>Dark slate, white text.</summary>
    Dark,
}

/// <summary>How dates are shown in an Excel export.</summary>
public enum ExcelDateFormat
{
    /// <summary>2026-11-25 14:30:00</summary>
    Iso,

    /// <summary>In the regional format of this computer (25.11.2026 14:30:00 in Turkey).</summary>
    Regional,
}

/// <summary>How an exported workbook looks: made for reports that need no touching up in Excel.</summary>
public sealed record ExcelExportOptions
{
    /// <summary>The table styles offered (Excel's own names).</summary>
    public static IReadOnlyList<string> TableStyles { get; } =
    [
        "TableStyleMedium2", "TableStyleMedium9", "TableStyleMedium7", "TableStyleMedium4", "TableStyleMedium15", "TableStyleLight9", "TableStyleLight1",
    ];

    /// <summary>The sheet's name (cleaned of the characters Excel does not allow, at most 31 characters).</summary>
    public string SheetName { get; init; } = "Result";

    /// <summary>A title above the table (bold, larger); none when empty.</summary>
    public string? Title { get; init; }

    /// <summary>The header stays in view while scrolling.</summary>
    public bool FreezeHeader { get; init; } = true;

    /// <summary>The header is bold.</summary>
    public bool BoldHeader { get; init; } = true;

    /// <summary>The header's fill (not used for an Excel table, whose style colours it).</summary>
    public ExcelHeaderColor HeaderColor { get; init; } = ExcelHeaderColor.Blue;

    /// <summary>Filter buttons on the header.</summary>
    public bool AutoFilter { get; init; } = true;

    /// <summary>The rows become an Excel table (sorting, filtering, banded rows, a style).</summary>
    public bool AsTable { get; init; }

    /// <summary>The table's style, one of <see cref="TableStyles"/>.</summary>
    public string TableStyle { get; init; } = "TableStyleMedium2";

    /// <summary>Thin borders around every cell.</summary>
    public bool Borders { get; init; }

    /// <summary>Column widths follow the header and the first rows.</summary>
    public bool AutoFitColumns { get; init; } = true;

    /// <summary>Numbers with thousands separators (1,234,567 / 1.234.567).</summary>
    public bool ThousandsSeparator { get; init; } = true;

    /// <summary>How dates are written.</summary>
    public ExcelDateFormat DateFormat { get; init; } = ExcelDateFormat.Regional;

    /// <summary>A row under the data with the sum of every numeric column (only the rows the filter shows).</summary>
    public bool Totals { get; init; }

    /// <summary>The word in the totals row.</summary>
    public string TotalsLabel { get; init; } = "Total";
}

public static partial class ResultExporter
{
    // Rows read before the sheet is written: they decide column widths and which columns are numbers.
    private const int SampleRows = 500;

    private const int MaxSheetRows = 1_048_576;

    private sealed record ExcelColumn(string Name, int Width, bool Numeric);

    /// <summary>The style indexes of one workbook (its cellXfs).</summary>
    private sealed class ExcelStyles
    {
        private readonly List<string> _formats = [];
        private readonly List<string> _xfs = ["<xf/>"];
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

        public ExcelStyles(ExcelExportOptions options)
        {
            var border = options.Borders ? 1 : 0;
            int Number(string code)
            {
                var id = 164 + _formats.Count;
                _formats.Add("<numFmt numFmtId=\"" + id.ToString(CultureInfo.InvariantCulture) + "\" formatCode=\"" + SecurityElement.Escape(code) + "\"/>");
                return id;
            }

            var regional = options.DateFormat == ExcelDateFormat.Regional;
            var date = Number(regional ? ExcelDate(CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern) : "yyyy-mm-dd");
            var dateTime = Number(regional
                ? ExcelDate(CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern) + " " + ExcelDate(CultureInfo.CurrentCulture.DateTimeFormat.LongTimePattern)
                : "yyyy-mm-dd hh:mm:ss");
            var integer = options.ThousandsSeparator ? 3 : 1; // built-in "#,##0" / "0"
            var fraction = options.ThousandsSeparator ? Number("#,##0.0##########") : 0;

            // Fonts: 0 normal, 1 bold, 2 bold white, 3 title. Fills: 0 none, 1 grey125 (required), 2 header.
            var (fill, light) = HeaderFill(options.AsTable ? ExcelHeaderColor.None : options.HeaderColor);
            HeaderFillXml = fill;
            var headerFont = !options.BoldHeader ? (fill is null || light ? 0 : 4) : (fill is null || light ? 1 : 2);
            Add("text", 0, 0, 0, border);
            Add("date", date, 0, 0, border);
            Add("datetime", dateTime, 0, 0, border);
            Add("integer", integer, 0, 0, border);
            Add("fraction", fraction, 0, 0, border);
            Add("header", 0, headerFont, fill is null ? 0 : 2, border);
            Add("title", 0, 3, 0, 0);
            Add("totalLabel", 0, 1, 0, border);
            Add("totalNumber", options.ThousandsSeparator ? Number("#,##0.##########") : 0, 1, 0, border);
            Borders = options.Borders;
        }

        public string? HeaderFillXml { get; }

        public bool Borders { get; }

        public int this[string name] => _index[name];

        public string Xml() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            (_formats.Count == 0 ? string.Empty : "<numFmts count=\"" + _formats.Count.ToString(CultureInfo.InvariantCulture) + "\">" + string.Concat(_formats) + "</numFmts>") +
            "<fonts count=\"5\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
            "<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"14\"/><name val=\"Calibri\"/></font>" +
            "<font><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"" + (HeaderFillXml is null ? "2" : "3") + "\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
            (HeaderFillXml ?? string.Empty) + "</fills>" +
            "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>" +
            "<border><left style=\"thin\"><color rgb=\"FFBFBFBF\"/></left><right style=\"thin\"><color rgb=\"FFBFBFBF\"/></right><top style=\"thin\"><color rgb=\"FFBFBFBF\"/></top>" +
            "<bottom style=\"thin\"><color rgb=\"FFBFBFBF\"/></bottom><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"" + _xfs.Count.ToString(CultureInfo.InvariantCulture) + "\">" + string.Concat(_xfs) + "</cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";

        private void Add(string name, int format, int font, int fill, int border)
        {
            _index[name] = _xfs.Count;
            _xfs.Add("<xf numFmtId=\"" + format.ToString(CultureInfo.InvariantCulture) + "\" fontId=\"" + font.ToString(CultureInfo.InvariantCulture) +
                "\" fillId=\"" + fill.ToString(CultureInfo.InvariantCulture) + "\" borderId=\"" + border.ToString(CultureInfo.InvariantCulture) + "\" xfId=\"0\"" +
                (format != 0 ? " applyNumberFormat=\"1\"" : string.Empty) + (font != 0 ? " applyFont=\"1\"" : string.Empty) +
                (fill != 0 ? " applyFill=\"1\"" : string.Empty) + (border != 0 ? " applyBorder=\"1\"" : string.Empty) + "/>");
        }

        private static (string? Xml, bool Light) HeaderFill(ExcelHeaderColor color)
        {
            var rgb = color switch
            {
                ExcelHeaderColor.Blue => "FF2A78D6",
                ExcelHeaderColor.Green => "FF2E7D32",
                ExcelHeaderColor.Orange => "FFE8711A",
                ExcelHeaderColor.Gray => "FFE7E9EE",
                ExcelHeaderColor.Dark => "FF33404F",
                _ => null,
            };
            return rgb is null
                ? (null, true)
                : ("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"" + rgb + "\"/><bgColor indexed=\"64\"/></patternFill></fill>", color == ExcelHeaderColor.Gray);
        }
    }

    /// <summary>A .NET date pattern as an Excel number format (months "M" → "m", hours "H" → "h", "tt" → AM/PM).</summary>
    internal static string ExcelDate(string pattern)
    {
        var text = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case 'M':
                    text.Append('m');
                    break;
                case 'H':
                    text.Append('h');
                    break;
                case 't':
                    if (i + 1 < pattern.Length && pattern[i + 1] == 't')
                    {
                        i++;
                    }

                    text.Append("AM/PM");
                    break;
                case 'f' or 'F' or 'K' or 'z' or 'g':
                    break;
                case '\'':
                    var end = pattern.IndexOf('\'', i + 1);
                    end = end < 0 ? pattern.Length : end;
                    text.Append('"').Append(pattern, i + 1, end - i - 1).Append('"');
                    i = end;
                    break;
                case '/' or '.' or ':' or '-' or ' ' or ',':
                    text.Append('\\').Append(c);
                    break;
                default:
                    text.Append(c);
                    break;
            }
        }

        return text.ToString().Trim();
    }

    /// <summary>The sheet name Excel accepts: without []:*?/\, not empty, at most 31 characters.</summary>
    internal static string SheetName(string? name)
    {
        var clean = new string([.. (name ?? string.Empty).Where(static c => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\') && !char.IsControl(c))]).Trim().Trim('\'');
        return clean.Length == 0 ? "Result" : clean.Length > 31 ? clean[..31] : clean;
    }

    /// <summary>The letters of a column (0 → A, 26 → AA).</summary>
    internal static string ColumnLetters(int index)
    {
        var letters = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            letters = (char)('A' + ((n - 1) % 26)) + letters;
        }

        return letters;
    }

    private static async Task<long> WriteExcelAsync(
        Stream stream, ExcelExportOptions options, IReadOnlyList<string> headers, IAsyncEnumerable<object?[]> rows, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var styles = new ExcelStyles(options);
        var sheetName = SheetName(options.SheetName);
        var title = string.IsNullOrWhiteSpace(options.Title) ? null : options.Title.Trim();
        var headerRow = title is null ? 1 : 3;
        var maxRows = MaxSheetRows - headerRow - (options.Totals ? 1 : 0);
        var names = TableNames(headers);

        // The first rows are read before anything is written: widths and numeric columns come from them.
        var enumerator = rows.WithCancellation(cancellationToken).ConfigureAwait(false).GetAsyncEnumerator();
        try
        {
            var sample = new List<object?[]>();
            var more = true;
            while (sample.Count < SampleRows && (more = await enumerator.MoveNextAsync()))
            {
                sample.Add(enumerator.Current);
            }

            var columns = Columns(names, sample, options);
            var last = ColumnLetters(Math.Max(columns.Count, 1) - 1);

            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            long count = 0;
            long written = 0;
            long lastRow;
            string range;
            bool filter, table;
            var sheet = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest);
            var output = await sheet.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (output.ConfigureAwait(false))
            {
                var writer = new StreamWriter(output, new UTF8Encoding(false), 1 << 16);
                await using (writer.ConfigureAwait(false))
                {
                    var head = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
                    head.Append("<sheetViews><sheetView workbookViewId=\"0\"").Append(options.Borders ? " showGridLines=\"0\"" : string.Empty).Append('>');
                    if (options.FreezeHeader)
                    {
                        head.Append("<pane ySplit=\"").Append(headerRow).Append("\" topLeftCell=\"A").Append(headerRow + 1).Append("\" activePane=\"bottomLeft\" state=\"frozen\"/>")
                            .Append("<selection pane=\"bottomLeft\" activeCell=\"A").Append(headerRow + 1).Append("\" sqref=\"A").Append(headerRow + 1).Append("\"/>");
                    }

                    head.Append("</sheetView></sheetViews><sheetFormatPr defaultRowHeight=\"15\"/>");
                    if (options.AutoFitColumns && columns.Count > 0)
                    {
                        head.Append("<cols>");
                        for (var i = 0; i < columns.Count; i++)
                        {
                            head.Append("<col min=\"").Append(i + 1).Append("\" max=\"").Append(i + 1).Append("\" width=\"").Append(columns[i].Width).Append("\" customWidth=\"1\"/>");
                        }

                        head.Append("</cols>");
                    }

                    head.Append("<sheetData>");
                    if (title is not null)
                    {
                        head.Append("<row r=\"1\" ht=\"20\" customHeight=\"1\"><c r=\"A1\" t=\"inlineStr\" s=\"").Append(styles["title"]).Append("\"><is><t xml:space=\"preserve\">")
                            .Append(SecurityElement.Escape(XmlSafe(title))).Append("</t></is></c></row>");
                    }

                    head.Append("<row r=\"").Append(headerRow).Append("\">");
                    foreach (var column in columns)
                    {
                        head.Append("<c t=\"inlineStr\" s=\"").Append(styles["header"]).Append("\"><is><t xml:space=\"preserve\">").Append(SecurityElement.Escape(XmlSafe(column.Name))).Append("</t></is></c>");
                    }

                    await writer.WriteAsync(head.Append("</row>"), cancellationToken).ConfigureAwait(false);

                    async Task Write(object?[] row)
                    {
                        count++;
                        if (written >= maxRows)
                        {
                            return;
                        }

                        var line = new StringBuilder("<row r=\"").Append((headerRow + written + 1).ToString(CultureInfo.InvariantCulture)).Append("\">");
                        foreach (var value in row)
                        {
                            line.Append(Cell(value, styles));
                        }

                        await writer.WriteAsync(line.Append("</row>"), cancellationToken).ConfigureAwait(false);
                        if (++written % 5000 == 0)
                        {
                            progress?.Report(written);
                        }
                    }

                    foreach (var row in sample)
                    {
                        await Write(row).ConfigureAwait(false);
                    }

                    while (more && await enumerator.MoveNextAsync())
                    {
                        await Write(enumerator.Current).ConfigureAwait(false);
                    }

                    lastRow = headerRow + Math.Max(written, 1);
                    var tail = new StringBuilder();
                    if (options.Totals && columns.Any(static c => c.Numeric))
                    {
                        var at = (lastRow + 1).ToString(CultureInfo.InvariantCulture);
                        tail.Append("<row r=\"").Append(at).Append("\">");
                        for (var i = 0; i < columns.Count; i++)
                        {
                            var letter = ColumnLetters(i);
                            if (columns[i].Numeric)
                            {
                                tail.Append("<c r=\"").Append(letter).Append(at).Append("\" s=\"").Append(styles["totalNumber"]).Append("\"><f>SUBTOTAL(109,")
                                    .Append(letter).Append(headerRow + 1).Append(':').Append(letter).Append(lastRow).Append(")</f></c>");
                            }
                            else if (i == 0)
                            {
                                tail.Append("<c r=\"A").Append(at).Append("\" t=\"inlineStr\" s=\"").Append(styles["totalLabel"]).Append("\"><is><t>")
                                    .Append(SecurityElement.Escape(options.TotalsLabel)).Append("</t></is></c>");
                            }
                        }

                        tail.Append("</row>");
                    }

                    tail.Append("</sheetData>");
                    range = "A" + headerRow.ToString(CultureInfo.InvariantCulture) + ":" + last + lastRow.ToString(CultureInfo.InvariantCulture);
                    filter = options.AutoFilter && !options.AsTable && columns.Count > 0;
                    if (filter)
                    {
                        tail.Append("<autoFilter ref=\"").Append(range).Append("\"/>");
                    }

                    tail.Append("<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/>");
                    if (options.AsTable && columns.Count > 0)
                    {
                        tail.Append("<tableParts count=\"1\"><tablePart r:id=\"rId1\"/></tableParts>");
                    }

                    await writer.WriteAsync(tail.Append("</worksheet>"), cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    table = options.AsTable && columns.Count > 0;
                }
            }

            // The other parts, once the sheet's entry is closed (a zip being written has one entry open at a time).
            {
                {
                    if (table)
                    {
                        await Entry(zip, "xl/worksheets/_rels/sheet1.xml.rels",
                            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/table\" Target=\"../tables/table1.xml\"/></Relationships>").ConfigureAwait(false);
                        var style = ExcelExportOptions.TableStyles.Contains(options.TableStyle) ? options.TableStyle : ExcelExportOptions.TableStyles[0];
                        await Entry(zip, "xl/tables/table1.xml",
                            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><table xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" id=\"1\" name=\"Result\" displayName=\"Result\" ref=\"" + range + "\" totalsRowShown=\"0\">" +
                            (options.AutoFilter ? "<autoFilter ref=\"" + range + "\"/>" : string.Empty) +
                            "<tableColumns count=\"" + columns.Count.ToString(CultureInfo.InvariantCulture) + "\">" +
                            string.Concat(columns.Select(static (c, i) => "<tableColumn id=\"" + (i + 1).ToString(CultureInfo.InvariantCulture) + "\" name=\"" + SecurityElement.Escape(XmlSafe(c.Name)) + "\"/>")) +
                            "</tableColumns><tableStyleInfo name=\"" + style + "\" showFirstColumn=\"0\" showLastColumn=\"0\" showRowStripes=\"1\" showColumnStripes=\"0\"/></table>").ConfigureAwait(false);
                    }

                    var quoted = "'" + sheetName.Replace("'", "''", StringComparison.Ordinal) + "'";
                    var absolute = "$A$" + headerRow.ToString(CultureInfo.InvariantCulture) + ":$" + last + "$" + lastRow.ToString(CultureInfo.InvariantCulture);
                    await Entry(zip, "[Content_Types].xml",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                        (table ? "<Override PartName=\"/xl/tables/table1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.table+xml\"/>" : string.Empty) + "</Types>").ConfigureAwait(false);
                    await Entry(zip, "_rels/.rels", """
                        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                        """).ConfigureAwait(false);
                    await Entry(zip, "xl/workbook.xml",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                        "<sheets><sheet name=\"" + SecurityElement.Escape(sheetName) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
                        (filter ? "<definedNames><definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"0\" hidden=\"1\">" + SecurityElement.Escape(quoted + "!" + absolute) + "</definedName></definedNames>" : string.Empty) +
                        "</workbook>").ConfigureAwait(false);
                    await Entry(zip, "xl/_rels/workbook.xml.rels", """
                        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
                        """).ConfigureAwait(false);
                    await Entry(zip, "xl/styles.xml", styles.Xml()).ConfigureAwait(false);
                    progress?.Report(written);
                    return count;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    // Header names a table can carry: not empty, without line breaks, each different (case-insensitively).
    private static List<string> TableNames(IReadOnlyList<string> headers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(headers.Count);
        for (var i = 0; i < headers.Count; i++)
        {
            var name = headers[i].Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
            name = name.Length == 0 ? "Column" + (i + 1).ToString(CultureInfo.InvariantCulture) : name;
            var unique = name;
            for (var n = 2; !seen.Add(unique); n++)
            {
                unique = name + n.ToString(CultureInfo.InvariantCulture);
            }

            names.Add(unique);
        }

        return names;
    }

    private static List<ExcelColumn> Columns(List<string> names, List<object?[]> sample, ExcelExportOptions options)
    {
        var columns = new List<ExcelColumn>(names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            var width = names[i].Length + (options.AutoFilter || options.AsTable ? 4 : 2);
            var numeric = false;
            var other = false;
            foreach (var row in sample)
            {
                var value = i < row.Length ? row[i] : null;
                if (value is null)
                {
                    continue;
                }

                numeric |= IsNumber(value);
                other |= !IsNumber(value);
                var length = value switch
                {
                    DateTime or DateTimeOffset => 19,
                    DateOnly => 10,
                    _ when IsNumber(value) => Text(value).Length + (options.ThousandsSeparator ? Text(value).Length / 3 : 0),
                    _ => Text(value).Split('\n')[0].Length,
                };
                width = Math.Max(width, length + 2);
            }

            columns.Add(new ExcelColumn(names[i], Math.Clamp(width, 6, 60), numeric && !other));
        }

        return columns;
    }

    private static bool IsNumber(object value) => value is decimal or double or float or int or long or short or byte or sbyte or ushort or uint or ulong;

    private static string Cell(object? value, ExcelStyles styles)
    {
        static string Number(string text, int style) => "<c s=\"" + style.ToString(CultureInfo.InvariantCulture) + "\"><v>" + text + "</v></c>";
        var text = styles["text"].ToString(CultureInfo.InvariantCulture);
        return value switch
        {
            null => styles.Borders ? "<c s=\"" + text + "\"/>" : "<c/>",
            bool flag => "<c t=\"b\" s=\"" + text + "\"><v>" + (flag ? "1" : "0") + "</v></c>",
            DateTime date => Number(date.ToOADate().ToString("R", CultureInfo.InvariantCulture), date.TimeOfDay == TimeSpan.Zero ? styles["date"] : styles["datetime"]),
            DateTimeOffset offset => Number(offset.DateTime.ToOADate().ToString("R", CultureInfo.InvariantCulture), styles["datetime"]),
            DateOnly day => Number(day.ToDateTime(TimeOnly.MinValue).ToOADate().ToString("R", CultureInfo.InvariantCulture), styles["date"]),

            // Numbers beyond 15 digits lose precision in Excel: kept as text.
            decimal number when Math.Abs(number) < 1e15m => Number(number.ToString(CultureInfo.InvariantCulture), decimal.IsInteger(number) ? styles["integer"] : styles["fraction"]),
            double number when double.IsFinite(number) => Number(number.ToString("R", CultureInfo.InvariantCulture), Math.Floor(number) == number ? styles["integer"] : styles["fraction"]),
            float number when float.IsFinite(number) => Number(number.ToString("R", CultureInfo.InvariantCulture), MathF.Floor(number) == number ? styles["integer"] : styles["fraction"]),
            int or short or byte or sbyte or ushort or uint => Number(Convert.ToString(value, CultureInfo.InvariantCulture)!, styles["integer"]),
            long number when Math.Abs(number) < 1_000_000_000_000_000 => Number(number.ToString(CultureInfo.InvariantCulture), styles["integer"]),
            _ => "<c t=\"inlineStr\" s=\"" + text + "\"><is><t xml:space=\"preserve\">" + SecurityElement.Escape(XmlSafe(Text(value))) + "</t></is></c>",
        };
    }

    // Characters XML 1.0 cannot carry (control characters) are dropped from text cells.
    private static string XmlSafe(string text) =>
        text.Any(static c => c < 0x20 && c is not ('\t' or '\n' or '\r')) ? new string([.. text.Where(static c => c >= 0x20 || c is '\t' or '\n' or '\r')]) : text;

    private static async Task Entry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        var output = await entry.OpenAsync().ConfigureAwait(false);
        await using (output.ConfigureAwait(false))
        {
            var bytes = new UTF8Encoding(false).GetBytes(content.Trim());
            await output.WriteAsync(bytes).ConfigureAwait(false);
        }
    }
}
