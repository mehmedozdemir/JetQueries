using System.Globalization;
using JetQueries.Providers.Metadata;
using JetQueries.Sdk;
using Npgsql;

namespace JetQueries.Provider.PostgreSql;

/// <summary>
/// The object details view for PostgreSQL, read from <c>pg_catalog</c> with parameters. Row counts are the
/// planner's estimate (<c>pg_class.reltuples</c>); no user table is scanned.
/// </summary>
internal sealed partial class PostgreSqlMetadataProvider : IObjectDetailsProvider
{
    public async Task<ObjectDetails?> GetDetailsAsync(DatabaseObject target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Kind switch
        {
            DatabaseObjectKinds.Table or DatabaseObjectKinds.View or DatabaseObjectKinds.Sequence => await RelationDetailsAsync(target, cancellationToken).ConfigureAwait(false),
            DatabaseObjectKinds.Function or DatabaseObjectKinds.Procedure => await RoutineDetailsAsync(target, cancellationToken).ConfigureAwait(false),
            DatabaseObjectKinds.Type => await TypeDetailsAsync(target, cancellationToken).ConfigureAwait(false),
            DatabaseObjectKinds.Schema => await SchemaDetailsAsync(target, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    private async Task<ObjectDetails?> RelationDetailsAsync(DatabaseObject target, CancellationToken cancellationToken)
    {
        const string Sql = $"""
            SELECT c.oid, n.nspname, c.relname, c.relkind::text, pg_get_userbyid(c.relowner),
                   CASE WHEN c.reltuples >= 0 AND c.relkind IN ('r', 'p', 'm') THEN c.reltuples::bigint::text END,
                   CASE WHEN c.relkind IN ('r', 'p', 'm') THEN pg_size_pretty(pg_total_relation_size(c.oid)) END,
                   t.spcname, obj_description(c.oid, 'pg_class'), c.relpersistence::text
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_tablespace t ON t.oid = c.reltablespace
            WHERE c.oid = {Relation}
            """;
        var found = await RowsAsync(Sql, [Text("schema", target.Name.Schema), Text("name", target.Name.Name)], cancellationToken).ConfigureAwait(false);
        if (found is not [var row, ..])
        {
            return null;
        }

        var kind = (string)row[3]!;
        var expected = kind switch
        {
            "r" or "p" or "f" => DatabaseObjectKinds.Table,
            "v" or "m" => DatabaseObjectKinds.View,
            "S" => DatabaseObjectKinds.Sequence,
            _ => DatabaseObjectKinds.None,
        };
        if (expected != target.Kind)
        {
            return null;
        }

        var oid = (uint)row[0]!;
        var schema = (string)row[1]!;
        var name = (string)row[2]!;
        var properties = new DetailProperties()
            .Add(ObjectDetailKeys.ObjectType, "Type", kind switch
            {
                "r" => "TABLE",
                "p" => "PARTITIONED TABLE",
                "f" => "FOREIGN TABLE",
                "v" => "VIEW",
                "m" => "MATERIALIZED VIEW",
                _ => "SEQUENCE",
            })
            .Add(ObjectDetailKeys.Schema, "Schema", schema)
            .Add("owner", "Owner", (string?)row[4])
            .Add(ObjectDetailKeys.Rows, "Rows (estimate)", (string?)row[5])
            .Add(ObjectDetailKeys.Size, "Size", (string?)row[6])
            .Add(ObjectDetailKeys.Tablespace, "Tablespace", (string?)row[7])
            .Add("persistence", "Persistence", (string?)row[9] switch { "u" => "UNLOGGED", "t" => "TEMPORARY", _ => null })
            .Add(ObjectDetailKeys.Comment, "Comment", (string?)row[8]);

        var sections = new List<ObjectDetailSection?>();
        if (target.Kind == DatabaseObjectKinds.Sequence)
        {
            await SequencePropertiesAsync(properties, oid, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            sections.Add(await ColumnsSectionAsync(oid, cancellationToken).ConfigureAwait(false));
            if (target.Kind == DatabaseObjectKinds.Table)
            {
                sections.Add(await ConstraintsSectionAsync(oid, cancellationToken).ConfigureAwait(false));
                sections.Add(await ForeignKeysSectionAsync(oid, schema, references: false, cancellationToken).ConfigureAwait(false));
                sections.Add(await ForeignKeysSectionAsync(oid, schema, references: true, cancellationToken).ConfigureAwait(false));
            }

            if (kind is not "v")
            {
                sections.Add(await IndexesSectionAsync(oid, cancellationToken).ConfigureAwait(false));
            }

            sections.Add(await TriggersSectionAsync(oid, cancellationToken).ConfigureAwait(false));
            sections.Add(await RelationDependenciesAsync(oid, schema, cancellationToken).ConfigureAwait(false));
        }

        return new ObjectDetails(
            new DatabaseObject(new DatabaseObjectName(null, schema, name), target.Kind),
            properties.Build(),
            [.. sections.OfType<ObjectDetailSection>()]);
    }

    private async Task<ObjectDetailSection?> ColumnsSectionAsync(uint oid, CancellationToken cancellationToken)
    {
        // The collation is shown only where it differs from the type's default.
        const string Sql = """
            SELECT a.attnum::int, a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull, pg_get_expr(d.adbin, d.adrelid),
                   a.attidentity::text, a.attgenerated::text,
                   EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conrelid = a.attrelid AND k.contype = 'p' AND a.attnum = ANY(k.conkey)),
                   EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conrelid = a.attrelid AND k.contype = 'f' AND a.attnum = ANY(k.conkey)),
                   CASE WHEN a.attcollation <> 0 AND a.attcollation <> t.typcollation THEN co.collname END,
                   col_description(a.attrelid, a.attnum)
            FROM pg_attribute a
            JOIN pg_type t ON t.oid = a.atttypid
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            LEFT JOIN pg_collation co ON co.oid = a.attcollation
            WHERE a.attrelid = @oid AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """;
        var table = new DetailTable(
            ObjectDetailKeys.Columns,
            "Columns",
            (ObjectDetailKeys.Position, "#"),
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.DataType, "Data type"),
            (ObjectDetailKeys.Nullable, "Nullable"),
            (ObjectDetailKeys.Default, "Default"),
            (ObjectDetailKeys.Identity, "Identity / generated"),
            (ObjectDetailKeys.Key, "Key"),
            (ObjectDetailKeys.Collation, "Collation"),
            (ObjectDetailKeys.Comment, "Comment"));
        foreach (var c in await RowsAsync(Sql, [Oid("oid", oid)], cancellationToken).ConfigureAwait(false))
        {
            var identity = (string?)c[5] switch
            {
                "a" => "GENERATED ALWAYS AS IDENTITY",
                "d" => "GENERATED BY DEFAULT AS IDENTITY",
                _ => (string?)c[6] == "s" ? "GENERATED ALWAYS (STORED)" : null,
            };
            var keys = string.Join(", ", new[] { (bool)c[7]! ? "PK" : null, (bool)c[8]! ? "FK" : null }.OfType<string>());
            table.Add([
                ((int)c[0]!).ToString(CultureInfo.InvariantCulture),
                (string?)c[1],
                (string?)c[2],
                DetailTable.Flag((bool)c[3]!),
                (string?)c[4],
                identity,
                keys.Length == 0 ? null : keys,
                (string?)c[9],
                (string?)c[10],
            ]);
        }

        return table.Build();
    }

    // The names of a constraint's columns, in the constraint's order; "keys" is conkey or confkey, "rel" the table.
    private static string ColumnNames(string keys, string rel) =>
        $"(SELECT string_agg(ca.attname, ', ' ORDER BY ck.ord) FROM unnest({keys}) WITH ORDINALITY ck(attnum, ord) JOIN pg_attribute ca ON ca.attrelid = {rel} AND ca.attnum = ck.attnum)";

    private async Task<ObjectDetailSection?> ConstraintsSectionAsync(uint oid, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT k.conname, k.contype::text, {ColumnNames("k.conkey", "k.conrelid")}, pg_get_constraintdef(k.oid, true), k.convalidated
            FROM pg_constraint k
            WHERE k.conrelid = @oid AND k.contype IN ('p', 'u', 'c', 'x')
            ORDER BY CASE k.contype WHEN 'p' THEN 0 WHEN 'u' THEN 1 WHEN 'c' THEN 2 ELSE 3 END, k.conname
            """;
        var table = new DetailTable(
            ObjectDetailKeys.Constraints,
            "Constraints",
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.Kind, "Kind"),
            (ObjectDetailKeys.ColumnList, "Columns"),
            (ObjectDetailKeys.Definition, "Definition"),
            (ObjectDetailKeys.Status, "Status"));
        foreach (var k in await RowsAsync(sql, [Oid("oid", oid)], cancellationToken).ConfigureAwait(false))
        {
            var kind = (string?)k[1] switch { "p" => "PRIMARY KEY", "u" => "UNIQUE", "c" => "CHECK", _ => "EXCLUDE" };
            var definition = (string?)k[3];
            table.Add([(string?)k[0], kind, (string?)k[2], definition, (bool)k[4]! ? "VALID" : "NOT VALID"], source: definition);
        }

        return table.Build();
    }

    /// <summary>The table's foreign keys, or (<paramref name="references"/>) the foreign keys of other tables that refer to it.</summary>
    private async Task<ObjectDetailSection?> ForeignKeysSectionAsync(uint oid, string schema, bool references, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT k.conname, n.nspname, c.relname, {ColumnNames("k.conkey", "k.conrelid")}, {ColumnNames("k.confkey", "k.confrelid")},
                   k.confdeltype::text, k.confupdtype::text, k.convalidated
            FROM pg_constraint k
            JOIN pg_class c ON c.oid = {(references ? "k.conrelid" : "k.confrelid")}
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE k.contype = 'f' AND {(references ? "k.confrelid" : "k.conrelid")} = @oid
            ORDER BY n.nspname, c.relname, k.conname
            """;
        var table = references
            ? new DetailTable(
                ObjectDetailKeys.References,
                "References",
                (ObjectDetailKeys.Name, "Name"),
                (ObjectDetailKeys.Table, "Table"),
                (ObjectDetailKeys.ColumnList, "Columns"),
                (ObjectDetailKeys.ReferencedColumns, "Referenced columns"),
                (ObjectDetailKeys.OnDelete, "On delete"),
                (ObjectDetailKeys.OnUpdate, "On update"),
                (ObjectDetailKeys.Status, "Status"))
            : new DetailTable(
                ObjectDetailKeys.ForeignKeys,
                "Foreign keys",
                (ObjectDetailKeys.Name, "Name"),
                (ObjectDetailKeys.ColumnList, "Columns"),
                (ObjectDetailKeys.Table, "Referenced table"),
                (ObjectDetailKeys.ReferencedColumns, "Referenced columns"),
                (ObjectDetailKeys.OnDelete, "On delete"),
                (ObjectDetailKeys.OnUpdate, "On update"),
                (ObjectDetailKeys.Status, "Status"));
        foreach (var k in await RowsAsync(sql, [Oid("oid", oid)], cancellationToken).ConfigureAwait(false))
        {
            var otherSchema = (string)k[1]!;
            var otherTable = (string)k[2]!;
            var shown = otherSchema == schema ? otherTable : otherSchema + "." + otherTable;
            var status = (bool)k[7]! ? "VALID" : "NOT VALID";
            var other = new DatabaseObject(new DatabaseObjectName(null, otherSchema, otherTable), DatabaseObjectKinds.Table);
            table.Add(
                references
                    ? [(string?)k[0], shown, (string?)k[3], (string?)k[4], Rule((string?)k[5]), Rule((string?)k[6]), status]
                    : [(string?)k[0], (string?)k[3], shown, (string?)k[4], Rule((string?)k[5]), Rule((string?)k[6]), status],
                other);
        }

        return table.Build();

        static string? Rule(string? code) => code switch
        {
            "a" => "NO ACTION",
            "r" => "RESTRICT",
            "c" => "CASCADE",
            "n" => "SET NULL",
            "d" => "SET DEFAULT",
            _ => null,
        };
    }

    private async Task<ObjectDetailSection?> IndexesSectionAsync(uint oid, CancellationToken cancellationToken)
    {
        // pg_get_indexdef(index, n, true) writes key n, column or expression; keys after indnkeyatts are INCLUDE columns.
        const string Sql = """
            SELECT ic.relname, am.amname, i.indisprimary, i.indisunique, i.indisvalid,
                   (SELECT string_agg(pg_get_indexdef(i.indexrelid, k, true), ', ' ORDER BY k) FROM generate_series(1, i.indnkeyatts::int) k),
                   (SELECT string_agg(pg_get_indexdef(i.indexrelid, k, true), ', ' ORDER BY k) FROM generate_series(i.indnkeyatts::int + 1, i.indnatts::int) k),
                   pg_get_expr(i.indpred, i.indrelid, true), pg_get_indexdef(i.indexrelid)
            FROM pg_index i
            JOIN pg_class ic ON ic.oid = i.indexrelid
            JOIN pg_am am ON am.oid = ic.relam
            WHERE i.indrelid = @oid
            ORDER BY i.indisprimary DESC, ic.relname
            """;
        var table = new DetailTable(
            ObjectDetailKeys.Indexes,
            "Indexes",
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.Kind, "Kind"),
            (ObjectDetailKeys.Unique, "Unique"),
            (ObjectDetailKeys.ColumnList, "Columns"),
            (ObjectDetailKeys.Included, "Included"),
            (ObjectDetailKeys.Filter, "Filter"),
            (ObjectDetailKeys.Status, "Status"));
        foreach (var i in await RowsAsync(Sql, [Oid("oid", oid)], cancellationToken).ConfigureAwait(false))
        {
            var kind = ((string?)i[1])?.ToUpperInvariant() + ((bool)i[2]! ? " (PRIMARY KEY)" : string.Empty);
            table.Add(
                [(string?)i[0], kind, DetailTable.Flag((bool)i[3]!), (string?)i[5], (string?)i[6], (string?)i[7], (bool)i[4]! ? "VALID" : "INVALID"],
                source: (string?)i[8]);
        }

        return table.Build();
    }

    private async Task<ObjectDetailSection?> TriggersSectionAsync(uint oid, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT t.tgname, t.tgtype::int, t.tgenabled::text, pg_get_triggerdef(t.oid, true)
            FROM pg_trigger t
            WHERE t.tgrelid = @oid AND NOT t.tgisinternal
            ORDER BY t.tgname
            """;
        var table = new DetailTable(
            ObjectDetailKeys.Triggers,
            "Triggers",
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.Events, "Events"),
            (ObjectDetailKeys.Kind, "Kind"),
            (ObjectDetailKeys.Status, "Status"));
        foreach (var t in await RowsAsync(Sql, [Oid("oid", oid)], cancellationToken).ConfigureAwait(false))
        {
            var (events, kind) = DescribeTrigger((int)t[1]!);
            table.Add([(string?)t[0], events, kind, (string?)t[2] == "D" ? "DISABLED" : "ENABLED"], source: (string?)t[3]);
        }

        return table.Build();
    }

    /// <summary>pg_trigger.tgtype bits: 1 row, 2 before, 4 insert, 8 delete, 16 update, 32 truncate, 64 instead of.</summary>
    internal static (string Events, string Kind) DescribeTrigger(int type)
    {
        var events = new List<string>();
        if ((type & 4) != 0)
        {
            events.Add("INSERT");
        }

        if ((type & 16) != 0)
        {
            events.Add("UPDATE");
        }

        if ((type & 8) != 0)
        {
            events.Add("DELETE");
        }

        if ((type & 32) != 0)
        {
            events.Add("TRUNCATE");
        }

        var timing = (type & 64) != 0 ? "INSTEAD OF" : (type & 2) != 0 ? "BEFORE" : "AFTER";
        return (string.Join(" OR ", events), timing + ((type & 1) != 0 ? " EACH ROW" : " STATEMENT"));
    }

    /// <summary>Views that use the relation and, for a view, the relations it uses (through its rewrite rule).</summary>
    private async Task<ObjectDetailSection?> RelationDependenciesAsync(uint oid, string schema, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT DISTINCT 'Used by' AS direction, n.nspname, v.relname, v.relkind::text
            FROM pg_depend d
            JOIN pg_rewrite r ON r.oid = d.objid
            JOIN pg_class v ON v.oid = r.ev_class
            JOIN pg_namespace n ON n.oid = v.relnamespace
            WHERE d.classid = 'pg_rewrite'::regclass AND d.refclassid = 'pg_class'::regclass AND d.refobjid = @oid AND v.oid <> @oid
            UNION
            SELECT DISTINCT 'Uses', n.nspname, c.relname, c.relkind::text
            FROM pg_rewrite r
            JOIN pg_depend d ON d.objid = r.oid AND d.classid = 'pg_rewrite'::regclass AND d.refclassid = 'pg_class'::regclass
            JOIN pg_class c ON c.oid = d.refobjid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE r.ev_class = @oid AND c.oid <> @oid
            ORDER BY 1 DESC, 2, 3
            """;
        var table = new DetailTable(
            ObjectDetailKeys.Dependencies,
            "Dependencies",
            (ObjectDetailKeys.Direction, "Direction"),
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.Kind, "Type"));
        foreach (var d in await RowsAsync(Sql, [Oid("oid", oid)], cancellationToken).ConfigureAwait(false))
        {
            var otherSchema = (string)d[1]!;
            var otherName = (string)d[2]!;
            var (kind, text) = (string?)d[3] switch
            {
                "v" => (DatabaseObjectKinds.View, "VIEW"),
                "m" => (DatabaseObjectKinds.View, "MATERIALIZED VIEW"),
                "S" => (DatabaseObjectKinds.Sequence, "SEQUENCE"),
                _ => (DatabaseObjectKinds.Table, "TABLE"),
            };
            table.Add(
                [(string?)d[0], otherSchema == schema ? otherName : otherSchema + "." + otherName, text],
                new DatabaseObject(new DatabaseObjectName(null, otherSchema, otherName), kind));
        }

        return table.Build();
    }

    private async Task SequencePropertiesAsync(DetailProperties properties, uint oid, CancellationToken cancellationToken)
    {
        // pg_sequences.last_value is null until nextval was called, or without the privilege to read it.
        const string Sql = """
            SELECT format_type(s.seqtypid, NULL), s.seqstart::text, s.seqincrement::text, s.seqmin::text, s.seqmax::text, s.seqcache::text, s.seqcycle,
                   (SELECT q.last_value::text FROM pg_sequences q JOIN pg_class c ON c.relname = q.sequencename JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = q.schemaname
                    WHERE c.oid = s.seqrelid)
            FROM pg_sequence s
            WHERE s.seqrelid = @oid
            """;
        if (await RowsAsync(Sql, [Oid("oid", oid)], cancellationToken).ConfigureAwait(false) is not [var s, ..])
        {
            return;
        }

        properties.Add(ObjectDetailKeys.DataType, "Data type", (string?)s[0])
            .Add("start", "Start", (string?)s[1])
            .Add("increment", "Increment by", (string?)s[2])
            .Add("minValue", "Min value", (string?)s[3])
            .Add("maxValue", "Max value", (string?)s[4])
            .Add("cache", "Cache", (string?)s[5])
            .Add("cycle", "Cycle", (bool)s[6]! ? "YES" : "NO")
            .Add("lastValue", "Last value", (string?)s[7]);
    }

    /// <summary>A routine's properties from its first overload; parameters of every overload.</summary>
    private async Task<ObjectDetails?> RoutineDetailsAsync(DatabaseObject target, CancellationToken cancellationToken)
    {
        // Every overload from the first schema of the search path that has the name (or the given schema), as GetDefinitionAsync reads them.
        const string Sql = """
            SELECT p.oid, n.nspname, p.proname, l.lanname, pg_get_function_result(p.oid), pg_get_userbyid(p.proowner),
                   p.provolatile::text, p.prosecdef, obj_description(p.oid, 'pg_proc'), pg_get_function_identity_arguments(p.oid)
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            JOIN pg_language l ON l.oid = p.prolang
            WHERE p.proname = @name AND p.prokind::text = @kind
              AND (n.nspname = @schema OR (@schema IS NULL AND n.nspname = ANY(current_schemas(false))))
            ORDER BY array_position(current_schemas(false), n.nspname::text), p.oid
            """;
        var routines = await RowsAsync(
            Sql,
            [Text("schema", target.Name.Schema), Text("name", target.Name.Name), Text("kind", target.Kind == DatabaseObjectKinds.Procedure ? "p" : "f")],
            cancellationToken).ConfigureAwait(false);
        if (routines is not [var first, ..])
        {
            return null;
        }

        var schema = (string)first[1]!;
        routines = [.. routines.Where(r => (string)r[1]! == schema)];
        var properties = new DetailProperties()
            .Add(ObjectDetailKeys.ObjectType, "Type", target.Kind == DatabaseObjectKinds.Procedure ? "PROCEDURE" : "FUNCTION")
            .Add(ObjectDetailKeys.Schema, "Schema", schema)
            .Add("owner", "Owner", (string?)first[5])
            .Add("language", "Language", (string?)first[3])
            .Add("returns", "Returns", target.Kind == DatabaseObjectKinds.Function ? (string?)first[4] : null)
            .Add("volatility", "Volatility", target.Kind == DatabaseObjectKinds.Function
                ? (string?)first[6] switch { "i" => "IMMUTABLE", "s" => "STABLE", _ => "VOLATILE" }
                : null)
            .Add("security", "Security", (bool)first[7]! ? "DEFINER" : "INVOKER")
            .Add("overloads", "Overloads", routines.Count > 1 ? routines.Count.ToString(CultureInfo.InvariantCulture) : null)
            .Add(ObjectDetailKeys.Comment, "Comment", (string?)first[8]);

        // proallargtypes lists every argument (with OUT ones) and is null when all are IN; proargtypes then has them.
        const string ParametersSql = """
            SELECT a.ord::int, a.name, format_type(a.type, NULL), COALESCE(a.mode::text, 'i'), pg_get_function_arg_default(p.oid, a.ord::int)
            FROM pg_proc p,
                 unnest(COALESCE(p.proallargtypes, p.proargtypes::oid[]), p.proargnames, p.proargmodes) WITH ORDINALITY a(type, name, mode, ord)
            WHERE p.oid = @oid AND a.type IS NOT NULL
            ORDER BY a.ord
            """;
        (string Key, string Title)[] columns =
        [
            (ObjectDetailKeys.Position, "#"),
            (ObjectDetailKeys.Name, "Name"),
            (ObjectDetailKeys.DataType, "Data type"),
            (ObjectDetailKeys.Direction, "Direction"),
            (ObjectDetailKeys.Default, "Default"),
        ];
        var overloaded = routines.Count > 1;
        var table = overloaded
            ? new DetailTable(ObjectDetailKeys.Parameters, "Parameters", [("routine", "Overload"), .. columns])
            : new DetailTable(ObjectDetailKeys.Parameters, "Parameters", columns);
        foreach (var routine in routines)
        {
            var signature = $"{routine[2]}({routine[9]})";
            foreach (var a in await RowsAsync(ParametersSql, [Oid("oid", (uint)routine[0]!)], cancellationToken).ConfigureAwait(false))
            {
                var direction = (string?)a[3] switch { "o" => "OUT", "b" => "INOUT", "v" => "VARIADIC", "t" => "TABLE", _ => "IN" };
                string?[] cells = [((int)a[0]!).ToString(CultureInfo.InvariantCulture), (string?)a[1], (string?)a[2], direction, (string?)a[4]];
                table.Add(overloaded ? [signature, .. cells] : cells);
            }
        }

        return new ObjectDetails(
            new DatabaseObject(new DatabaseObjectName(null, schema, (string)first[2]!), target.Kind),
            properties.Build(),
            [.. new[] { table.Build() }.OfType<ObjectDetailSection>()]);
    }

    private async Task<ObjectDetails?> TypeDetailsAsync(DatabaseObject target, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT n.nspname, t.typname, t.typtype::text, pg_get_userbyid(t.typowner), format_type(t.typbasetype, t.typtypmod), obj_description(t.oid, 'pg_type'),
                   (SELECT string_agg(e.enumlabel, ', ' ORDER BY e.enumsortorder) FROM pg_enum e WHERE e.enumtypid = t.oid)
            FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE t.typname = @name AND (n.nspname = @schema OR (@schema IS NULL AND n.nspname = ANY(current_schemas(false))))
            ORDER BY array_position(current_schemas(false), n.nspname::text)
            LIMIT 1
            """;
        if (await RowsAsync(Sql, [Text("schema", target.Name.Schema), Text("name", target.Name.Name)], cancellationToken).ConfigureAwait(false) is not [var t, ..])
        {
            return null;
        }

        var kind = (string?)t[2];
        var properties = new DetailProperties()
            .Add(ObjectDetailKeys.ObjectType, "Type", kind switch { "e" => "ENUM", "d" => "DOMAIN", "c" => "COMPOSITE TYPE", "r" => "RANGE", "m" => "MULTIRANGE", _ => "TYPE" })
            .Add(ObjectDetailKeys.Schema, "Schema", (string?)t[0])
            .Add("owner", "Owner", (string?)t[3])
            .Add(ObjectDetailKeys.DataType, "Base type", kind == "d" ? (string?)t[4] : null)
            .Add("values", "Values", (string?)t[6])
            .Add(ObjectDetailKeys.Comment, "Comment", (string?)t[5]);
        return new ObjectDetails(new DatabaseObject(new DatabaseObjectName(null, (string)t[0]!, (string)t[1]!), DatabaseObjectKinds.Type), properties.Build(), []);
    }

    private async Task<ObjectDetails?> SchemaDetailsAsync(DatabaseObject target, CancellationToken cancellationToken)
    {
        const string Sql = "SELECT n.nspname, pg_get_userbyid(n.nspowner), obj_description(n.oid, 'pg_namespace') FROM pg_namespace n WHERE n.nspname = @name";
        if (await RowsAsync(Sql, [Text("name", target.Name.Name)], cancellationToken).ConfigureAwait(false) is not [var s, ..])
        {
            return null;
        }

        var properties = new DetailProperties()
            .Add(ObjectDetailKeys.ObjectType, "Type", "SCHEMA")
            .Add("owner", "Owner", (string?)s[1])
            .Add(ObjectDetailKeys.Comment, "Comment", (string?)s[2]);
        return new ObjectDetails(new DatabaseObject(new DatabaseObjectName(null, null, (string)s[0]!), DatabaseObjectKinds.Schema), properties.Build(), []);
    }

    /// <summary>Every row as its values; DBNull becomes null.</summary>
    private async Task<List<object?[]>> RowsAsync(string sql, NpgsqlParameter[] parameters, CancellationToken cancellationToken)
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
