using System.Globalization;
using JetQueries.Providers.Metadata;
using JetQueries.Sdk;

namespace JetQueries.Provider.Oracle;

/// <summary>
/// The object details view for Oracle: everything is read from ALL_* dictionary views with bind variables.
/// Row counts come from the optimizer statistics (ALL_TABLES.NUM_ROWS); no user table is ever scanned.
/// </summary>
internal sealed partial class OracleMetadataProvider : IProgressiveObjectDetailsProvider
{
    private const string DateFormat = "'YYYY-MM-DD HH24:MI:SS'";

    public Task<ObjectDetails?> GetDetailsAsync(DatabaseObject target, CancellationToken cancellationToken) =>
        GetDetailsAsync(target, null, cancellationToken);

    /// <remarks>
    /// The properties and the columns are reported as soon as they are read; the keys, references and indexes
    /// follow (on a large dictionary those take the most time).
    /// </remarks>
    public async Task<ObjectDetails?> GetDetailsAsync(DatabaseObject target, IProgress<ObjectDetails>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind == DatabaseObjectKinds.Schema)
        {
            return await SchemaDetailsAsync(target, cancellationToken).ConfigureAwait(false);
        }

        if (target.Name.Container is { } package && target.Kind is DatabaseObjectKinds.Procedure or DatabaseObjectKinds.Function)
        {
            return await MemberDetailsAsync(target, package, cancellationToken).ConfigureAwait(false);
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
        void Report() => progress?.Report(new ObjectDetails(resolved, properties.Build(), [.. sections.OfType<ObjectDetailSection>()]));

        switch (target.Kind)
        {
            case DatabaseObjectKinds.Table:
                {
                    // The columns first (one cheap view), their PK/FK marks once the constraints are read.
                    var columns = await ColumnRowsAsync(key, cancellationToken).ConfigureAwait(false);
                    sections.Add(ColumnsSection(columns, []));
                    Report();
                    var constraints = await ConstraintsAsync(key, cancellationToken).ConfigureAwait(false);
                    sections[0] = ColumnsSection(columns, constraints);
                    sections.Add(ConstraintsSection(constraints));
                    await TablePropertiesAsync(properties, key, cancellationToken).ConfigureAwait(false);
                    Report();
                    sections.Add(await ForeignKeysSectionAsync(owner, constraints, cancellationToken).ConfigureAwait(false));
                    var referencesAt = sections.Count;
                    sections.Add(await IndexesSectionAsync(key, cancellationToken).ConfigureAwait(false));
                    Report();

                    // Other tables' keys pointing here: the slowest to find, read last but shown before the indexes.
                    sections.Insert(referencesAt, await ReferencesSectionAsync(owner, constraints, cancellationToken).ConfigureAwait(false));
                    Report();
                }

                sections.Add(await TriggersSectionAsync(key, cancellationToken).ConfigureAwait(false));
                break;

            case DatabaseObjectKinds.View:
                properties.Add(ObjectDetailKeys.Comment, "Comment", await TableCommentAsync(key, cancellationToken).ConfigureAwait(false));
                sections.Add(ColumnsSection(await ColumnRowsAsync(key, cancellationToken).ConfigureAwait(false), []));
                Report();
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

    /// <summary>
    /// A routine declared in a package: the package's dates and status, the routine's parameters (each overload)
    /// and what the package depends on.
    /// </summary>
    private async Task<ObjectDetails?> MemberDetailsAsync(DatabaseObject target, string package, CancellationToken cancellationToken)
    {
        var objectSql = $"""
            SELECT o.owner, o.object_name, TO_CHAR(o.created, {DateFormat}), TO_CHAR(o.last_ddl_time, {DateFormat}), o.status
            FROM all_objects o
            WHERE o.owner = NVL(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')) AND o.object_name = :name AND o.object_type = 'PACKAGE'
            """;
        if (await RowsAsync(objectSql, [("owner", target.Name.Schema), ("name", package)], cancellationToken).ConfigureAwait(false) is not [var head, ..])
        {
            return null;
        }

        var owner = head[0]!;
        const string Sql = """
            SELECT TO_CHAR(a.overload), TO_CHAR(a.position), a.argument_name, a.data_type, a.type_owner, a.type_name, a.type_subname, a.in_out, a.defaulted
            FROM all_arguments a
            WHERE a.owner = :owner AND a.package_name = :package AND a.object_name = :member
              AND a.data_level = 0 AND (a.argument_name IS NOT NULL OR a.position = 0) AND a.data_type IS NOT NULL
            ORDER BY a.subprogram_id, a.overload, a.position
            """;
        var arguments = await RowsAsync(Sql, [("owner", owner), ("package", head[1]), ("member", target.Name.Name)], cancellationToken).ConfigureAwait(false);

        // Members of a package are found in ALL_PROCEDURES even without parameters.
        const string Exists = "SELECT TO_CHAR(COUNT(*)) FROM all_procedures p WHERE p.owner = :owner AND p.object_name = :package AND p.procedure_name = :member";
        if (arguments.Count == 0 && await RowsAsync(Exists, [("owner", owner), ("package", head[1]), ("member", target.Name.Name)], cancellationToken).ConfigureAwait(false) is [["0"]])
        {
            return null;
        }

        var overloaded = arguments.Select(static a => a[0]).Distinct().Count() > 1;
        (string Key, string Title)[] columns =
        [
            (ObjectDetailKeys.Position, "#"),
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.DataType, "Data type"),
            (ObjectDetailKeys.Direction, "Direction"),
            (ObjectDetailKeys.Default, "Has default"),
        ];
        var table = overloaded
            ? new DetailTable(ObjectDetailKeys.Parameters, "Parameters", [("overload", "Overload"), .. columns])
            : new DetailTable(ObjectDetailKeys.Parameters, "Parameters", columns);
        foreach (var a in arguments)
        {
            var dataType = a[5] is { } typeName ? (a[4] is { } typeOwner ? typeOwner + "." : string.Empty) + typeName + (a[6] is { } sub ? "." + sub : string.Empty) : a[3];
            var isReturn = a[1] == "0";
            string?[] cells = [a[1], isReturn ? "RETURN" : a[2], dataType, isReturn ? "RETURN" : a[7], DetailTable.Flag(a[8] == "Y")];
            table.Add(overloaded ? [a[0], .. cells] : cells);
        }

        var resolved = new DatabaseObject(new DatabaseObjectName(null, owner, target.Name.Name) { Container = head[1] }, target.Kind);
        var properties = new DetailProperties()
            .Add(ObjectDetailKeys.ObjectType, "Type", target.Kind == DatabaseObjectKinds.Function ? "FUNCTION" : "PROCEDURE")
            .Add(ObjectDetailKeys.Schema, "Owner", owner)
            .Add("package", "Package", head[1])
            .Add(ObjectDetailKeys.Created, "Created", head[2])
            .Add(ObjectDetailKeys.Modified, "Last DDL", head[3])
            .Add(ObjectDetailKeys.Status, "Status", head[4]);
        var dependencies = await DependenciesSectionAsync(owner, head[1]!, "PACKAGE BODY", cancellationToken).ConfigureAwait(false);
        return new ObjectDetails(resolved, properties.Build(), [.. new[] { table.Build(), dependencies }.OfType<ObjectDetailSection>()]);
    }

    /// <summary>
    /// The source of a package member: its declaration in the specification and its implementation in the body
    /// (each overload), cut from the package's source with the PL/SQL structure parser.
    /// </summary>
    private async Task<string?> MemberSourceAsync(string? owner, string package, string member, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT s.owner, s.type, s.text FROM all_source s
            WHERE s.owner = NVL(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')) AND s.name = :package AND s.type IN ('PACKAGE', 'PACKAGE BODY')
            ORDER BY s.type, s.line
            """;
        var spec = new System.Text.StringBuilder();
        var body = new System.Text.StringBuilder();
        string? schema = null;
        foreach (var row in await RowsAsync(Sql, [("owner", owner), ("package", package)], cancellationToken).ConfigureAwait(false))
        {
            schema ??= row[0];
            (row[1] == "PACKAGE" ? spec : body).Append(row[2]);
        }

        if (schema is null)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var (text, what) in new[] { (spec.ToString(), "PACKAGE"), (body.ToString(), "PACKAGE BODY") })
        {
            if (text.Length == 0)
            {
                continue;
            }

            // ALL_SOURCE starts at "PACKAGE name …": parsed as a CREATE so the routines are the package's children.
            var source = "CREATE OR REPLACE " + text;
            var script = Language.PlStructureParser.Parse(source);
            foreach (var node in script.Root.Descendants().Where(n =>
                         n.Kind is Language.PlNodeKind.Procedure or Language.PlNodeKind.Function &&
                         string.Equals(n.Name?.Trim('"'), member, StringComparison.OrdinalIgnoreCase)))
            {
                var start = node.KeywordStart >= 0 ? node.KeywordStart : node.Start;
                var end = Math.Min(node.End, source.Length);
                var code = source[start..end].TrimEnd();
                parts.Add($"-- {what} {schema}.{package}\n" + code + (code.EndsWith(';') ? string.Empty : ";"));
            }
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts) + "\n";
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

    // The dictionary views (ALL_CONSTRAINTS above all) are expensive to parse and to run nested: every query
    // here reads one view or one plain join, without scalar subqueries, and the pieces are put together in code.
    // A first look at a table on a cold instance went from tens of seconds to under one.

    /// <summary>A key, unique or check constraint, or a foreign key, of the table, with its columns in order.</summary>
    private sealed record TableConstraint(
        string Name,
        string Type,
        string? Condition,
        string? Status,
        string? Validated,
        string? DeleteRule,
        string? ReferencedOwner,
        string? ReferencedName,
        List<string> Columns);

    private async Task<List<TableConstraint>> ConstraintsAsync((string, string?)[] key, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT k.constraint_name, k.constraint_type, k.search_condition_vc, k.status, k.validated, k.delete_rule,
                   k.r_owner, k.r_constraint_name, k.generated, cc.column_name
            FROM all_constraints k
            LEFT JOIN all_cons_columns cc ON cc.owner = k.owner AND cc.constraint_name = k.constraint_name AND cc.table_name = k.table_name
            WHERE k.owner = :owner AND k.table_name = :name AND k.constraint_type IN ('P', 'U', 'C', 'R')
            ORDER BY k.constraint_name, cc.position
            """;
        var constraints = new List<TableConstraint>();
        foreach (var k in await RowsAsync(Sql, key, cancellationToken).ConfigureAwait(false))
        {
            // NOT NULL constraints Oracle named itself are shown as the column's "Nullable", not as constraints.
            if (k[1] == "C" && k[8] == "GENERATED NAME" && k[2] is { } condition
                && condition.StartsWith('"') && condition.EndsWith("\" IS NOT NULL", StringComparison.Ordinal))
            {
                continue;
            }

            if (constraints.Count == 0 || constraints[^1].Name != k[0])
            {
                constraints.Add(new TableConstraint(k[0]!, k[1]!, k[2], k[3], k[4], k[5], k[6], k[7], []));
            }

            if (k[9] is { } column)
            {
                constraints[^1].Columns.Add(column);
            }
        }

        return constraints;
    }

    private async Task<List<string?[]>> ColumnRowsAsync((string, string?)[] key, CancellationToken cancellationToken)
    {
        // ALL_TAB_COLS (not ALL_TAB_COLUMNS) tells virtual columns; hidden system columns are left out.
        const string Sql = """
            SELECT TO_CHAR(c.column_id), c.column_name, c.data_type, TO_CHAR(c.data_length), TO_CHAR(c.data_precision), TO_CHAR(c.data_scale),
                   TO_CHAR(c.char_length), c.char_used, c.nullable, c.identity_column, c.virtual_column, m.comments, c.data_default
            FROM all_tab_cols c
            LEFT JOIN all_col_comments m ON m.owner = c.owner AND m.table_name = c.table_name AND m.column_name = c.column_name
            WHERE c.owner = :owner AND c.table_name = :name AND c.hidden_column = 'NO'
            ORDER BY c.column_id
            """;
        return await RowsAsync(Sql, key, cancellationToken).ConfigureAwait(false);
    }

    private static ObjectDetailSection? ColumnsSection(List<string?[]> rows, List<TableConstraint> constraints)
    {
        var primary = constraints.Where(static k => k.Type == "P").SelectMany(static k => k.Columns).ToHashSet(StringComparer.Ordinal);
        var foreign = constraints.Where(static k => k.Type == "R").SelectMany(static k => k.Columns).ToHashSet(StringComparer.Ordinal);
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
        foreach (var c in rows)
        {
            var dataType = OracleTypeFormatter.Format(c[2] ?? string.Empty, ToInt(c[3]) ?? 0, ToInt(c[4]), ToInt(c[5]), ToInt(c[6]) ?? 0, c[7]);
            var identity = c[9] == "YES" ? "IDENTITY" : c[10] == "YES" ? "VIRTUAL" : null;

            // An identity column's default is the sequence Oracle generated for it: noise.
            var defaultValue = identity == "IDENTITY" ? null : c[12]?.Trim();
            var keys = string.Join(", ", new[] { primary.Contains(c[1]!) ? "PK" : null, foreign.Contains(c[1]!) ? "FK" : null }.OfType<string>());
            table.Add([c[0], c[1], dataType, DetailTable.Flag(c[8] == "Y"), defaultValue, identity, keys.Length == 0 ? null : keys, c[11]]);
        }

        return table.Build();
    }

    private static ObjectDetailSection? ConstraintsSection(List<TableConstraint> constraints)
    {
        var table = new DetailTable(
            ObjectDetailKeys.Constraints,
            "Constraints",
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.Kind, "Kind"),
            (ObjectDetailKeys.ColumnList, "Columns"),
            (ObjectDetailKeys.Definition, "Condition"),
            (ObjectDetailKeys.Status, "Status"));
        foreach (var k in constraints.Where(static k => k.Type != "R").OrderBy(static k => k.Type switch { "P" => 1, "U" => 2, _ => 3 }).ThenBy(static k => k.Name, StringComparer.Ordinal))
        {
            var kind = k.Type switch { "P" => "PRIMARY KEY", "U" => "UNIQUE", _ => "CHECK" };
            table.Add([k.Name, kind, Join(k.Columns), k.Condition, Status(k.Status, k.Validated)], source: k.Condition);
        }

        return table.Build();
    }

    private async Task<ObjectDetailSection?> ForeignKeysSectionAsync(string owner, List<TableConstraint> constraints, CancellationToken cancellationToken)
    {
        var foreignKeys = constraints.Where(static k => k.Type == "R" && k.ReferencedOwner is not null && k.ReferencedName is not null).OrderBy(static k => k.Name, StringComparer.Ordinal).ToList();
        if (foreignKeys.Count == 0)
        {
            return null;
        }

        // The keys they refer to: table and columns, from ALL_CONS_COLUMNS alone.
        var parameters = new List<(string, string?)>();
        var conditions = new List<string>();
        foreach (var (fk, i) in foreignKeys.Select(static (k, i) => (k, i)))
        {
            conditions.Add($"(rc.owner = :o{i} AND rc.constraint_name = :n{i})");
            parameters.Add(($"o{i}", fk.ReferencedOwner));
            parameters.Add(($"n{i}", fk.ReferencedName));
        }

        var sql = $"""
            SELECT rc.owner, rc.constraint_name, rc.table_name, rc.column_name
            FROM all_cons_columns rc
            WHERE {string.Join(" OR ", conditions)}
            ORDER BY rc.owner, rc.constraint_name, rc.position
            """;
        var referenced = new Dictionary<(string, string), (string Table, List<string> Columns)>();
        foreach (var r in await RowsAsync(sql, [.. parameters], cancellationToken).ConfigureAwait(false))
        {
            if (!referenced.TryGetValue((r[0]!, r[1]!), out var target))
            {
                referenced[(r[0]!, r[1]!)] = target = (r[2]!, []);
            }

            target.Columns.Add(r[3]!);
        }

        var table = new DetailTable(
            ObjectDetailKeys.ForeignKeys,
            "Foreign keys",
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.ColumnList, "Columns"),
            (ObjectDetailKeys.Table, "Referenced table"),
            (ObjectDetailKeys.ReferencedColumns, "Referenced columns"),
            (ObjectDetailKeys.OnDelete, "On delete"),
            (ObjectDetailKeys.Status, "Status"));
        foreach (var k in foreignKeys)
        {
            if (!referenced.TryGetValue((k.ReferencedOwner!, k.ReferencedName!), out var target))
            {
                continue;
            }

            table.Add(
                [k.Name, Join(k.Columns), Qualify(owner, k.ReferencedOwner, target.Table), Join(target.Columns), k.DeleteRule, Status(k.Status, k.Validated)],
                new DatabaseObject(new DatabaseObjectName(null, k.ReferencedOwner, target.Table), DatabaseObjectKinds.Table));
        }

        return table.Build();
    }

    private async Task<ObjectDetailSection?> ReferencesSectionAsync(string owner, List<TableConstraint> constraints, CancellationToken cancellationToken)
    {
        // Foreign keys of other tables that point at this table's primary or unique keys.
        var keys = constraints.Where(static k => k.Type is "P" or "U").ToDictionary(static k => k.Name, StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            return null;
        }

        var names = keys.Keys.Select(static (_, i) => $":k{i}").ToList();
        var sql = $"""
            SELECT k.owner, k.table_name, k.constraint_name, k.r_constraint_name, k.delete_rule, k.status, k.validated, cc.column_name
            FROM all_constraints k
            LEFT JOIN all_cons_columns cc ON cc.owner = k.owner AND cc.constraint_name = k.constraint_name AND cc.table_name = k.table_name
            WHERE k.constraint_type = 'R' AND k.r_owner = :owner AND k.r_constraint_name IN ({string.Join(", ", names)})
            ORDER BY k.owner, k.table_name, k.constraint_name, cc.position
            """;
        (string, string?)[] parameters = [("owner", owner), .. keys.Keys.Select(static (name, i) => ($"k{i}", (string?)name))];
        var table = new DetailTable(
            ObjectDetailKeys.References,
            "References",
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.Table, "Table"),
            (ObjectDetailKeys.ColumnList, "Columns"),
            (ObjectDetailKeys.ReferencedColumns, "Referenced columns"),
            (ObjectDetailKeys.OnDelete, "On delete"),
            (ObjectDetailKeys.Status, "Status"));
        string?[]? current = null;
        var columns = new List<string>();
        void Flush()
        {
            if (current is { } k)
            {
                table.Add(
                    [k[2], Qualify(owner, k[0], k[1]!), Join(columns), Join(keys[k[3]!].Columns), k[4], Status(k[5], k[6])],
                    new DatabaseObject(new DatabaseObjectName(null, k[0], k[1]!), DatabaseObjectKinds.Table));
            }

            columns.Clear();
        }

        foreach (var k in await RowsAsync(sql, parameters, cancellationToken).ConfigureAwait(false))
        {
            if (current is null || current[0] != k[0] || current[2] != k[2])
            {
                Flush();
                current = k;
            }

            if (k[7] is { } column)
            {
                columns.Add(column);
            }
        }

        Flush();
        return table.Build();
    }

    private async Task<ObjectDetailSection?> IndexesSectionAsync((string, string?)[] key, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT i.owner, i.index_name, i.index_type, i.uniqueness, i.status, i.tablespace_name, ic.column_name, ic.descend
            FROM all_indexes i
            LEFT JOIN all_ind_columns ic ON ic.index_owner = i.owner AND ic.index_name = i.index_name
            WHERE i.table_owner = :owner AND i.table_name = :name AND i.index_type <> 'LOB'
            ORDER BY i.index_name, i.owner, ic.column_position
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
        string?[]? current = null;
        var columns = new List<string>();
        void Flush()
        {
            if (current is { } i)
            {
                table.Add([i[1], i[2], DetailTable.Flag(i[3] == "UNIQUE"), Join(columns), i[5], i[4]]);
            }

            columns.Clear();
        }

        foreach (var i in await RowsAsync(Sql, key, cancellationToken).ConfigureAwait(false))
        {
            if (current is null || current[0] != i[0] || current[1] != i[1])
            {
                Flush();
                current = i;
            }

            if (i[6] is { } column)
            {
                columns.Add(column + (i[7] == "DESC" ? " DESC" : string.Empty));
            }
        }

        Flush();
        return table.Build();
    }

    private static string? Join(List<string> columns) => columns.Count == 0 ? null : string.Join(", ", columns);

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
