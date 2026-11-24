using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Connections;
using JetQueries.Core.Queries;
using JetQueries.Presentation.Resources;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>
/// Whether a SELECT's rows can be edited in the grid: one table after FROM (no join, union, grouping or DISTINCT),
/// and which result columns come straight from a column of it. Read from the text only; the table is then resolved
/// by the language service and its key read from the catalog.
/// </summary>
public static class EditableSelect
{
    /// <summary>A SELECT of one table.</summary>
    /// <param name="TableOffset">An offset inside the table's name (for the language service to resolve it).</param>
    /// <param name="TableText">The table as written.</param>
    /// <param name="AllColumns">The select list has <c>*</c> or <c>t.*</c>.</param>
    /// <param name="Plain">Result column name → table column, for the items that are a column (maybe renamed).</param>
    public sealed record Shape(int TableOffset, string TableText, bool AllColumns, IReadOnlyDictionary<string, string> Plain);

    private readonly record struct Token(string Text, int Start, int End, bool Quoted, bool Word, int Depth);

    private static readonly HashSet<string> Rejected = new(StringComparer.OrdinalIgnoreCase)
    {
        "JOIN", "UNION", "INTERSECT", "EXCEPT", "MINUS", "GROUP", "HAVING", "DISTINCT", "INTO", "CONNECT", "START", "PIVOT", "UNPIVOT",
        "APPLY", "WINDOW", "LATERAL", "MODEL", "MATCH_RECOGNIZE", "TABLESAMPLE",
    };

    private static readonly HashSet<string> AfterTable = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "ORDER", "LIMIT", "OFFSET", "FETCH", "FOR",
    };

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "ORDER", "LIMIT", "OFFSET", "FETCH", "FOR", "AS", "WITH", "ON", "AND", "OR", "NOT", "NULL", "CASE", "TOP",
    };

    /// <summary>The shape of <paramref name="sql"/>; null when it is not a SELECT of one table.</summary>
    public static Shape? Analyze(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var tokens = Tokenize(sql);
        while (tokens.Count > 0 && tokens[^1].Text == ";" && !tokens[^1].Quoted)
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        if (tokens.Count < 4 || !IsWord(tokens[0], "SELECT") || tokens.Any(static t => t.Text == ";" && !t.Quoted))
        {
            return null;
        }

        if (tokens.Any(t => t is { Word: true, Depth: 0 } && Rejected.Contains(t.Text)))
        {
            return null;
        }

        var from = tokens.FindIndex(t => t.Depth == 0 && IsWord(t, "FROM"));
        if (from < 0 || tokens.FindIndex(from + 1, t => t.Depth == 0 && IsWord(t, "FROM")) >= 0)
        {
            return null;
        }

        // The table: a name with its schema (and database), maybe an alias, then the end or a clause.
        var i = from + 1;
        if (i >= tokens.Count || !IsName(tokens[i]))
        {
            return null;
        }

        var last = tokens[i];
        while (i + 2 < tokens.Count && tokens[i + 1] is { Text: ".", Quoted: false } && IsName(tokens[i + 2]))
        {
            i += 2;
            last = tokens[i];
        }

        var tableText = sql[tokens[from + 1].Start..last.End];
        i++;
        if (i < tokens.Count && IsWord(tokens[i], "AS"))
        {
            i++;
            if (i >= tokens.Count || !IsName(tokens[i]))
            {
                return null;
            }

            i++;
        }
        else if (i < tokens.Count && IsName(tokens[i]) && !(tokens[i].Word && AfterTable.Contains(tokens[i].Text)))
        {
            i++;
        }

        if (i < tokens.Count && !(tokens[i] is { Word: true, Depth: 0 } && AfterTable.Contains(tokens[i].Text)))
        {
            return null;
        }

        // The select list: which result columns are a column of the table.
        var start = 1;
        if (start < from && IsWord(tokens[start], "ALL"))
        {
            start++;
        }

        if (start < from && IsWord(tokens[start], "TOP"))
        {
            start++;
            if (start < from && tokens[start].Text == "(")
            {
                while (start < from && !(tokens[start].Text == ")" && tokens[start].Depth == 0))
                {
                    start++;
                }
            }

            start++;
            while (start < from && (IsWord(tokens[start], "PERCENT") || IsWord(tokens[start], "WITH") || IsWord(tokens[start], "TIES")))
            {
                start++;
            }
        }

        var all = false;
        var plain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var item = new List<Token>();
        for (var t = start; t <= from; t++)
        {
            if (t == from || tokens[t] is { Text: ",", Quoted: false, Depth: 0 })
            {
                switch (Item(item))
                {
                    case (true, _, _):
                        all = true;
                        break;
                    case (false, { } header, { } column):
                        plain[header] = column;
                        break;
                }

                item.Clear();
            }
            else
            {
                item.Add(tokens[t]);
            }
        }

        return new Shape(last.Quoted ? last.Start + 1 : last.Start, tableText, all, plain);
    }

    // "*", "t.*" → all; "a", "t.a", "a AS b", "t.a b" → (header, column); anything else is computed.
    private static (bool All, string? Header, string? Column) Item(List<Token> item)
    {
        if (item.Count == 0)
        {
            return (false, null, null);
        }

        var i = 0;
        if (!IsName(item[0]) && item[0].Text != "*")
        {
            return (false, null, null);
        }

        while (i + 2 < item.Count && item[i + 1] is { Text: ".", Quoted: false } && (IsName(item[i + 2]) || item[i + 2].Text == "*"))
        {
            i += 2;
        }

        if (item[i] is { Text: "*", Quoted: false })
        {
            return (i == item.Count - 1, null, null);
        }

        var column = item[i].Text;
        i++;
        if (i == item.Count)
        {
            return (false, column, column);
        }

        if (IsWord(item[i], "AS"))
        {
            i++;
        }

        return i == item.Count - 1 && IsName(item[i]) ? (false, item[i].Text, column) : (false, null, null);
    }

    private static bool IsWord(Token token, string word) => token.Word && string.Equals(token.Text, word, StringComparison.OrdinalIgnoreCase);

    private static bool IsName(Token token) => token.Quoted || (token.Word && !Reserved.Contains(token.Text));

    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        var depth = 0;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
            }
            else if (c == '\'')
            {
                var start = i++;
                while (i < sql.Length && (sql[i] != '\'' || (i + 1 < sql.Length && sql[i + 1] == '\'' && ++i > 0)))
                {
                    i++;
                }

                i = Math.Min(i + 1, sql.Length);
                tokens.Add(new Token(sql[start..i], start, i, Quoted: false, Word: false, depth));
            }
            else if (c is '"' or '[' or '`')
            {
                var close = c == '[' ? ']' : c;
                var start = i;
                var end = sql.IndexOf(close, i + 1);
                i = end < 0 ? sql.Length : end + 1;
                tokens.Add(new Token(sql[(start + 1)..Math.Max(start + 1, i - 1)], start, i, Quoted: true, Word: false, depth));
            }
            else if (char.IsLetter(c) || c is '_' or '@' or '#')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$' or '#' or '@'))
                {
                    i++;
                }

                tokens.Add(new Token(sql[start..i], start, i, Quoted: false, Word: true, depth));
            }
            else if (char.IsDigit(c))
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '.'))
                {
                    i++;
                }

                tokens.Add(new Token(sql[start..i], start, i, Quoted: false, Word: false, depth));
            }
            else
            {
                if (c == ')')
                {
                    depth = Math.Max(0, depth - 1);
                }

                tokens.Add(new Token(c.ToString(), i, i + 1, Quoted: false, Word: false, depth));
                if (c == '(')
                {
                    depth++;
                }

                i++;
            }
        }

        return tokens;
    }
}

/// <summary>What a saved change does.</summary>
public enum RowChangeKind
{
    Update,
    Insert,
    Delete,
}

/// <summary>One row's change, as saved: the columns and their new values, and the key as it was read.</summary>
/// <param name="Kind">Update, insert or delete.</param>
/// <param name="Columns">The table columns set (update, insert); empty for a delete.</param>
/// <param name="Values">Their values.</param>
/// <param name="KeyColumns">The key columns (update, delete).</param>
/// <param name="Keys">The key's values as read.</param>
public sealed record RowChange(RowChangeKind Kind, IReadOnlyList<string> Columns, IReadOnlyList<object?> Values, IReadOnlyList<string> KeyColumns, IReadOnlyList<object?> Keys);

/// <summary>
/// The changes made to a result in its grid, not saved yet (DBeaver's data editor): cells set, rows added, rows
/// marked for deletion. Values are converted by the column's type as they are typed; an empty cell is NULL. Saving
/// runs them in one transaction, each found by the table's primary key.
/// </summary>
public sealed partial class ResultEditor : ObservableObject
{
    private static readonly CompositeFormat BadValue = CompositeFormat.Parse(Strings.EditBadValue);
    private readonly QueryResultSet _set;
    private readonly ColumnInfo?[] _targets;
    private readonly ImportValues.Kind[] _kinds;
    private readonly int[] _keys;
    private readonly Dictionary<int, object?[]> _originals = [];
    private readonly Dictionary<int, HashSet<int>> _modified = [];
    private readonly HashSet<int> _deleted = [];
    private readonly SortedSet<int> _inserted = [];

    /// <param name="set">The result.</param>
    /// <param name="table">The table its rows come from.</param>
    /// <param name="database">The database to save in (the query window's); null for the connection's.</param>
    /// <param name="targets">For each result column, the table column it shows; null for a computed one.</param>
    /// <param name="keys">The result columns of the primary key.</param>
    /// <param name="canInsert">Rows can be added (the provider scripts an INSERT).</param>
    internal ResultEditor(QueryResultSet set, DatabaseObjectName table, string? database, IReadOnlyList<ColumnInfo?> targets, IReadOnlyList<int> keys, bool canInsert)
    {
        _set = set;
        Table = table;
        Database = database;
        _targets = [.. targets];
        _kinds = [.. targets.Select(static t => t is null ? ImportValues.Kind.Text : ImportValues.KindOf(t.DataType))];
        _keys = [.. keys];
        CanInsert = canInsert;
    }

    /// <summary>A cell, a row mark or the rows changed: the grid draws again.</summary>
    public event EventHandler? Changed;

    /// <summary>The table the changes go to.</summary>
    public DatabaseObjectName Table { get; }

    /// <summary>The database to save in; null for the connection's.</summary>
    public string? Database { get; }

    /// <summary>Rows can be added.</summary>
    public bool CanInsert { get; }

    /// <summary>Rows changed, added or marked for deletion.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
    public partial int ChangeCount { get; private set; }

    /// <summary>There is something to save.</summary>
    public bool HasChanges => ChangeCount > 0;

    /// <summary>Why the last value typed was refused; null when it was taken.</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>Whether the cell can be edited: a column of the table (not binary), on a row not marked for deletion.</summary>
    public bool CanEdit(int row, int column) =>
        column >= 0 && column < _targets.Length && _targets[column] is not null && _kinds[column] != ImportValues.Kind.Bytes && !_deleted.Contains(row);

    /// <summary>The text the editor starts with.</summary>
    public string? EditText(int row, int column) => QueryResultSet.FormatValue(_set.GetValue(row, column));

    /// <summary>Whether the cell holds a change.</summary>
    public bool IsModified(int row, int column) => _modified.TryGetValue(row, out var columns) && columns.Contains(column);

    /// <summary>Whether the row was added.</summary>
    public bool IsInserted(int row) => _inserted.Contains(row);

    /// <summary>Whether the row is to be deleted.</summary>
    public bool IsDeleted(int row) => _deleted.Contains(row);

    /// <summary>Sets a cell from the text typed; false (and <see cref="Error"/>) when it is not a value of the column's type.</summary>
    public bool SetText(int row, int column, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!CanEdit(row, column))
        {
            return false;
        }

        var (value, error) = ImportValues.Convert(text, _kinds[column], emptyIsNull: true);
        if (error is not null)
        {
            Error = string.Format(CultureInfo.CurrentCulture, BadValue, text, _targets[column]!.Name, _targets[column]!.DataType);
            return false;
        }

        Error = null;
        if (!_inserted.Contains(row) && !_originals.ContainsKey(row))
        {
            _originals[row] = [.. _set.Row(row)];
        }

        _set.SetValue(row, column, value);
        if (!_modified.TryGetValue(row, out var columns))
        {
            _modified[row] = columns = [];
        }

        if (!_inserted.Contains(row) && Same(_originals[row][column], value))
        {
            columns.Remove(column);
            if (columns.Count == 0)
            {
                _modified.Remove(row);
                _originals.Remove(row);
            }
        }
        else
        {
            columns.Add(column);
        }

        Update();
        return true;
    }

    /// <summary>Adds an empty row at the end; its source row.</summary>
    public int AddRow()
    {
        if (!CanInsert)
        {
            return -1;
        }

        var row = _set.RowCount;
        _set.AddRows([new object?[_set.Columns.Count]]);
        _inserted.Add(row);
        Update();
        return row;
    }

    /// <summary>Marks the rows for deletion, or unmarks them when all already are; an added row is dropped at once.</summary>
    public void ToggleDelete(IReadOnlyCollection<int> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var unmark = rows.Count > 0 && rows.All(_deleted.Contains);
        foreach (var row in rows)
        {
            if (unmark)
            {
                _deleted.Remove(row);
            }
            else if (row >= 0 && row < _set.RowCount)
            {
                _deleted.Add(row);
            }
        }

        // Added rows marked for deletion at the end of the result are simply dropped.
        while (_inserted.Count > 0 && _inserted.Max == _set.RowCount - 1 && _deleted.Contains(_inserted.Max))
        {
            var last = _inserted.Max;
            _inserted.Remove(last);
            _deleted.Remove(last);
            _modified.Remove(last);
            _set.RemoveRowsFrom(last);
        }

        Update();
    }

    private bool CanRevert() => HasChanges;

    /// <summary>Drops every change: the values read come back, added rows go.</summary>
    [RelayCommand(CanExecute = nameof(CanRevert))]
    public void Revert()
    {
        foreach (var (row, values) in _originals)
        {
            for (var c = 0; c < values.Length; c++)
            {
                _set.SetValue(row, c, values[c]);
            }
        }

        if (_inserted.Count > 0)
        {
            _set.RemoveRowsFrom(_inserted.Min);
        }

        Clear();
    }

    /// <summary>Forgets the changes (they were saved, or the rows are being replaced).</summary>
    public void Clear()
    {
        _originals.Clear();
        _modified.Clear();
        _deleted.Clear();
        _inserted.Clear();
        Error = null;
        Update();
    }

    /// <summary>The changes to save, in order: deletes, updates, inserts.</summary>
    public IReadOnlyList<RowChange> Changes()
    {
        var keyColumns = _keys.Select(k => _targets[k]!.Name).ToArray();
        var changes = new List<RowChange>();
        foreach (var row in _deleted.Where(r => !_inserted.Contains(r)).Order())
        {
            changes.Add(new RowChange(RowChangeKind.Delete, [], [], keyColumns, KeysOf(row)));
        }

        foreach (var (row, columns) in _modified.Where(m => !_inserted.Contains(m.Key) && !_deleted.Contains(m.Key)).OrderBy(static m => m.Key))
        {
            var set = columns.Order().ToArray();
            changes.Add(new RowChange(RowChangeKind.Update, [.. set.Select(c => _targets[c]!.Name)], [.. set.Select(c => _set.GetValue(row, c))], keyColumns, KeysOf(row)));
        }

        foreach (var row in _inserted.Where(r => !_deleted.Contains(r)))
        {
            // Columns left empty are not named, so that their defaults (and identities) apply.
            var set = Enumerable.Range(0, _targets.Length).Where(c => _targets[c] is not null && _set.GetValue(row, c) is not null).ToArray();
            if (set.Length > 0)
            {
                changes.Add(new RowChange(RowChangeKind.Insert, [.. set.Select(c => _targets[c]!.Name)], [.. set.Select(c => _set.GetValue(row, c))], [], []));
            }
        }

        return changes;
    }

    /// <summary>Runs the changes in one transaction (when the connection has them); a row not found rolls everything back.</summary>
    /// <returns>Rows written.</returns>
    public static async Task<int> SaveAsync(ConnectionSession session, DatabaseObjectName table, string? database, IReadOnlyList<RowChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(changes);
        if (session.Provider is not IRowEditScripting rows)
        {
            throw new NotSupportedException(Strings.EditNotSupported);
        }

        var channel = await session.OpenChannelAsync(cancellationToken).ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            if (database is not null && channel.ContextKind == DatabaseContextKind.Database)
            {
                await channel.ChangeDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
            }

            var transaction = channel.SupportsTransactions;
            if (transaction)
            {
                await channel.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                foreach (var change in changes)
                {
                    var (sql, parameters) = Statement(session.Provider, rows, table, change);
                    long? affected = null;
                    await foreach (var item in channel.ExecuteAsync(new QueryRequest(sql) { Parameters = parameters }, cancellationToken).ConfigureAwait(false))
                    {
                        if (item is StatementCompleted { RowsAffected: { } count } && affected is null)
                        {
                            affected = count;
                        }
                    }

                    // The key finds one row or the change is not what the user saw: nothing is kept.
                    if (change.Kind != RowChangeKind.Insert && affected is { } found && found != 1)
                    {
                        throw new InvalidOperationException(string.Format(
                            CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.EditRowsAffected), found, Describe(change)));
                    }
                }

                if (transaction)
                {
                    await channel.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                return changes.Count;
            }
            catch
            {
                if (transaction)
                {
                    await channel.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }
        }
    }

    /// <summary>The statement of a change and its parameters.</summary>
    public static (string Sql, IReadOnlyList<QueryParameter> Parameters) Statement(IDatabaseProvider provider, IRowEditScripting rows, DatabaseObjectName table, RowChange change)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(change);
        switch (change.Kind)
        {
            case RowChangeKind.Update:
                return (rows.ScriptUpdate(table, change.Columns, change.KeyColumns), [.. Parameters('s', change.Values), .. Parameters('k', change.Keys)]);
            case RowChangeKind.Delete:
                return (rows.ScriptDelete(table, change.KeyColumns), [.. Parameters('k', change.Keys)]);
            default:
                if (provider is not IBulkInsertScripting insert)
                {
                    throw new NotSupportedException(Strings.EditNotSupported);
                }

                return (insert.ScriptInsert(table, change.Columns, 1),
                    [.. change.Values.Select(static (v, c) => new QueryParameter(string.Create(CultureInfo.InvariantCulture, $"p0_{c}"), v))]);
        }

        static IEnumerable<QueryParameter> Parameters(char kind, IReadOnlyList<object?> values) =>
            values.Select((v, i) => new QueryParameter(string.Create(CultureInfo.InvariantCulture, $"{kind}{i}"), v));
    }

    private static string Describe(RowChange change) =>
        string.Join(", ", change.KeyColumns.Select((k, i) => k + " = " + (QueryResultSet.FormatValue(change.Keys[i]) ?? "NULL")));

    private object?[] KeysOf(int row) => [.. _keys.Select(k => _originals.TryGetValue(row, out var original) ? original[k] : _set.GetValue(row, k))];

    private static bool Same(object? a, object? b) => a switch
    {
        null => b is null,
        _ when b is null => false,
        IConvertible when b is IConvertible && IsNumber(a) && IsNumber(b) =>
            Convert.ToDecimal(a, CultureInfo.InvariantCulture) == Convert.ToDecimal(b, CultureInfo.InvariantCulture),
        _ => Equals(a, b) || string.Equals(QueryResultSet.FormatValue(a), QueryResultSet.FormatValue(b), StringComparison.Ordinal),
    };

    private static bool IsNumber(object value) => value is int or long or short or byte or sbyte or decimal or uint or ulong or ushort;

    private void Update()
    {
        ChangeCount = _deleted.Count(r => !_inserted.Contains(r))
            + _modified.Keys.Count(r => !_inserted.Contains(r) && !_deleted.Contains(r))
            + _inserted.Count(r => !_deleted.Contains(r));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
