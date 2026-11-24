using System.Globalization;
using System.Text;

namespace JetQueries.Presentation.ViewModels;

/// <summary>
/// Reads delimited text (CSV) as RFC 4180 describes it: fields in double quotes may hold the separator, line breaks
/// and doubled quotes. The separator can be told from the first line.
/// </summary>
public static class CsvReader
{
    /// <summary>The separator the first line uses most, outside quotes: comma, semicolon, tab or pipe.</summary>
    public static char DetectDelimiter(string firstLine)
    {
        ArgumentNullException.ThrowIfNull(firstLine);
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0, ['|'] = 0 };
        var quoted = false;
        foreach (var c in firstLine)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && counts.TryGetValue(c, out var seen))
            {
                counts[c] = seen + 1;
            }
        }

        var best = counts.MaxBy(static p => p.Value);
        return best.Value == 0 ? ',' : best.Key;
    }

    /// <summary>The records of the text, one string array each, in order.</summary>
    public static IEnumerable<string[]> Read(TextReader reader, char delimiter)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var any = false;
        int read;
        while ((read = reader.Read()) >= 0)
        {
            var c = (char)read;
            any = true;
            if (quoted)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && reader.Peek() == '\n')
                {
                    reader.Read();
                }

                fields.Add(field.ToString());
                field.Clear();
                if (fields is not [""])
                {
                    yield return [.. fields];
                }

                fields.Clear();
                any = false;
            }
            else
            {
                field.Append(c);
            }
        }

        if (any)
        {
            fields.Add(field.ToString());
            if (fields is not [""])
            {
                yield return [.. fields];
            }
        }
    }
}

/// <summary>
/// Turns the text of a CSV field into a value of the target column's type, so that every database receives a
/// typed parameter (no implicit conversion that depends on the session's language). Numbers and dates are read
/// invariantly first, then in the user's culture (1.250,50; 15.01.2026).
/// </summary>
public static class ImportValues
{
    /// <summary>The kind of value a column type takes.</summary>
    public enum Kind
    {
        Text,
        Whole,
        Exact,
        Approximate,
        Flag,
        Moment,
        MomentWithZone,
        TimeOfDay,
        Identifier,
        Bytes,
    }

    /// <summary>The kind for a database type name ("nvarchar(50)", "NUMBER(10,2)", "timestamp with time zone").</summary>
    public static Kind KindOf(string? dataType)
    {
        var type = (dataType ?? string.Empty).Trim().ToUpperInvariant();
        var paren = type.IndexOf('(', StringComparison.Ordinal);
        var name = (paren < 0 ? type : type[..paren]).Trim();
        if (type.Contains("WITH TIME ZONE", StringComparison.Ordinal) || name is "DATETIMEOFFSET" or "TIMESTAMPTZ")
        {
            return Kind.MomentWithZone;
        }

        return name switch
        {
            "INT" or "INTEGER" or "BIGINT" or "SMALLINT" or "TINYINT" or "INT2" or "INT4" or "INT8" or "SERIAL" or "BIGSERIAL" or "SMALLSERIAL" => Kind.Whole,
            "DECIMAL" or "NUMERIC" or "NUMBER" or "MONEY" or "SMALLMONEY" => Kind.Exact,
            "FLOAT" or "REAL" or "DOUBLE" or "DOUBLE PRECISION" or "FLOAT4" or "FLOAT8" or "BINARY_FLOAT" or "BINARY_DOUBLE" => Kind.Approximate,
            "BIT" or "BOOL" or "BOOLEAN" => Kind.Flag,
            "DATE" or "DATETIME" or "DATETIME2" or "SMALLDATETIME" or "TIMESTAMP" or "TIMESTAMP WITHOUT TIME ZONE" => Kind.Moment,
            "TIME" => Kind.TimeOfDay,
            "UNIQUEIDENTIFIER" or "UUID" => Kind.Identifier,
            "BINARY" or "VARBINARY" or "BYTEA" or "BLOB" or "RAW" or "IMAGE" => Kind.Bytes,
            _ => type.StartsWith("TIMESTAMP", StringComparison.Ordinal) ? Kind.Moment : Kind.Text,
        };
    }

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "dd.MM.yyyy", "dd.MM.yyyy HH:mm", "dd.MM.yyyy HH:mm:ss", "d.M.yyyy", "d.M.yyyy HH:mm:ss", "dd/MM/yyyy", "dd/MM/yyyy HH:mm:ss",
    ];

    /// <summary>The value; an error message when the text is not of the kind.</summary>
    /// <param name="text">The field.</param>
    /// <param name="kind">The column's kind.</param>
    /// <param name="emptyIsNull">An empty field is NULL (else an empty string, or NULL for non-text kinds).</param>
    public static (object? Value, string? Error) Convert(string text, Kind kind, bool emptyIsNull)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 && (emptyIsNull || kind != Kind.Text))
        {
            return (null, null);
        }

        var value = text.Trim();
        var culture = CultureInfo.CurrentCulture;
        switch (kind)
        {
            case Kind.Text:
                return (text, null);
            case Kind.Whole:
                return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) ||
                       long.TryParse(value, NumberStyles.Integer | NumberStyles.AllowThousands, culture, out integer)
                    ? (integer, null)
                    : (null, "integer");
            case Kind.Exact:
                return decimal.TryParse(value, NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var number) ||
                       decimal.TryParse(value, NumberStyles.Number, culture, out number)
                    ? (number, null)
                    : (null, "number");
            case Kind.Approximate:
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ||
                       double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, culture, out real)
                    ? (real, null)
                    : (null, "number");
            case Kind.Flag:
                // Turkish dotless and dotted i first: the invariant upper case does not map them (hayır, EVET, İ…).
                return value.Replace('ı', 'i').Replace('İ', 'I').ToUpperInvariant() switch
                {
                    "1" or "TRUE" or "T" or "Y" or "YES" or "EVET" or "E" => (true, null),
                    "0" or "FALSE" or "F" or "N" or "NO" or "HAYIR" or "H" => (false, null),
                    _ => (null, "boolean"),
                };
            case Kind.Moment:
                return DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date) ||
                       DateTime.TryParse(value, culture, DateTimeStyles.AllowWhiteSpaces, out date)
                    ? (date, null)
                    : (null, "date");
            case Kind.MomentWithZone:
                return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var offset) ||
                       DateTimeOffset.TryParse(value, culture, DateTimeStyles.AssumeLocal, out offset)
                    ? (offset, null)
                    : (null, "date");
            case Kind.TimeOfDay:
                return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time) ? (time, null) : (null, "time");
            case Kind.Identifier:
                return Guid.TryParse(value, out var guid) ? (guid, null) : (null, "GUID");
            case Kind.Bytes:
                try
                {
                    return (System.Convert.FromHexString(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value), null);
                }
                catch (FormatException)
                {
                    return (null, "hex");
                }

            default:
                return (text, null);
        }
    }
}
