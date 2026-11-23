using System.Globalization;
using JetQueries.Providers.Metadata;
using JetQueries.Sdk;

namespace JetQueries.Provider.Oracle;

/// <summary>
/// The object details view for Oracle: everything is read from ALL_* dictionary views with bind variables.
/// Row counts come from the optimizer statistics (ALL_TABLES.NUM_ROWS); no user table is ever scanned.
/// </summary>
internal sealed partial class OracleMetadataProvider : IObjectDetailsProvider
{
    private const string DateFormat = "'YYYY-MM-DD HH24:MI:SS'";

    public async Task<ObjectDetails?> GetDetailsAsync(DatabaseObject target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind == DatabaseObjectKinds.Schema)
        {
            return await SchemaDetailsAsync(target, cancellationToken).ConfigureAwait(false);
        }

        if (ObjectTypes.FirstOrDefault(t => t.Kind == target.Kind) is not { Type: { } type })
        {
            return null;
        }

        var objectSql = $"""
            SELECT o.owner, o.object_name, TO_CHAR(o.created, {DateFormat}), TO_CHAR(o.last_ddl_time, {DateFormat}), o.status
            FROM all_objects o
            WHERE o.owner = NVL(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')) AND o.object_name = :name AND o.object_type = :type
            """;
        var found = await RowsAsync(objectSql, [("owner", target.Name.Schema), ("name", target.Name.Name), ("type", type)], cancellationToken).ConfigureAwait(false);
        if (found is not [var head, ..])
        {
            return null;
        }

        var owner = head[0]!;
        var name = head[1]!;
        var resolved = new DatabaseObject(new DatabaseObjectName(null, owner, name), target.Kind);
        var properties = new DetailProperties()
            .Add(ObjectDetailKeys.ObjectType, "Type", type)
            .Add(ObjectDetailKeys.Schema, "Owner", owner)
            .Add(ObjectDetailKeys.Created, "Created", head[2])
            .Add(ObjectDetailKeys.Modified, "Last DDL", head[3])
            .Add(ObjectDetailKeys.Status, "Status", head[4]);
        var sections = new List<ObjectDetailSection?>();
        (string, string?)[] key = [("owner", owner), ("name", name)];

        switch (target.Kind)
        {
            case DatabaseObjectKinds.Table:
                await TablePropertiesAsync(properties, key, cancellationToken).ConfigureAwait(false);
                sections.Add(await ColumnsSectionAsync(key, cancellationToken).ConfigureAwait(false));
                sections.Add(await ConstraintsSectionAsync(key, cancellationToken).ConfigureAwait(false));
                sections.Add(await ForeignKeysSectionAsync(owner, key, cancellationToken).ConfigureAwait(false));
                sections.Add(await ReferencesSectionAsync(owner, key, cancellationToken).ConfigureAwait(false));
                sections.Add(await IndexesSectionAsync(key, cancellationToken).ConfigureAwait(false));
                sections.Add(await TriggersSectionAsync(key, cancellationToken).ConfigureAwait(false));
                break;

            case DatabaseObjectKinds.View:
                properties.Add(ObjectDetailKeys.Comment, "Comment", await TableCommentAsync(key, cancellationToken).ConfigureAwait(false));
                sections.Add(await ColumnsSectionAsync(key, cancellationToken).ConfigureAwait(false));
                sections.Add(await TriggersSectionAsync(key, cancellationToken).ConfigureAwait(false));
                break;

            case DatabaseObjectKinds.Procedure or DatabaseObjectKinds.Function:
                sections.Add(await ParametersSectionAsync(key, package: false, cancellationToken).ConfigureAwait(false));
                break;

            case DatabaseObjectKinds.Package or DatabaseObjectKinds.PackageBody:
                sections.Add(await ParametersSectionAsync(key, package: true, cancellationToken).ConfigureAwait(false));
                break;

            case DatabaseObjectKinds.Sequence:
                await SequencePropertiesAsync(properties, key, cancellationToken).ConfigureAwait(false);
                break;

            case DatabaseObjectKinds.Synonym:
                {
                    const string Sql = "SELECT s.table_owner, s.table_name, s.db_link FROM all_synonyms s WHERE s.owner = :owner AND s.synonym_name = :name";
                    if (await RowsAsync(Sql, key, cancellationToken).ConfigureAwait(false) is [var synonym, ..])
                    {
                        var to = (synonym[0] is { } toOwner ? toOwner + "." : string.Empty) + synonym[1] + (synonym[2] is { } link ? "@" + link : string.Empty);
                        properties.Add(ObjectDetailKeys.Definition, "For", to);
                    }

                    break;
                }

            case DatabaseObjectKinds.Trigger:
                {
                    const string Sql = """
                        SELECT t.table_owner, t.table_name, t.triggering_event, t.trigger_type, t.status
                        FROM all_triggers t WHERE t.owner = :owner AND t.trigger_name = :name
                        """;
                    if (await RowsAsync(Sql, key, cancellationToken).ConfigureAwait(false) is [var trigger, ..])
                    {
                        properties.Add(ObjectDetailKeys.Table, "Table", trigger[1] is null ? null : Qualify(owner, trigger[0], trigger[1]!))
                            .Add(ObjectDetailKeys.Events, "Events", trigger[2])
                            .Add(ObjectDetailKeys.Kind, "Kind", trigger[3])
                            .Add("enabled", "Enabled", trigger[4]);
                    }

                    break;
                }
        }

        if (target.Kind is not (DatabaseObjectKinds.Synonym or DatabaseObjectKinds.Sequence))
        {
            sections.Add(await DependenciesSectionAsync(owner, name, type, cancellationToken).ConfigureAwait(false));
        }

        return new ObjectDetails(resolved, properties.Build(), [.. sections.OfType<ObjectDetailSection>()]);
    }

    private async Task<ObjectDetails?> SchemaDetailsAsync(DatabaseObject target, CancellationToken cancellationToken)
    {
        var sql = $"SELECT u.username, TO_CHAR(u.created, {DateFormat}) FROM all_users u WHERE u.username = :name";
        if (await RowsAsync(sql, [("name", target.Name.Name)], cancellationToken).ConfigureAwait(false) is not [var user, ..])
        {
            return null;
        }

        var properties = new DetailProperties()
            .Add(ObjectDetailKeys.ObjectType, "Type", "SCHEMA")
            .Add(ObjectDetailKeys.Created, "Created", user[1]);
        return new ObjectDetails(new DatabaseObject(new DatabaseObjectName(null, null, user[0]!), DatabaseObjectKinds.Schema), properties.Build(), []);
    }

    private async Task TablePropertiesAsync(DetailProperties properties, (string, string?)[] key, CancellationToken cancellationToken)
    {
        // Segments are only visible for the user's own tables (USER_SEGMENTS); for other schemas the size stays empty.
        var sql = $"""
            SELECT TO_CHAR(t.num_rows), TO_CHAR(t.last_analyzed, {DateFormat}), t.tablespace_name, t.partitioned, t.temporary, c.comments,
                   (SELECT TO_CHAR(SUM(s.bytes)) FROM user_segments s WHERE t.owner = USER AND s.segment_name = t.table_name AND s.segment_type LIKE 'TABLE%')
            FROM all_tables t
            LEFT JOIN all_tab_comments c ON c.owner = t.owner AND c.table_name = t.table_name
            WHERE t.owner = :owner AND t.table_name = :name
            """;
        if (await RowsAsync(sql, key, cancellationToken).ConfigureAwait(false) is not [var table, ..])
        {
            return;
        }

        properties.Add(ObjectDetailKeys.Rows, "Rows (statistics)", table[0])
            .Add("lastAnalyzed", "Last analyzed", table[1])
            .Add(ObjectDetailKeys.Size, "Size", FormatBytes(table[6]))
            .Add(ObjectDetailKeys.Tablespace, "Tablespace", table[2])
            .Add("partitioned", "Partitioned", table[3] == "YES" ? "YES" : null)
            .Add("temporary", "Temporary", table[4] == "Y" ? "YES" : null)
            .Add(ObjectDetailKeys.Comment, "Comment", table[5]);
    }

    private async Task<string?> TableCommentAsync((string, string?)[] key, CancellationToken cancellationToken) =>
        await RowsAsync("SELECT c.comments FROM all_tab_comments c WHERE c.owner = :owner AND c.table_name = :name", key, cancellationToken).ConfigureAwait(false)
            is [var row, ..] ? row[0] : null;

    private async Task<ObjectDetailSection?> ColumnsSectionAsync((string, string?)[] key, CancellationToken cancellationToken)
    {
        // ALL_TAB_COLS (not ALL_TAB_COLUMNS) tells virtual columns; hidden system columns are left out.
        const string Sql = """
            SELECT TO_CHAR(c.column_id), c.column_name, c.data_type, TO_CHAR(c.data_length), TO_CHAR(c.data_precision), TO_CHAR(c.data_scale),
                   TO_CHAR(c.char_length), c.char_used, c.nullable, c.identity_column, c.virtual_column,
                   CASE WHEN EXISTS (SELECT 1 FROM all_constraints k JOIN all_cons_columns kc ON kc.owner = k.owner AND kc.constraint_name = k.constraint_name
                                     WHERE k.constraint_type = 'P' AND k.owner = c.owner AND k.table_name = c.table_name AND kc.column_name = c.column_name)
                        THEN 'PK' END,
                   CASE WHEN EXISTS (SELECT 1 FROM all_constraints k JOIN all_cons_columns kc ON kc.owner = k.owner AND kc.constraint_name = k.constraint_name
                                     WHERE k.constraint_type = 'R' AND k.owner = c.owner AND k.table_name = c.table_name AND kc.column_name = c.column_name)
                        THEN 'FK' END,
                   m.comments, c.data_default
            FROM all_tab_cols c
            LEFT JOIN all_col_comments m ON m.owner = c.owner AND m.table_name = c.table_name AND m.column_name = c.column_name
            WHERE c.owner = :owner AND c.table_name = :name AND c.hidden_column = 'NO'
            ORDER BY c.column_id
            """;
        var table = new DetailTable(
            ObjectDetailKeys.Columns,
            "Columns",
            (ObjectDetailKeys.Position, "#"),
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.DataType, "Data type"),
            (ObjectDetailKeys.Nullable, "Nullable"),
            (ObjectDetailKeys.Default, "Default"),
            (ObjectDetailKeys.Identity, "Identity / virtual"),
            (ObjectDetailKeys.Key, "Key"),
            (ObjectDetailKeys.Comment, "Comment"));
        foreach (var c in await RowsAsync(Sql, key, cancellationToken).ConfigureAwait(false))
        {
            var dataType = OracleTypeFormatter.Format(c[2] ?? string.Empty, ToInt(c[3]) ?? 0, ToInt(c[4]), ToInt(c[5]), ToInt(c[6]) ?? 0, c[7]);
            var identity = c[9] == "YES" ? "IDENTITY" : c[10] == "YES" ? "VIRTUAL" : null;

            // An identity column's default is the sequence Oracle generated for it: noise.
            var defaultValue = identity == "IDENTITY" ? null : c[14]?.Trim();
            var keys = string.Join(", ", new[] { c[11], c[12] }.OfType<string>());
            table.Add([c[0], c[1], dataType, DetailTable.Flag(c[8] == "Y"), defaultValue, identity, keys.Length == 0 ? null : keys, c[13]]);
        }

        return table.Build();
    }

    private async Task<ObjectDetailSection?> ConstraintsSectionAsync((string, string?)[] key, CancellationToken cancellationToken)
    {
        // NOT NULL constraints Oracle named itself are shown as the column's "Nullable", not here.
        const string Sql = """
            SELECT k.constraint_name, k.constraint_type, k.search_condition_vc, k.status, k.validated,
                   (SELECT LISTAGG(cc.column_name, ', ') WITHIN GROUP (ORDER BY cc.position)
                    FROM all_cons_columns cc WHERE cc.owner = k.owner AND cc.constraint_name = k.constraint_name AND cc.table_name = k.table_name)
            FROM all_constraints k
            WHERE k.owner = :owner AND k.table_name = :name AND k.constraint_type IN ('P', 'U', 'C')
              AND NOT (k.constraint_type = 'C' AND k.generated = 'GENERATED NAME' AND k.search_condition_vc LIKE '"%" IS NOT NULL')
            ORDER BY CASE k.constraint_type WHEN 'P' THEN 1 WHEN 'U' THEN 2 ELSE 3 END, k.constraint_name
            """;
        var table = new DetailTable(
            ObjectDetailKeys.Constraints,
            "Constraints",
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.Kind, "Kind"),
            (ObjectDetailKeys.ColumnList, "Columns"),
            (ObjectDetailKeys.Definition, "Condition"),
            (ObjectDetailKeys.Status, "Status"));
        foreach (var k in await RowsAsync(Sql, key, cancellationToken).ConfigureAwait(false))
        {
            var kind = k[1] switch { "P" => "PRIMARY KEY", "U" => "UNIQUE", _ => "CHECK" };
            table.Add([k[0], kind, k[5], k[2], Status(k[3], k[4])], source: k[2]);
        }

        return table.Build();
    }

    private async Task<ObjectDetailSection?> ForeignKeysSectionAsync(string owner, (string, string?)[] key, CancellationToken cancellationToken)
    {
        const string Sql = $"""
            SELECT k.constraint_name, r.owner, r.table_name, k.delete_rule, k.status, k.validated, {ForeignKeyColumns}
            FROM all_constraints k
            JOIN all_constraints r ON r.owner = k.r_owner AND r.constraint_name = k.r_constraint_name
            WHERE k.owner = :owner AND k.table_name = :name AND k.constraint_type = 'R'
            ORDER BY k.constraint_name
            """;
        var table = new DetailTable(
            ObjectDetailKeys.ForeignKeys,
            "Foreign keys",
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.ColumnList, "Columns"),
            (ObjectDetailKeys.Table, "Referenced table"),
            (ObjectDetailKeys.ReferencedColumns, "Referenced columns"),
            (ObjectDetailKeys.OnDelete, "On delete"),
            (ObjectDetailKeys.Status, "Status"));
        foreach (var k in await RowsAsync(Sql, key, cancellationToken).ConfigureAwait(false))
        {
            table.Add(
                [k[0], k[6], Qualify(owner, k[1], k[2]!), k[7], k[3], Status(k[4], k[5])],
                new DatabaseObject(new DatabaseObjectName(null, k[1], k[2]!), DatabaseObjectKinds.Table));
        }

        return table.Build();
    }

    // The foreign key's own columns, then the columns of the key it refers to, in the same order.
    private const string ForeignKeyColumns = """
        (SELECT LISTAGG(cc.column_name, ', ') WITHIN GROUP (ORDER BY cc.position) FROM all_cons_columns cc WHERE cc.owner = k.owner AND cc.constraint_name = k.constraint_name),
        (SELECT LISTAGG(rc.column_name, ', ') WITHIN GROUP (ORDER BY rc.position) FROM all_cons_columns rc WHERE rc.owner = r.owner AND rc.constraint_name = r.constraint_name)
        """;

    private async Task<ObjectDetailSection?> ReferencesSectionAsync(string owner, (string, string?)[] key, CancellationToken cancellationToken)
    {
        const string Sql = $"""
            SELECT k.constraint_name, k.owner, k.table_name, k.delete_rule, k.status, k.validated, {ForeignKeyColumns}
            FROM all_constraints r
            JOIN all_constraints k ON k.r_owner = r.owner AND k.r_constraint_name = r.constraint_name AND k.constraint_type = 'R'
            WHERE r.owner = :owner AND r.table_name = :name AND r.constraint_type IN ('P', 'U')
            ORDER BY k.owner, k.table_name, k.constraint_name
            """;
        var table = new DetailTable(
            ObjectDetailKeys.References,
            "References",
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.Table, "Table"),
            (ObjectDetailKeys.ColumnList, "Columns"),
            (ObjectDetailKeys.ReferencedColumns, "Referenced columns"),
            (ObjectDetailKeys.OnDelete, "On delete"),
            (ObjectDetailKeys.Status, "Status"));
        foreach (var k in await RowsAsync(Sql, key, cancellationToken).ConfigureAwait(false))
        {
            table.Add(
                [k[0], Qualify(owner, k[1], k[2]!), k[6], k[7], k[3], Status(k[4], k[5])],
                new DatabaseObject(new DatabaseObjectName(null, k[1], k[2]!), DatabaseObjectKinds.Table));
        }

        return table.Build();
    }

    private async Task<ObjectDetailSection?> IndexesSectionAsync((string, string?)[] key, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT i.index_name, i.index_type, i.uniqueness, i.status, i.tablespace_name,
                   (SELECT LISTAGG(ic.column_name || CASE WHEN ic.descend = 'DESC' THEN ' DESC' END, ', ') WITHIN GROUP (ORDER BY ic.column_position)
                    FROM all_ind_columns ic WHERE ic.index_owner = i.owner AND ic.index_name = i.index_name)
            FROM all_indexes i
            WHERE i.table_owner = :owner AND i.table_name = :name AND i.index_type <> 'LOB'
            ORDER BY i.index_name
            """;
        var table = new DetailTable(
            ObjectDetailKeys.Indexes,
            "Indexes",
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.Kind, "Kind"),
            (ObjectDetailKeys.Unique, "Unique"),
            (ObjectDetailKeys.ColumnList, "Columns"),
            (ObjectDetailKeys.Tablespace, "Tablespace"),
            (ObjectDetailKeys.Status, "Status"));
        foreach (var i in await RowsAsync(Sql, key, cancellationToken).ConfigureAwait(false))
        {
            table.Add([i[0], i[1], DetailTable.Flag(i[2] == "UNIQUE"), i[5], i[4], i[3]]);
        }

        return table.Build();
    }

    private async Task<ObjectDetailSection?> TriggersSectionAsync((string, string?)[] key, CancellationToken cancellationToken)
    {
        // TRIGGER_BODY is a LONG; the command fetches all of it (InitialLONGFetchSize).
        const string Sql = """
            SELECT t.owner, t.trigger_name, t.triggering_event, t.trigger_type, t.status, t.description, t.trigger_body
            FROM all_triggers t
            WHERE t.table_owner = :owner AND t.table_name = :name
            ORDER BY t.trigger_name
            """;
        var table = new DetailTable(
            ObjectDetailKeys.Triggers,
            "Triggers",
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.Events, "Events"),
            (ObjectDetailKeys.Kind, "Kind"),
            (ObjectDetailKeys.Status, "Status"));
        foreach (var t in await RowsAsync(Sql, key, cancellationToken).ConfigureAwait(false))
        {
            var source = t[5] is { } description ? "CREATE OR REPLACE TRIGGER " + description.TrimEnd() + "\n" + t[6]?.TrimEnd() : t[6];
            table.Add(
                [t[1], t[2]?.Trim(), t[3], t[4]],
                new DatabaseObject(new DatabaseObjectName(null, t[0], t[1]!), DatabaseObjectKinds.Trigger),
                source);
        }

        return table.Build();
    }

    private async Task<ObjectDetailSection?> ParametersSectionAsync((string, string?)[] key, bool package, CancellationToken cancellationToken)
    {
        // Position 0 is a function's return value. A routine without parameters has one row without a name.
        var sql = $"""
            SELECT a.object_name, TO_CHAR(a.overload), TO_CHAR(a.position), a.argument_name, a.data_type, a.type_owner, a.type_name, a.type_subname, a.in_out, a.defaulted
            FROM all_arguments a
            WHERE a.owner = :owner AND {(package ? "a.package_name = :name" : "a.object_name = :name AND a.package_name IS NULL")}
              AND a.data_level = 0 AND (a.argument_name IS NOT NULL OR a.position = 0) AND a.data_type IS NOT NULL
            ORDER BY a.subprogram_id, a.overload, a.position
            """;
        (string Key, string Title)[] columns =
        [
            (ObjectDetailKeys.Position, "#"),
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.DataType, "Data type"),
            (ObjectDetailKeys.Direction, "Direction"),
            (ObjectDetailKeys.Default, "Has default"),
        ];
        var table = package
            ? new DetailTable(ObjectDetailKeys.Parameters, "Parameters", [("routine", "Routine"), .. columns])
            : new DetailTable(ObjectDetailKeys.Parameters, "Parameters", columns);
        foreach (var a in await RowsAsync(sql, key, cancellationToken).ConfigureAwait(false))
        {
            var dataType = a[6] is { } typeName ? (a[5] is { } typeOwner ? typeOwner + "." : string.Empty) + typeName + (a[7] is { } sub ? "." + sub : string.Empty) : a[4];
            var isReturn = a[2] == "0";
            string?[] cells = [a[2], isReturn ? "RETURN" : a[3], dataType, isReturn ? "RETURN" : a[8], DetailTable.Flag(a[9] == "Y")];
            table.Add(package ? [a[0] + (a[1] is { } overload ? " (" + overload + ")" : string.Empty), .. cells] : cells);
        }

        return table.Build();
    }

    private async Task SequencePropertiesAsync(DetailProperties properties, (string, string?)[] key, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT TO_CHAR(s.min_value), TO_CHAR(s.max_value), TO_CHAR(s.increment_by), s.cycle_flag, s.order_flag, TO_CHAR(s.cache_size), TO_CHAR(s.last_number)
            FROM all_sequences s WHERE s.sequence_owner = :owner AND s.sequence_name = :name
            """;
        if (await RowsAsync(Sql, key, cancellationToken).ConfigureAwait(false) is not [var s, ..])
        {
            return;
        }

        properties.Add("increment", "Increment by", s[2])
            .Add("minValue", "Min value", s[0])
            .Add("maxValue", "Max value", s[1])
            .Add("cycle", "Cycle", s[3] == "Y" ? "YES" : "NO")
            .Add("order", "Order", s[4] == "Y" ? "YES" : "NO")
            .Add("cache", "Cache", s[5])
            .Add("lastValue", "Last number", s[6]);
    }

    private async Task<ObjectDetailSection?> DependenciesSectionAsync(string owner, string name, string type, CancellationToken cancellationToken)
    {
        // Every PL/SQL unit depends on SYS.STANDARD; that says nothing.
        const string Sql = """
            SELECT 'Uses', d.referenced_owner, d.referenced_name, d.referenced_type
            FROM all_dependencies d
            WHERE d.owner = :owner AND d.name = :name AND d.type = :type
              AND NOT (d.referenced_owner = 'SYS' AND d.referenced_name IN ('STANDARD', 'DBMS_STANDARD', 'SYS_STUB_FOR_PURITY_ANALYSIS'))
            UNION ALL
            SELECT 'Used by', d.owner, d.name, d.type
            FROM all_dependencies d
            WHERE d.referenced_owner = :owner AND d.referenced_name = :name AND d.referenced_type = :type
            ORDER BY 1 DESC, 2, 3
            """;
        var table = new DetailTable(
            ObjectDetailKeys.Dependencies,
            "Dependencies",
            (ObjectDetailKeys.Direction, "Direction"),
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.Kind, "Type"));
        foreach (var d in await RowsAsync(Sql, [("owner", owner), ("name", name), ("type", type)], cancellationToken).ConfigureAwait(false))
        {
            var kind = ObjectTypes.FirstOrDefault(t => t.Type == d[3]).Kind;
            table.Add(
                [d[0], Qualify(owner, d[1], d[2]!), d[3]],
                kind == DatabaseObjectKinds.None ? null : new DatabaseObject(new DatabaseObjectName(null, d[1], d[2]!), kind));
        }

        return table.Build();
    }

    /// <summary>NAME within the same schema, OWNER.NAME otherwise.</summary>
    private static string Qualify(string currentOwner, string? owner, string name) =>
        owner is null || string.Equals(owner, currentOwner, StringComparison.Ordinal) ? name : owner + "." + name;

    private static string? Status(string? status, string? validated) =>
        validated == "NOT VALIDATED" ? status + " (NOT VALIDATED)" : status;

    private static int? ToInt(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>A byte count as KB/MB/GB.</summary>
    internal static string? FormatBytes(string? bytes)
    {
        if (!decimal.TryParse(bytes, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(unit == 0 ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.#", CultureInfo.InvariantCulture))} {units[unit]}");
    }

    /// <summary>Every column as text; the SQL converts numbers and dates with TO_CHAR.</summary>
    private async Task<List<string?[]>> RowsAsync(string sql, (string Name, string? Value)[] parameters, CancellationToken cancellationToken)
    {
        var rows = new List<string?[]>();
        await foreach (var row in QueryAsync(
                           sql,
                           parameters,
                           static r =>
                           {
                               var cells = new string?[r.FieldCount];
                               for (var i = 0; i < cells.Length; i++)
                               {
                                   cells[i] = r.IsDBNull(i) ? null : r.GetString(i);
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
