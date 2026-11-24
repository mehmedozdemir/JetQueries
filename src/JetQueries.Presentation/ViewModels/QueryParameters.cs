using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using JetQueries.Presentation.Resources;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>
/// The placeholders of a parameterized query (DBeaver's): <c>:name</c> is a value bound as a parameter (never
/// written into the text), <c>${name}</c> is text put into the SQL as typed (a table name, a column list). Those in
/// strings, quoted names and comments do not count, nor PostgreSQL casts (<c>::int</c>), Oracle assignments
/// (<c>:=</c>) or trigger rows (<c>:new.x</c>, <c>:old.x</c>).
/// </summary>
public static class QueryPlaceholders
{
    /// <summary>A placeholder in the text.</summary>
    /// <param name="Name">The name, without <c>:</c> or <c>${}</c>.</param>
    /// <param name="Start">Where it starts.</param>
    /// <param name="Length">Its length in the text.</param>
    /// <param name="IsText">A <c>${name}</c> text placeholder.</param>
    public sealed record Placeholder(string Name, int Start, int Length, bool IsText);

    /// <summary>The placeholders of <paramref name="sql"/>, in order.</summary>
    public static IReadOnlyList<Placeholder> Find(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var found = new List<Placeholder>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (c == '-' && next == '-')
            {
                i = End(sql, sql.IndexOf('\n', i));
            }
            else if (c == '/' && next == '*')
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + 2;
            }
            else if (c == '\'')
            {
                i = Quoted(sql, i, '\'');
            }
            else if (c is '"' or '`')
            {
                i = Quoted(sql, i, c);
            }
            else if (c == '[')
            {
                var close = sql.IndexOf(']', i + 1);
                i = close < 0 ? sql.Length : close + 1;
            }
            else if (c == '$' && DollarTag(sql, i) is { } tag)
            {
                // PostgreSQL dollar quoting: $$ … $$ or $body$ … $body$ (a function's text).
                var close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + tag.Length;
            }
            else if (c == '$' && next == '{')
            {
                var close = sql.IndexOf('}', i + 2);
                var name = close < 0 ? string.Empty : sql[(i + 2)..close].Trim();
                if (name.Length > 0 && name.All(static ch => char.IsLetterOrDigit(ch) || ch is '_' or '.'))
                {
                    found.Add(new Placeholder(name, i, close - i + 1, IsText: true));
                    i = close + 1;
                }
                else
                {
                    i += 2;
                }
            }
            else if (c == ':' && (char.IsLetter(next) || next == '_') && (i == 0 || !IsNamePart(sql[i - 1]) && sql[i - 1] != ':'))
            {
                var end = i + 1;
                while (end < sql.Length && IsNamePart(sql[end]))
                {
                    end++;
                }

                var name = sql[(i + 1)..end];
                var isRow = end < sql.Length && sql[end] == '.' &&
                    (name.Equals("new", StringComparison.OrdinalIgnoreCase) || name.Equals("old", StringComparison.OrdinalIgnoreCase));
                if (!isRow)
                {
                    found.Add(new Placeholder(name, i, end - i, IsText: false));
                }

                i = end;
            }
            else if (IsNamePart(c))
            {
                // A word: a colon right after it (a label, a time in a name) is not a placeholder.
                while (i < sql.Length && IsNamePart(sql[i]))
                {
                    i++;
                }
            }
            else
            {
                i++;
            }
        }

        return found;
    }

    /// <summary>
    /// The text with value placeholders written as the provider's parameter references and text placeholders
    /// replaced by their text.
    /// </summary>
    public static string Apply(string sql, IReadOnlyList<Placeholder> placeholders, Func<string, string> reference, IReadOnlyDictionary<string, string> texts)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(placeholders);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(texts);
        var text = new StringBuilder(sql.Length + 16);
        var at = 0;
        foreach (var placeholder in placeholders.OrderBy(static p => p.Start))
        {
            text.Append(sql, at, placeholder.Start - at);
            text.Append(placeholder.IsText ? texts.GetValueOrDefault(placeholder.Name, string.Empty) : reference(placeholder.Name));
            at = placeholder.Start + placeholder.Length;
        }

        return text.Append(sql, at, sql.Length - at).ToString();
    }

    private static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    private static int End(string sql, int index) => index < 0 ? sql.Length : index;

    private static int Quoted(string sql, int start, char quote)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return sql.Length;
    }

    private static string? DollarTag(string sql, int start)
    {
        var end = start + 1;
        while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_'))
        {
            end++;
        }

        return end < sql.Length && sql[end] == '$' && (end == start + 1 || !char.IsDigit(sql[start + 1])) ? sql[start..(end + 1)] : null;
    }
}

/// <summary>How a value typed for a parameter is sent.</summary>
public enum QueryParameterKind
{
    /// <summary>A whole or decimal number when it reads as one, else text.</summary>
    Auto,
    Text,
    Number,
    Date,
    Null,
}

/// <summary>One parameter of the parameters dialog.</summary>
public sealed partial class QueryParameterItem : ObservableObject
{
    public QueryParameterItem(string name, bool isText)
    {
        Name = name;
        IsText = isText;
        Value = string.Empty;
    }

    /// <summary>The name in the query.</summary>
    public string Name { get; }

    /// <summary>A <c>${name}</c>: the text goes into the SQL as typed.</summary>
    public bool IsText { get; }

    /// <summary>As written in the query: <c>:name</c> or <c>${name}</c>.</summary>
    public string Label => IsText ? "${" + Name + "}" : ":" + Name;

    /// <summary>The value typed.</summary>
    [ObservableProperty]
    public partial string Value { get; set; }

    /// <summary>How it is sent (value placeholders only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValueEnabled))]
    public partial QueryParameterKind Kind { get; set; }

    /// <summary>NULL takes no value.</summary>
    public bool IsValueEnabled => Kind != QueryParameterKind.Null;
}

/// <summary>
/// The parameters dialog shown before a parameterized query runs: one line per placeholder, with the values given
/// last time (in this window) filled in.
/// </summary>
public sealed partial class QueryParametersViewModel : ObservableObject
{
    private static readonly CompositeFormat BadValue = CompositeFormat.Parse(Strings.ParametersBadValue);

    public QueryParametersViewModel(IEnumerable<QueryParameterItem> items)
    {
        Items = [.. items];
    }

    /// <summary>The parameters, each once, in the order they first appear.</summary>
    public ObservableCollection<QueryParameterItem> Items { get; }

    /// <summary>The kinds offered, with their captions.</summary>
    public IReadOnlyList<SettingChoice<QueryParameterKind>> Kinds { get; } =
    [
        new(QueryParameterKind.Auto, Strings.ParameterAuto),
        new(QueryParameterKind.Text, Strings.ParameterText),
        new(QueryParameterKind.Number, Strings.ParameterNumber),
        new(QueryParameterKind.Date, Strings.ParameterDate),
        new(QueryParameterKind.Null, "NULL"),
    ];

    /// <summary>Why the values cannot be used; null when they can.</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>The values as parameters and texts; false (and <see cref="Error"/>) when one does not read as its kind.</summary>
    public bool TryBuild(out IReadOnlyList<QueryParameter> parameters, out IReadOnlyDictionary<string, string> texts)
    {
        var values = new List<QueryParameter>();
        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            if (item.IsText)
            {
                substitutions[item.Name] = item.Value;
                continue;
            }

            var (value, ok) = Convert(item.Value, item.Kind);
            if (!ok)
            {
                Error = string.Format(CultureInfo.CurrentCulture, BadValue, item.Label, item.Value);
                parameters = [];
                texts = substitutions;
                return false;
            }

            values.Add(new QueryParameter(item.Name, value));
        }

        Error = null;
        parameters = values;
        texts = substitutions;
        return true;
    }

    /// <summary>A typed value as sent.</summary>
    public static (object? Value, bool Ok) Convert(string text, QueryParameterKind kind)
    {
        ArgumentNullException.ThrowIfNull(text);
        switch (kind)
        {
            case QueryParameterKind.Null:
                return (null, true);
            case QueryParameterKind.Text:
                return (text, true);
            case QueryParameterKind.Date:
                var (date, error) = ImportValues.Convert(text, ImportValues.Kind.Moment, emptyIsNull: true);
                return (date, error is null);
            case QueryParameterKind.Number:
                if (text.Trim().Length == 0)
                {
                    return (null, true);
                }

                var (number, numberError) = ImportValues.Convert(text, text.Contains('.', StringComparison.Ordinal) || text.Contains(',', StringComparison.Ordinal) ? ImportValues.Kind.Exact : ImportValues.Kind.Whole, emptyIsNull: true);
                return (number, numberError is null);
            default:
                var trimmed = text.Trim();
                if (long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) && trimmed.Length < 19 && !(trimmed.Length > 1 && trimmed[0] == '0'))
                {
                    return (whole, true);
                }

                if (decimal.TryParse(trimmed, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var exact) && trimmed[0] != '.' && !(trimmed.Length > 1 && trimmed[0] == '0' && trimmed[1] != '.'))
                {
                    return (exact, true);
                }

                return (text, true);
        }
    }
}
