using System.Globalization;
using JetQueries.Providers.Metadata;
using JetQueries.Sdk;
using Microsoft.Data.SqlClient;
using K = JetQueries.Sdk.ObjectDetailKeys;

namespace JetQueries.Provider.SqlServer;

/// <summary>
/// The object details view for SQL Server: catalog views only (no DMVs, no table scans), every query
/// parameterized on the bracket-quoted name, the database part as a bracket-quoted catalog prefix.
/// </summary>
internal sealed partial class SqlServerMetadataProvider
{
    public async Task<ObjectDetails?> GetDetailsAsync(DatabaseObject target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind is DatabaseObjectKinds.Database or DatabaseObjectKinds.Schema or DatabaseObjectKinds.None)
        {
            return null;
        }

        var p = SqlServerNames.CatalogPrefix(target.Name.Database);
        var name = SqlServerNames.Quote(target.Name);
        var info = await RowsAsync(ObjectSql(p), name, cancellationToken).ConfigureAwait(false);
        if (info.Count == 0)
        {
            return null;
        }

        var o = info[0];
        var type = Text(o[0])?.Trim();
        var kind = SqlServerMetadataProvider.KindOf(type ?? string.Empty);
        var properties = new DetailProperties()
            .Add(K.ObjectType, "Type", Text(o[1])?.Replace('_', ' '))
            .Add(K.Schema, "Schema", Text(o[2]))
            .Add(K.Database, "Database", target.Name.Database)
            .Add(K.Created, "Created", Date(o[3]))
            .Add(K.Modified, "Modified", Date(o[4]));
        if (kind is DatabaseObjectKinds.Table or DatabaseObjectKinds.View)
        {
            properties
                .Add(K.Rows, "Rows (estimate)", o[5] is null ? null : Convert.ToInt64(o[5], CultureInfo.InvariantCulture).ToString("N0", CultureInfo.InvariantCulture))
                .Add(K.Size, "Size", o[6] is null ? null : SizeText(Convert.ToInt64(o[6], CultureInfo.InvariantCulture) * 8))
                .Add(K.Tablespace, "Filegroup", Text(o[8]));
        }

        properties.Add(K.Comment, "Description", Text(o[7]));
        var sections = new List<ObjectDetailSection?>();
        switch (kind)
        {
            case DatabaseObjectKinds.Table or DatabaseObjectKinds.View:
                sections.Add(await ColumnsSectionAsync(p, name, cancellationToken).ConfigureAwait(false));
                if (kind == DatabaseObjectKinds.Table)
                {
                    sections.Add(await ConstraintsSectionAsync(p, name, cancellationToken).ConfigureAwait(false));
                    var (outgoing, incoming) = await ForeignKeySectionsAsync(p, name, target.Name.Database, cancellationToken).ConfigureAwait(false);
                    sections.Add(outgoing);
                    sections.Add(incoming);
                }

                sections.Add(await IndexesSectionAsync(p, name, cancellationToken).ConfigureAwait(false));
                sections.Add(await TriggersSectionAsync(p, name, target.Name, cancellationToken).ConfigureAwait(false));
                break;
            case DatabaseObjectKinds.Procedure or DatabaseObjectKinds.Function:
                sections.Add(await ParametersSectionAsync(p, name, cancellationToken).ConfigureAwait(false));
                if (kind == DatabaseObjectKinds.Function)
                {
                    sections.Add(await ColumnsSectionAsync(p, name, cancellationToken).ConfigureAwait(false));
                }

                break;
            case DatabaseObjectKinds.Sequence:
                await AddSequenceAsync(properties, p, name, cancellationToken).ConfigureAwait(false);
                break;
            default:
                break;
        }

        if (kind is not DatabaseObjectKinds.Sequence and not DatabaseObjectKinds.Synonym)
        {
            sections.Add(await DependenciesSectionAsync(p, name, target.Name.Database, cancellationToken).ConfigureAwait(false));
        }

        var schema = Text(o[2]);
        var resolved = new DatabaseObject(target.Name with { Schema = schema ?? target.Name.Schema }, kind == DatabaseObjectKinds.None ? target.Kind : kind);
        return new ObjectDetails(resolved, properties.Build(), [.. sections.OfType<ObjectDetailSection>()]);
    }

    private static string ObjectSql(string p) => $"""
        DECLARE @id int = OBJECT_ID(@name);
        SELECT o.type, o.type_desc, s.name, o.create_date, o.modify_date,
               (SELECT SUM(pa.rows) FROM {p}sys.partitions pa WHERE pa.object_id = o.object_id AND pa.index_id IN (0, 1)),
               (SELECT SUM(au.used_pages) FROM {p}sys.partitions pa JOIN {p}sys.allocation_units au ON au.container_id = pa.partition_id
                 WHERE pa.object_id = o.object_id),
               CAST(ep.value AS nvarchar(4000)),
               (SELECT TOP (1) ds.name FROM {p}sys.indexes i JOIN {p}sys.data_spaces ds ON ds.data_space_id = i.data_space_id
                 WHERE i.object_id = o.object_id AND i.index_id IN (0, 1))
        FROM {p}sys.objects o
        JOIN {p}sys.schemas s ON s.schema_id = o.schema_id
        LEFT JOIN {p}sys.extended_properties ep ON ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = N'MS_Description'
        WHERE o.object_id = @id;
        """;

    private async Task<ObjectDetailSection?> ColumnsSectionAsync(string p, string name, CancellationToken cancellationToken)
    {
        var sql = $"""
            DECLARE @id int = OBJECT_ID(@name);
            SELECT c.column_id, c.name, t.name, bt.name, t.is_user_defined, c.max_length, c.precision, c.scale, c.is_nullable,
                   dc.definition, c.is_identity, CAST(ic.seed_value AS nvarchar(64)), CAST(ic.increment_value AS nvarchar(64)), cc.definition,
                   c.collation_name, CAST(ep.value AS nvarchar(4000)),
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM {p}sys.index_columns x JOIN {p}sys.indexes i ON i.object_id = x.object_id AND i.index_id = x.index_id
                                          WHERE i.is_primary_key = 1 AND x.object_id = c.object_id AND x.column_id = c.column_id) THEN 1 ELSE 0 END AS bit),
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM {p}sys.foreign_key_columns f
                                          WHERE f.parent_object_id = c.object_id AND f.parent_column_id = c.column_id) THEN 1 ELSE 0 END AS bit)
            FROM {p}sys.columns c
            JOIN {p}sys.types t ON t.user_type_id = c.user_type_id
            LEFT JOIN {p}sys.types bt ON bt.user_type_id = c.system_type_id
            LEFT JOIN {p}sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            LEFT JOIN {p}sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN {p}sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN {p}sys.extended_properties ep ON ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = N'MS_Description'
            WHERE c.object_id = @id
            ORDER BY c.column_id;
            """;
        var table = new DetailTable(
            K.Columns, "Columns", (K.Position, "#"), (K.Name, "Name"), (K.DataType, "Data type"), (K.Nullable, "Nullable"), (K.Default, "Default"),
            (K.Identity, "Identity / computed"), (K.Key, "Key"), (K.Collation, "Collation"), (K.Comment, "Description"));
        foreach (var r in await RowsAsync(sql, name, cancellationToken).ConfigureAwait(false))
        {
            var dataType = SqlServerTypeFormatter.Format(
                Text(r[2]) ?? string.Empty, Text(r[3]), Convert.ToInt16(r[5], CultureInfo.InvariantCulture), Convert.ToByte(r[6], CultureInfo.InvariantCulture),
                Convert.ToByte(r[7], CultureInfo.InvariantCulture), Bool(r[4]));
            var identity = Bool(r[10]) ? $"IDENTITY({Text(r[11])}, {Text(r[12])})" : r[13] is null ? null : "AS " + Text(r[13]);
            var key = string.Join(", ", new[] { Bool(r[16]) ? "PK" : null, Bool(r[17]) ? "FK" : null }.OfType<string>());
            table.Add([Text(r[0]), Text(r[1]), dataType, DetailTable.Flag(Bool(r[8])), Text(r[9]), identity, key, Text(r[14]), Text(r[15])]);
        }

        return table.Build();
    }

    private async Task<ObjectDetailSection?> ConstraintsSectionAsync(string p, string name, CancellationToken cancellationToken)
    {
        var sql = $"""
            DECLARE @id int = OBJECT_ID(@name);
            SELECT 0 AS ord, i.name COLLATE DATABASE_DEFAULT, CAST(CASE WHEN i.is_primary_key = 1 THEN 'PRIMARY KEY' ELSE 'UNIQUE' END AS nvarchar(20)) COLLATE DATABASE_DEFAULT,
                   CAST(i.type_desc AS nvarchar(max)) COLLATE DATABASE_DEFAULT,
                   STUFF((SELECT N', ' + c.name + CASE WHEN ic.is_descending_key = 1 THEN N' DESC' ELSE N'' END
                          FROM {p}sys.index_columns ic JOIN {p}sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                          WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                          ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N'') COLLATE DATABASE_DEFAULT,
                   i.is_disabled, CAST(0 AS bit)
            FROM {p}sys.indexes i
            WHERE i.object_id = @id AND (i.is_primary_key = 1 OR i.is_unique_constraint = 1)
            UNION ALL
            SELECT 1, cc.name COLLATE DATABASE_DEFAULT, N'CHECK' COLLATE DATABASE_DEFAULT, cc.definition COLLATE DATABASE_DEFAULT, c.name COLLATE DATABASE_DEFAULT,
                   cc.is_disabled, cc.is_not_trusted
            FROM {p}sys.check_constraints cc
            LEFT JOIN {p}sys.columns c ON c.object_id = cc.parent_object_id AND c.column_id = cc.parent_column_id
            WHERE cc.parent_object_id = @id
            UNION ALL
            SELECT 2, dc.name COLLATE DATABASE_DEFAULT, N'DEFAULT' COLLATE DATABASE_DEFAULT, dc.definition COLLATE DATABASE_DEFAULT, c.name COLLATE DATABASE_DEFAULT,
                   CAST(0 AS bit), CAST(0 AS bit)
            FROM {p}sys.default_constraints dc
            JOIN {p}sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
            WHERE dc.parent_object_id = @id
            ORDER BY 1, 2;
            """;
        var table = new DetailTable(
            K.Constraints, "Constraints", (K.Name, "Name"), (K.Kind, "Type"), (K.ColumnList, "Columns"), (K.Definition, "Definition"), (K.Status, "Status"));
        foreach (var r in await RowsAsync(sql, name, cancellationToken).ConfigureAwait(false))
        {
            var isKey = Convert.ToInt32(r[0], CultureInfo.InvariantCulture) == 0;
            var status = Bool(r[5]) ? "disabled" : Bool(r[6]) ? "not trusted" : null;

            // Keys: (name, kind, columns, index type); checks and defaults: (name, kind, definition, column).
            table.Add(isKey
                ? [Text(r[1]), Text(r[2]), Text(r[4]), Text(r[3])?.Replace('_', ' '), status]
                : [Text(r[1]), Text(r[2]), Text(r[4]), Text(r[3]), status]);
        }

        return table.Build();
    }

    private async Task<(ObjectDetailSection? Outgoing, ObjectDetailSection? Incoming)> ForeignKeySectionsAsync(
        string p, string name, string? database, CancellationToken cancellationToken)
    {
        var sql = $"""
            DECLARE @id int = OBJECT_ID(@name);
            SELECT fk.name, fk.parent_object_id, ps.name, po.name, rs.name, ro.name,
                   fk.delete_referential_action_desc, fk.update_referential_action_desc, fk.is_disabled, fk.is_not_trusted,
                   pc.name, rc.name, CAST(CASE WHEN fk.parent_object_id = @id THEN 1 ELSE 0 END AS bit), CAST(CASE WHEN fk.referenced_object_id = @id THEN 1 ELSE 0 END AS bit)
            FROM {p}sys.foreign_keys fk
            JOIN {p}sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN {p}sys.objects po ON po.object_id = fk.parent_object_id
            JOIN {p}sys.schemas ps ON ps.schema_id = po.schema_id
            JOIN {p}sys.objects ro ON ro.object_id = fk.referenced_object_id
            JOIN {p}sys.schemas rs ON rs.schema_id = ro.schema_id
            JOIN {p}sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN {p}sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE fk.parent_object_id = @id OR fk.referenced_object_id = @id
            ORDER BY fk.name, fkc.constraint_column_id;
            """;
        var keys = new List<(string Name, string ParentSchema, string Parent, string RefSchema, string Ref, string? OnDelete, string? OnUpdate, string? Status,
            List<string> Columns, List<string> RefColumns, bool Outgoing, bool Incoming)>();
        foreach (var r in await RowsAsync(sql, name, cancellationToken).ConfigureAwait(false))
        {
            var fkName = Text(r[0])!;
            if (keys.Count == 0 || keys[^1].Name != fkName)
            {
                keys.Add((fkName, Text(r[2])!, Text(r[3])!, Text(r[4])!, Text(r[5])!, Rule(r[6]), Rule(r[7]),
                    Bool(r[8]) ? "disabled" : Bool(r[9]) ? "not trusted" : null, [], [], Bool(r[12]), Bool(r[13])));
            }

            keys[^1].Columns.Add(Text(r[10])!);
            keys[^1].RefColumns.Add(Text(r[11])!);
        }

        var outgoing = new DetailTable(
            K.ForeignKeys, "Foreign keys", (K.Name, "Name"), (K.ColumnList, "Columns"), (K.Table, "References"), (K.ReferencedColumns, "Referenced columns"),
            (K.OnDelete, "On delete"), (K.OnUpdate, "On update"), (K.Status, "Status"));
        var incoming = new DetailTable(
            K.References, "References", (K.Name, "Name"), (K.Table, "Table"), (K.ColumnList, "Columns"), (K.ReferencedColumns, "Referenced columns"),
            (K.OnDelete, "On delete"), (K.OnUpdate, "On update"));
        foreach (var key in keys)
        {
            if (key.Outgoing)
            {
                outgoing.Add(
                    [key.Name, string.Join(", ", key.Columns), key.RefSchema + "." + key.Ref, string.Join(", ", key.RefColumns), key.OnDelete, key.OnUpdate, key.Status],
                    new DatabaseObject(new DatabaseObjectName(database, key.RefSchema, key.Ref), DatabaseObjectKinds.Table));
            }

            if (key.Incoming)
            {
                incoming.Add(
                    [key.Name, key.ParentSchema + "." + key.Parent, string.Join(", ", key.Columns), string.Join(", ", key.RefColumns), key.OnDelete, key.OnUpdate],
                    new DatabaseObject(new DatabaseObjectName(database, key.ParentSchema, key.Parent), DatabaseObjectKinds.Table));
            }
        }

        return (outgoing.Build(), incoming.Build());

        static string? Rule(object? value) => Text(value)?.Replace('_', ' ');
    }

    private async Task<ObjectDetailSection?> IndexesSectionAsync(string p, string name, CancellationToken cancellationToken)
    {
        var sql = $"""
            DECLARE @id int = OBJECT_ID(@name);
            SELECT i.index_id, i.name, i.type_desc, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.is_disabled, i.filter_definition,
                   ic.is_included_column, ic.is_descending_key, c.name
            FROM {p}sys.indexes i
            JOIN {p}sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN {p}sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = @id AND i.index_id > 0
            ORDER BY i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id;
            """;
        var table = new DetailTable(
            K.Indexes, "Indexes", (K.Name, "Name"), (K.Kind, "Type"), (K.Unique, "Unique"), (K.ColumnList, "Columns"), (K.Included, "Included"),
            (K.Filter, "Filter"), (K.Status, "Status"));
        var rows = await RowsAsync(sql, name, cancellationToken).ConfigureAwait(false);
        foreach (var index in rows.GroupBy(static r => Convert.ToInt32(r[0], CultureInfo.InvariantCulture)))
        {
            var first = index.First();
            var kind = Text(first[2])?.Replace('_', ' ');
            if (Bool(first[4]))
            {
                kind += " · PK";
            }
            else if (Bool(first[5]))
            {
                kind += " · UQ";
            }

            var keys = index.Where(static r => !Bool(r[8])).Select(static r => Text(r[10]) + (Bool(r[9]) ? " DESC" : string.Empty));
            var included = index.Where(static r => Bool(r[8])).Select(static r => Text(r[10]));
            table.Add([Text(first[1]), kind, DetailTable.Flag(Bool(first[3])), string.Join(", ", keys), string.Join(", ", included), Text(first[7]),
                Bool(first[6]) ? "disabled" : null]);
        }

        return table.Build();
    }

    private async Task<ObjectDetailSection?> TriggersSectionAsync(string p, string name, DatabaseObjectName parent, CancellationToken cancellationToken)
    {
        var sql = $"""
            DECLARE @id int = OBJECT_ID(@name);
            SELECT t.name, t.is_disabled, t.is_instead_of_trigger,
                   STUFF((SELECT N', ' + te.type_desc FROM {p}sys.trigger_events te
                          WHERE te.object_id = t.object_id ORDER BY te.type FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N''),
                   m.definition
            FROM {p}sys.triggers t
            LEFT JOIN {p}sys.sql_modules m ON m.object_id = t.object_id
            WHERE t.parent_id = @id
            ORDER BY t.name;
            """;
        var table = new DetailTable(K.Triggers, "Triggers", (K.Name, "Name"), (K.Kind, "Timing"), (K.Events, "Events"), (K.Status, "Status"));
        foreach (var r in await RowsAsync(sql, name, cancellationToken).ConfigureAwait(false))
        {
            table.Add(
                [Text(r[0]), Bool(r[2]) ? "INSTEAD OF" : "AFTER", Text(r[3]), Bool(r[1]) ? "disabled" : "enabled"],
                new DatabaseObject(parent with { Name = Text(r[0])! }, DatabaseObjectKinds.Trigger),
                Text(r[4]));
        }

        return table.Build();
    }

    private async Task<ObjectDetailSection?> ParametersSectionAsync(string p, string name, CancellationToken cancellationToken)
    {
        var sql = $"""
            DECLARE @id int = OBJECT_ID(@name);
            SELECT pa.parameter_id, pa.name, t.name, bt.name, t.is_user_defined, pa.max_length, pa.precision, pa.scale, pa.is_output,
                   pa.has_default_value, CAST(pa.default_value AS nvarchar(4000)), pa.is_readonly
            FROM {p}sys.parameters pa
            JOIN {p}sys.types t ON t.user_type_id = pa.user_type_id
            LEFT JOIN {p}sys.types bt ON bt.user_type_id = pa.system_type_id
            WHERE pa.object_id = @id
            ORDER BY pa.parameter_id;
            """;
        var table = new DetailTable(
            K.Parameters, "Parameters", (K.Position, "#"), (K.Name, "Name"), (K.DataType, "Data type"), (K.Direction, "Direction"), (K.Default, "Default"));
        foreach (var r in await RowsAsync(sql, name, cancellationToken).ConfigureAwait(false))
        {
            var position = Convert.ToInt32(r[0], CultureInfo.InvariantCulture);
            var dataType = SqlServerTypeFormatter.Format(
                Text(r[2]) ?? string.Empty, Text(r[3]), Convert.ToInt16(r[5], CultureInfo.InvariantCulture), Convert.ToByte(r[6], CultureInfo.InvariantCulture),
                Convert.ToByte(r[7], CultureInfo.InvariantCulture), Bool(r[4]));
            var direction = position == 0 ? "RETURN" : Bool(r[8]) ? "OUT" : Bool(r[11]) ? "IN (READONLY)" : "IN";
            table.Add([position.ToString(CultureInfo.InvariantCulture), Text(r[1]), dataType, direction, Bool(r[9]) ? Text(r[10]) ?? "(default)" : null]);
        }

        return table.Build();
    }

    private async Task AddSequenceAsync(DetailProperties properties, string p, string name, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT TYPE_NAME(s.system_type_id), CAST(s.start_value AS nvarchar(100)), CAST(s.increment AS nvarchar(100)), CAST(s.minimum_value AS nvarchar(100)),
                   CAST(s.maximum_value AS nvarchar(100)), s.is_cycling, s.is_cached, s.cache_size, CAST(s.current_value AS nvarchar(100))
            FROM {p}sys.sequences s WHERE s.object_id = OBJECT_ID(@name);
            """;
        foreach (var r in await RowsAsync(sql, name, cancellationToken).ConfigureAwait(false))
        {
            properties
                .Add(K.DataType, "Data type", Text(r[0]))
                .Add("start", "Start", Text(r[1]))
                .Add("increment", "Increment", Text(r[2]))
                .Add("minimum", "Minimum", Text(r[3]))
                .Add("maximum", "Maximum", Text(r[4]))
                .Add("cycle", "Cycle", Bool(r[5]) ? "yes" : "no")
                .Add("cache", "Cache", Bool(r[6]) ? Text(r[7]) ?? "yes" : "no")
                .Add("current", "Current value", Text(r[8]));
        }
    }

    private async Task<ObjectDetailSection?> DependenciesSectionAsync(string p, string name, string? database, CancellationToken cancellationToken)
    {
        // Who uses the object (by id; one-part names inside modules resolve per caller, so they may be missing) and what it uses.
        var sql = $"""
            DECLARE @id int = OBJECT_ID(@name);
            SELECT * FROM (
                SELECT DISTINCT TOP (500) 0 AS direction, s.name COLLATE DATABASE_DEFAULT AS schema_name, o.name COLLATE DATABASE_DEFAULT AS name,
                       RTRIM(o.type) COLLATE DATABASE_DEFAULT AS type, o.type_desc COLLATE DATABASE_DEFAULT AS type_desc
                FROM {p}sys.sql_expression_dependencies d
                JOIN {p}sys.objects o ON o.object_id = d.referencing_id
                JOIN {p}sys.schemas s ON s.schema_id = o.schema_id
                WHERE d.referenced_id = @id AND d.referencing_id <> @id) used_by
            UNION ALL
            SELECT * FROM (
                SELECT DISTINCT TOP (500) 1 AS direction, COALESCE(s.name, d.referenced_schema_name) COLLATE DATABASE_DEFAULT AS schema_name,
                       d.referenced_entity_name COLLATE DATABASE_DEFAULT AS name, RTRIM(o.type) COLLATE DATABASE_DEFAULT AS type,
                       o.type_desc COLLATE DATABASE_DEFAULT AS type_desc
                FROM {p}sys.sql_expression_dependencies d
                LEFT JOIN {p}sys.objects o ON o.object_id = d.referenced_id
                LEFT JOIN {p}sys.schemas s ON s.schema_id = o.schema_id
                WHERE d.referencing_id = @id AND d.referenced_class = 1 AND d.referenced_server_name IS NULL AND d.referenced_database_name IS NULL) uses
            ORDER BY 1, 2, 3;
            """;
        var table = new DetailTable(K.Dependencies, "Dependencies", (K.Direction, "Relation"), (K.Name, "Object"), (K.Kind, "Type"));
        foreach (var r in await RowsAsync(sql, name, cancellationToken).ConfigureAwait(false))
        {
            var schema = Text(r[1]);
            var objectName = Text(r[2])!;
            var kind = SqlServerMetadataProvider.KindOf(Text(r[3]) ?? string.Empty);
            table.Add(
                [Convert.ToInt32(r[0], CultureInfo.InvariantCulture) == 0 ? "used by" : "uses", schema is null ? objectName : schema + "." + objectName, Text(r[4])?.Replace('_', ' ')],
                kind == DatabaseObjectKinds.None ? null : new DatabaseObject(new DatabaseObjectName(database, schema, objectName), kind));
        }

        return table.Build();
    }

    private async Task<List<object?[]>> RowsAsync(string sql, string name, CancellationToken cancellationToken)
    {
        var rows = new List<object?[]>();
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Constant text and bracket-quoted catalog prefixes only; the name is a parameter.
            command.CommandText = "SET NOCOUNT ON;\n" + sql;
#pragma warning restore CA2100
            command.CommandTimeout = 30;
            command.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 1024).Value = name;
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var row = new object?[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++)
                    {
                        row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }

                    rows.Add(row);
                }
            }
        }

        return rows;
    }

    private static string? Text(object? value) => value switch
    {
        null => null,
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static bool Bool(object? value) => value is not null && Convert.ToBoolean(value, CultureInfo.InvariantCulture);

    private static string? Date(object? value) => value is DateTime date ? date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : null;

    internal static string SizeText(long kilobytes) => kilobytes switch
    {
        < 1024 => kilobytes.ToString("N0", CultureInfo.InvariantCulture) + " KB",
        < 1024 * 1024 => (kilobytes / 1024d).ToString("N1", CultureInfo.InvariantCulture) + " MB",
        _ => (kilobytes / 1024d / 1024d).ToString("N2", CultureInfo.InvariantCulture) + " GB",
    };
}
