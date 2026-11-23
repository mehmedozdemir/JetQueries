using System.Globalization;
using System.Text.RegularExpressions;
using JetQueries.Providers.Metadata;
using JetQueries.Sdk;

namespace JetQueries.Provider.Sqlite;

/// <summary>
/// The object details view for SQLite, from the schema table and the table-valued pragmas (table_xinfo,
/// index_list, index_info, foreign_key_list). SQLite keeps no row count statistics that are always there, so
/// no row count is shown.
/// </summary>
internal sealed partial class SqliteMetadataProvider : IObjectDetailsProvider
{
    // The timing and event of a trigger: [BEFORE | AFTER | INSTEAD OF] INSERT | DELETE | UPDATE [OF columns] ON.
    [GeneratedRegex(@"(?:\b(?<kind>BEFORE|AFTER|INSTEAD\s+OF)\s+)?\b(?<event>INSERT|DELETE|UPDATE)\b(?:\s+OF\s+(?<columns>.*?))?\s+ON\s",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TriggerHead();

    [GeneratedRegex(@"\bWHERE\b(?<condition>.*)\z", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex PartialIndexCondition();

    [GeneratedRegex(@"\bAUTOINCREMENT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AutoIncrement();

    public async Task<ObjectDetails?> GetDetailsAsync(DatabaseObject target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var databases = await DatabasesAsync(cancellationToken).ConfigureAwait(false);
        if (target.Kind == DatabaseObjectKinds.Schema)
        {
            return Find(databases, target.Name.Name) is { } database
                ? new ObjectDetails(
                    new DatabaseObject(new DatabaseObjectName(null, null, database), DatabaseObjectKinds.Schema),
                    new DetailProperties().Add(ObjectDetailKeys.ObjectType, "Type", "DATABASE").Build(),
                    [])
                : null;
        }

        var type = target.Kind switch
        {
            DatabaseObjectKinds.Table => "table",
            DatabaseObjectKinds.View => "view",
            DatabaseObjectKinds.Trigger => "trigger",
            _ => null,
        };
        if (type is null)
        {
            return null;
        }

        // The same lookup order as an unqualified name in SQL: temp, main, then attached databases.
        IEnumerable<string> lookup = target.Name.Schema is { } given
            ? Find(databases, given) is { } match ? [match] : []
            : databases.OrderBy(static d => d.Equals("temp", StringComparison.OrdinalIgnoreCase) ? 0 : d.Equals("main", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
        foreach (var schema in lookup)
        {
            var sql = $"SELECT s.name, s.tbl_name, s.sql FROM {SqliteNames.Quote(schema)}.sqlite_schema s WHERE s.name = @name COLLATE NOCASE AND s.type = @type";
            var found = await RowsAsync(sql, [("@name", target.Name.Name), ("@type", type)], cancellationToken).ConfigureAwait(false);
            if (found is [var row, ..])
            {
                return await DetailsAsync(target.Kind, schema, (string)row[0]!, (string?)row[1], (string?)row[2], cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private async Task<ObjectDetails> DetailsAsync(DatabaseObjectKinds kind, string schema, string name, string? table, string? definition, CancellationToken cancellationToken)
    {
        var properties = new DetailProperties()
            .Add(ObjectDetailKeys.ObjectType, "Type", kind switch { DatabaseObjectKinds.Table => "TABLE", DatabaseObjectKinds.View => "VIEW", _ => "TRIGGER" })
            .Add(ObjectDetailKeys.Schema, "Database", schema);
        var sections = new List<ObjectDetailSection?>();
        switch (kind)
        {
            case DatabaseObjectKinds.Table:
                {
                    var columns = await ColumnsAsync(schema, name, cancellationToken).ConfigureAwait(false);
                    var foreignKeys = await ForeignKeysAsync(schema, name, cancellationToken).ConfigureAwait(false);
                    var indexes = await IndexesAsync(schema, name, cancellationToken).ConfigureAwait(false);
                    sections.Add(ColumnsSection(columns, foreignKeys, definition));
                    sections.Add(ConstraintsSection(columns, indexes));
                    sections.Add(ForeignKeysSection(schema, foreignKeys));
                    sections.Add(await ReferencesSectionAsync(schema, name, cancellationToken).ConfigureAwait(false));
                    sections.Add(IndexesSection(indexes));
                    sections.Add(await TriggersSectionAsync(schema, name, cancellationToken).ConfigureAwait(false));
                    break;
                }

            case DatabaseObjectKinds.View:
                sections.Add(ColumnsSection(await ColumnsAsync(schema, name, cancellationToken).ConfigureAwait(false), [], definition));
                sections.Add(await TriggersSectionAsync(schema, name, cancellationToken).ConfigureAwait(false));
                break;

            default:
                {
                    var (events, timing) = DescribeTrigger(definition);
                    properties.Add(ObjectDetailKeys.Table, "Table", table).Add(ObjectDetailKeys.Events, "Events", events).Add(ObjectDetailKeys.Kind, "Kind", timing);
                    break;
                }
        }

        return new ObjectDetails(new DatabaseObject(new DatabaseObjectName(null, schema, name), kind), properties.Build(), [.. sections.OfType<ObjectDetailSection>()]);
    }

    private sealed record Column(long Id, string Name, string Type, bool NotNull, string? Default, int PrimaryKey, long Hidden);

    private sealed record ForeignKey(long Id, string Table, List<string> From, List<string?> To, string? OnUpdate, string? OnDelete);

    private sealed record Index(string Name, bool Unique, string Origin, bool Partial, List<string?> Columns, string? Sql);

    private async Task<List<Column>> ColumnsAsync(string schema, string name, CancellationToken cancellationToken)
    {
        // hidden 1 is a virtual table's internal column; 2 and 3 are generated (virtual, stored).
        const string Sql = "SELECT cid, name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo(@name, @schema) WHERE hidden <> 1 ORDER BY cid";
        return [.. (await RowsAsync(Sql, [("@name", name), ("@schema", schema)], cancellationToken).ConfigureAwait(false))
            .Select(static r => new Column((long)r[0]!, (string)r[1]!, (string?)r[2] ?? string.Empty, (long)r[3]! != 0, (string?)r[4], (int)(long)r[5]!, (long)r[6]!))];
    }

    private async Task<List<ForeignKey>> ForeignKeysAsync(string schema, string name, CancellationToken cancellationToken)
    {
        const string Sql = "SELECT id, seq, \"table\", \"from\", \"to\", on_update, on_delete FROM pragma_foreign_key_list(@name, @schema) ORDER BY id, seq";
        var keys = new List<ForeignKey>();
        foreach (var r in await RowsAsync(Sql, [("@name", name), ("@schema", schema)], cancellationToken).ConfigureAwait(false))
        {
            var id = (long)r[0]!;
            if (keys.Count == 0 || keys[^1].Id != id)
            {
                keys.Add(new ForeignKey(id, (string)r[2]!, [], [], (string?)r[5], (string?)r[6]));
            }

            keys[^1].From.Add((string)r[3]!);
            keys[^1].To.Add((string?)r[4]);
        }

        return keys;
    }

    private async Task<List<Index>> IndexesAsync(string schema, string name, CancellationToken cancellationToken)
    {
        var source = SqliteNames.Quote(schema) + ".sqlite_schema";
        var sql = $"""
            SELECT l.name, l."unique", l.origin, l.partial, (SELECT s.sql FROM {source} s WHERE s.type = 'index' AND s.name = l.name)
            FROM pragma_index_list(@name, @schema) l
            ORDER BY l.origin = 'pk' DESC, l.name
            """;
        var indexes = new List<Index>();
        foreach (var r in await RowsAsync(sql, [("@name", name), ("@schema", schema)], cancellationToken).ConfigureAwait(false))
        {
            var indexName = (string)r[0]!;

            // A column of an expression index has no name (cid -2).
            var columns = (await RowsAsync(
                    "SELECT name, \"desc\" FROM pragma_index_xinfo(@index, @schema) WHERE \"key\" = 1 ORDER BY seqno",
                    [("@index", indexName), ("@schema", schema)],
                    cancellationToken).ConfigureAwait(false))
                .Select(static c => c[0] is string column ? column + ((long)c[1]! != 0 ? " DESC" : string.Empty) : "<expression>")
                .ToList<string?>();
            indexes.Add(new Index(indexName, (long)r[1]! != 0, (string)r[2]!, (long)r[3]! != 0, columns, (string?)r[4]));
        }

        return indexes;
    }

    private static ObjectDetailSection? ColumnsSection(List<Column> columns, List<ForeignKey> foreignKeys, string? definition)
    {
        var table = new DetailTable(
            ObjectDetailKeys.Columns,
            "Columns",
            (ObjectDetailKeys.Position, "#"),
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.DataType, "Data type"),
            (ObjectDetailKeys.Nullable, "Nullable"),
            (ObjectDetailKeys.Default, "Default"),
            (ObjectDetailKeys.Identity, "Generated"),
            (ObjectDetailKeys.Key, "Key"));

        // A single INTEGER PRIMARY KEY column is the rowid; AUTOINCREMENT never reuses values.
        var primaryKey = columns.Where(static c => c.PrimaryKey > 0).ToList();
        var rowId = primaryKey is [{ } only] && only.Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase) ? only.Name : null;
        foreach (var c in columns)
        {
            var identity = c.Hidden switch
            {
                2 => "GENERATED (VIRTUAL)",
                3 => "GENERATED (STORED)",
                _ when c.Name == rowId => definition is not null && AutoIncrement().IsMatch(definition) ? "ROWID (AUTOINCREMENT)" : "ROWID",
                _ => null,
            };
            var isForeign = foreignKeys.Any(k => k.From.Contains(c.Name, StringComparer.OrdinalIgnoreCase));
            var keys = string.Join(", ", new[] { c.PrimaryKey > 0 ? "PK" : null, isForeign ? "FK" : null }.OfType<string>());
            table.Add([
                (c.Id + 1).ToString(CultureInfo.InvariantCulture),
                c.Name,
                c.Type.Length == 0 ? null : c.Type,
                DetailTable.Flag(!c.NotNull && c.PrimaryKey == 0),
                c.Default,
                identity,
                keys.Length == 0 ? null : keys,
            ]);
        }

        return table.Build();
    }

    private static ObjectDetailSection? ConstraintsSection(List<Column> columns, List<Index> indexes)
    {
        // SQLite has no catalog of constraints: the primary key comes from table_xinfo, unique ones from their
        // automatic indexes. CHECK constraints are only in the CREATE TABLE text.
        var table = new DetailTable(
            ObjectDetailKeys.Constraints,
            "Constraints",
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.Kind, "Kind"),
            (ObjectDetailKeys.ColumnList, "Columns"));
        var primaryKey = columns.Where(static c => c.PrimaryKey > 0).OrderBy(static c => c.PrimaryKey).Select(static c => c.Name).ToList();
        if (primaryKey.Count > 0)
        {
            table.Add([indexes.FirstOrDefault(static i => i.Origin == "pk")?.Name, "PRIMARY KEY", string.Join(", ", primaryKey)]);
        }

        foreach (var index in indexes.Where(static i => i.Origin == "u"))
        {
            table.Add([index.Name, "UNIQUE", string.Join(", ", index.Columns)]);
        }

        return table.Build();
    }

    private static ObjectDetailSection? ForeignKeysSection(string schema, List<ForeignKey> foreignKeys)
    {
        var table = new DetailTable(
            ObjectDetailKeys.ForeignKeys,
            "Foreign keys",
            (ObjectDetailKeys.ColumnList, "Columns"),
            (ObjectDetailKeys.Table, "Referenced table"),
            (ObjectDetailKeys.ReferencedColumns, "Referenced columns"),
            (ObjectDetailKeys.OnDelete, "On delete"),
            (ObjectDetailKeys.OnUpdate, "On update"));
        foreach (var key in foreignKeys)
        {
            // "to" is null when the key refers to the other table's primary key without naming it.
            var to = key.To.All(static t => t is null) ? null : string.Join(", ", key.To);
            table.Add(
                [string.Join(", ", key.From), key.Table, to, key.OnDelete, key.OnUpdate],
                new DatabaseObject(new DatabaseObjectName(null, schema, key.Table), DatabaseObjectKinds.Table));
        }

        return table.Build();
    }

    private async Task<ObjectDetailSection?> ReferencesSectionAsync(string schema, string name, CancellationToken cancellationToken)
    {
        // Every table's foreign keys, as SQLite keeps them nowhere else; the pragma takes the table from the row.
        var sql = $"""
            SELECT s.name, f.id, f."from", f."to", f.on_delete, f.on_update
            FROM {SqliteNames.Quote(schema)}.sqlite_schema s, pragma_foreign_key_list(s.name, @schema) f
            WHERE s.type = 'table' AND f."table" = @name COLLATE NOCASE
            ORDER BY s.name COLLATE NOCASE, f.id, f.seq
            """;
        var table = new DetailTable(
            ObjectDetailKeys.References,
            "References",
            (ObjectDetailKeys.Table, "Table"),
            (ObjectDetailKeys.ColumnList, "Columns"),
            (ObjectDetailKeys.ReferencedColumns, "Referenced columns"),
            (ObjectDetailKeys.OnDelete, "On delete"),
            (ObjectDetailKeys.OnUpdate, "On update"));
        var rows = await RowsAsync(sql, [("@name", name), ("@schema", schema)], cancellationToken).ConfigureAwait(false);
        foreach (var group in rows.GroupBy(static r => ((string)r[0]!, (long)r[1]!)))
        {
            var first = group.First();
            var to = group.All(static r => r[3] is null) ? null : string.Join(", ", group.Select(static r => (string?)r[3]));
            table.Add(
                [group.Key.Item1, string.Join(", ", group.Select(static r => (string?)r[2])), to, (string?)first[4], (string?)first[5]],
                new DatabaseObject(new DatabaseObjectName(null, schema, group.Key.Item1), DatabaseObjectKinds.Table));
        }

        return table.Build();
    }

    private static ObjectDetailSection? IndexesSection(List<Index> indexes)
    {
        var table = new DetailTable(
            ObjectDetailKeys.Indexes,
            "Indexes",
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.Kind, "Kind"),
            (ObjectDetailKeys.Unique, "Unique"),
            (ObjectDetailKeys.ColumnList, "Columns"),
            (ObjectDetailKeys.Filter, "Filter"));
        foreach (var index in indexes)
        {
            var kind = index.Origin switch { "pk" => "PRIMARY KEY", "u" => "UNIQUE CONSTRAINT", _ => "INDEX" };
            var filter = index.Partial && index.Sql is { } sql && PartialIndexCondition().Match(sql) is { Success: true } where
                ? where.Groups["condition"].Value.Trim().TrimEnd(';').Trim()
                : null;
            table.Add([index.Name, kind, DetailTable.Flag(index.Unique), string.Join(", ", index.Columns), filter], source: index.Sql);
        }

        return table.Build();
    }

    private async Task<ObjectDetailSection?> TriggersSectionAsync(string schema, string name, CancellationToken cancellationToken)
    {
        var sql = $"SELECT s.name, s.sql FROM {SqliteNames.Quote(schema)}.sqlite_schema s WHERE s.type = 'trigger' AND s.tbl_name = @name COLLATE NOCASE ORDER BY s.name";
        var table = new DetailTable(
            ObjectDetailKeys.Triggers,
            "Triggers",
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.Events, "Events"),
            (ObjectDetailKeys.Kind, "Kind"));
        foreach (var t in await RowsAsync(sql, [("@name", name)], cancellationToken).ConfigureAwait(false))
        {
            var source = (string?)t[1];
            var (events, kind) = DescribeTrigger(source);
            table.Add(
                [(string?)t[0], events, kind],
                new DatabaseObject(new DatabaseObjectName(null, schema, (string)t[0]!), DatabaseObjectKinds.Trigger),
                source);
        }

        return table.Build();
    }

    /// <summary>The event (INSERT, UPDATE OF a, b…) and timing of a CREATE TRIGGER statement; BEFORE when not written.</summary>
    internal static (string? Events, string? Kind) DescribeTrigger(string? sql)
    {
        if (sql is null || TriggerHead().Match(sql) is not { Success: true } match)
        {
            return (null, null);
        }

        var events = match.Groups["event"].Value.ToUpperInvariant();
        if (match.Groups["columns"].Success)
        {
            events += " OF " + match.Groups["columns"].Value.Trim();
        }

        var timing = match.Groups["kind"].Value.ToUpperInvariant();
        var kind = timing.Length == 0 ? "BEFORE" : timing.StartsWith("INSTEAD", StringComparison.Ordinal) ? "INSTEAD OF" : timing;
        return (events, kind);
    }

    /// <summary>Every row as its values; DBNull becomes null.</summary>
    private async Task<List<object?[]>> RowsAsync(string sql, (string Name, object? Value)[] parameters, CancellationToken cancellationToken)
    {
        var rows = new List<object?[]>();
        await foreach (var row in QueryAsync(
                           sql,
                           parameters,
                           static r =>
                           {
                               var cells = new object?[r.FieldCount];
                               for (var i = 0; i < cells.Length; i++)
                               {
                                   cells[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                               }

                               return cells;
                           },
                           cancellationToken).ConfigureAwait(false))
        {
            rows.Add(row);
        }

        return rows;
    }
}
