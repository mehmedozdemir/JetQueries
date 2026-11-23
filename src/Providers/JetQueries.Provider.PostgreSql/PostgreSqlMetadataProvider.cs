using System.Runtime.CompilerServices;
using System.Text;
using JetQueries.Sdk;
using Npgsql;
using NpgsqlTypes;

namespace JetQueries.Provider.PostgreSql;

/// <summary>
/// Reads the PostgreSQL catalog (<c>pg_catalog</c>) one level at a time: schemas, then the objects of a schema.
/// Every value is a parameter; names in generated DDL are quoted by <see cref="PostgreSqlNames"/>.
/// </summary>
/// <remarks>
/// Naming convention of the objects returned: a schema is <c>(null, null, schema)</c>, everything else is
/// <c>(null, schema, name)</c>, with names as the catalog stores them. A null schema in a request means the
/// session's search path, resolved by the server.
/// </remarks>
internal sealed partial class PostgreSqlMetadataProvider(Func<NpgsqlConnection> currentConnection) : IMetadataProvider
{
    // The oid of the table or view a (schema, name) pair means; without a schema, the search path decides.
    private const string Relation = """
        (CASE WHEN @schema IS NULL THEN to_regclass(quote_ident(@name))::oid
              ELSE (SELECT c.oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @schema AND c.relname = @name)
         END)
        """;

    // Objects that belong to an extension (pgcrypto's functions in public, for example) are its business.
    private static string NotFromExtension(string oid) => $"NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = {oid} AND d.deptype = 'e')";

    public async IAsyncEnumerable<DatabaseObject> GetObjectsAsync(
        DatabaseObject? parent,
        DatabaseObjectKinds kinds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (parent is null && kinds.HasFlag(DatabaseObjectKinds.Schema))
        {
            // The system schemas are left out; the current one is always shown.
            const string Sql = """
                SELECT n.nspname FROM pg_namespace n
                WHERE (n.nspname NOT LIKE 'pg!_%' ESCAPE '!' AND n.nspname <> 'information_schema' AND has_schema_privilege(n.oid, 'USAGE'))
                   OR n.nspname = current_schema()
                ORDER BY n.nspname
                """;
            await foreach (var name in QueryAsync(Sql, [], static r => r.GetString(0), cancellationToken).ConfigureAwait(false))
            {
                yield return new DatabaseObject(new DatabaseObjectName(null, null, name), DatabaseObjectKinds.Schema);
            }
        }

        if (parent is not null && parent.Kind != DatabaseObjectKinds.Schema)
        {
            yield break;
        }

        // Objects of a schema; at the root, objects of the current schema (the first of the search path).
        var schema = parent?.Name.Name;
        var relationKinds = new List<string>();
        if (kinds.HasFlag(DatabaseObjectKinds.Table))
        {
            relationKinds.AddRange(["r", "p", "f"]);
        }

        if (kinds.HasFlag(DatabaseObjectKinds.View))
        {
            relationKinds.AddRange(["v", "m"]);
        }

        if (kinds.HasFlag(DatabaseObjectKinds.Sequence))
        {
            relationKinds.Add("S");
        }

        if (relationKinds.Count > 0)
        {
            // Partitions are listed under nothing: a partitioned table can have thousands of them.
            const string Sql = """
                SELECT n.nspname, c.relname, c.relkind::text
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = COALESCE(@schema, current_schema()) AND c.relkind::text = ANY(@kinds) AND NOT c.relispartition
                ORDER BY c.relname
                """;
            await foreach (var row in QueryAsync(
                               Sql,
                               [Text("schema", schema), TextArray("kinds", relationKinds)],
                               static r => (Schema: r.GetString(0), Name: r.GetString(1), Kind: r.GetString(2)),
                               cancellationToken).ConfigureAwait(false))
            {
                var kind = row.Kind switch
                {
                    "v" or "m" => DatabaseObjectKinds.View,
                    "S" => DatabaseObjectKinds.Sequence,
                    _ => DatabaseObjectKinds.Table,
                };
                yield return new DatabaseObject(new DatabaseObjectName(null, row.Schema, row.Name), kind);
            }
        }

        var routineKinds = new List<string>();
        if (kinds.HasFlag(DatabaseObjectKinds.Function))
        {
            routineKinds.Add("f");
        }

        if (kinds.HasFlag(DatabaseObjectKinds.Procedure))
        {
            routineKinds.Add("p");
        }

        if (routineKinds.Count > 0)
        {
            // Overloads share a name and appear once.
            var sql = $"""
                SELECT DISTINCT n.nspname, p.proname, p.prokind::text
                FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = COALESCE(@schema, current_schema()) AND p.prokind::text = ANY(@kinds) AND {NotFromExtension("p.oid")}
                ORDER BY p.proname
                """;
            await foreach (var row in QueryAsync(
                               sql,
                               [Text("schema", schema), TextArray("kinds", routineKinds)],
                               static r => (Schema: r.GetString(0), Name: r.GetString(1), Kind: r.GetString(2)),
                               cancellationToken).ConfigureAwait(false))
            {
                yield return new DatabaseObject(
                    new DatabaseObjectName(null, row.Schema, row.Name),
                    row.Kind == "p" ? DatabaseObjectKinds.Procedure : DatabaseObjectKinds.Function);
            }
        }

        if (kinds.HasFlag(DatabaseObjectKinds.Type))
        {
            // Enums, domains, ranges and stand-alone composite types; not the row type every table has.
            var sql = $"""
                SELECT n.nspname, t.typname
                FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
                WHERE n.nspname = COALESCE(@schema, current_schema())
                  AND (t.typtype IN ('e', 'd', 'r', 'm') OR (t.typtype = 'c' AND EXISTS (SELECT 1 FROM pg_class c WHERE c.oid = t.typrelid AND c.relkind = 'c')))
                  AND {NotFromExtension("t.oid")}
                ORDER BY t.typname
                """;
            await foreach (var row in QueryAsync(
                               sql,
                               [Text("schema", schema)],
                               static r => (Schema: r.GetString(0), Name: r.GetString(1)),
                               cancellationToken).ConfigureAwait(false))
            {
                yield return new DatabaseObject(new DatabaseObjectName(null, row.Schema, row.Name), DatabaseObjectKinds.Type);
            }
        }
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DatabaseObjectName tableOrView, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tableOrView);
        const string Sql = $"""
            SELECT a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull, a.attnum::int,
                   EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid = a.attrelid AND i.indisprimary AND a.attnum = ANY(i.indkey))
            FROM pg_attribute a
            WHERE a.attrelid = {Relation} AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """;

        var columns = new List<ColumnInfo>();
        await foreach (var column in QueryAsync(
                           Sql,
                           [Text("schema", tableOrView.Schema), Text("name", tableOrView.Name)],
                           static r => new ColumnInfo(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetInt32(3), r.GetBoolean(4)),
                           cancellationToken).ConfigureAwait(false))
        {
            columns.Add(column);
        }

        return columns;
    }

    public async Task<string?> GetDefinitionAsync(DatabaseObject target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Kind switch
        {
            DatabaseObjectKinds.View => await ViewAsync(target.Name, cancellationToken).ConfigureAwait(false),
            DatabaseObjectKinds.Table => await TableAsync(target.Name, cancellationToken).ConfigureAwait(false),
            DatabaseObjectKinds.Sequence => await SequenceAsync(target.Name, cancellationToken).ConfigureAwait(false),
            DatabaseObjectKinds.Function or DatabaseObjectKinds.Procedure => await RoutineAsync(target, cancellationToken).ConfigureAwait(false),
            DatabaseObjectKinds.Type => await TypeAsync(target.Name, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    private async Task<string?> ViewAsync(DatabaseObjectName name, CancellationToken cancellationToken)
    {
        const string Sql = $"""
            SELECT n.nspname, c.relname, c.relkind::text, pg_get_viewdef(c.oid, true)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.oid = {Relation} AND c.relkind IN ('v', 'm')
            """;
        await foreach (var row in QueryAsync(
                           Sql,
                           [Text("schema", name.Schema), Text("name", name.Name)],
                           static r => (Schema: r.GetString(0), Name: r.GetString(1), Kind: r.GetString(2), Text: r.GetString(3)),
                           cancellationToken).ConfigureAwait(false))
        {
            var head = row.Kind == "m" ? "CREATE MATERIALIZED VIEW" : "CREATE OR REPLACE VIEW";
            return $"{head} {PostgreSqlNames.Quote(row.Schema, row.Name)} AS\n{row.Text.Trim().TrimEnd(';')};\n";
        }

        return null;
    }

    /// <summary>A CREATE TABLE written from the catalog: columns, constraints, then the other indexes.</summary>
    private async Task<string?> TableAsync(DatabaseObjectName name, CancellationToken cancellationToken)
    {
        const string ColumnsSql = $"""
            SELECT n.nspname, c.relname, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull,
                   pg_get_expr(d.adbin, d.adrelid), a.attidentity::text, a.attgenerated::text
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            LEFT JOIN pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
            WHERE c.oid = {Relation}
            ORDER BY a.attnum
            """;
        string? schema = null;
        string? table = null;
        var lines = new List<string>();
        await foreach (var row in QueryAsync(
                           ColumnsSql,
                           [Text("schema", name.Schema), Text("name", name.Name)],
                           static r => (Schema: r.GetString(0), Table: r.GetString(1), Column: r.GetString(2), Type: r.GetString(3), NotNull: r.GetBoolean(4),
                               Default: r.IsDBNull(5) ? null : r.GetString(5), Identity: r.GetString(6), Generated: r.GetString(7)),
                           cancellationToken).ConfigureAwait(false))
        {
            schema = row.Schema;
            table = row.Table;
            var line = new StringBuilder("    ").Append(PostgreSqlNames.Quote(row.Column)).Append(' ').Append(row.Type);
            if (row.Identity is "a" or "d")
            {
                line.Append(row.Identity == "a" ? " GENERATED ALWAYS AS IDENTITY" : " GENERATED BY DEFAULT AS IDENTITY");
            }
            else if (row.Generated == "s" && row.Default is not null)
            {
                line.Append(" GENERATED ALWAYS AS (").Append(row.Default).Append(") STORED");
            }
            else if (row.Default is not null)
            {
                line.Append(" DEFAULT ").Append(row.Default);
            }

            if (row.NotNull)
            {
                line.Append(" NOT NULL");
            }

            lines.Add(line.ToString());
        }

        if (schema is null || table is null)
        {
            return null;
        }

        const string ConstraintsSql = $"""
            SELECT k.conname, pg_get_constraintdef(k.oid, true)
            FROM pg_constraint k
            WHERE k.conrelid = {Relation}
            ORDER BY CASE k.contype WHEN 'p' THEN 0 WHEN 'u' THEN 1 WHEN 'c' THEN 2 WHEN 'f' THEN 3 ELSE 4 END, k.conname
            """;
        await foreach (var row in QueryAsync(
                           ConstraintsSql,
                           [Text("schema", schema), Text("name", table)],
                           static r => (Name: r.GetString(0), Definition: r.GetString(1)),
                           cancellationToken).ConfigureAwait(false))
        {
            lines.Add($"    CONSTRAINT {PostgreSqlNames.Quote(row.Name)} {row.Definition}");
        }

        var script = new StringBuilder("CREATE TABLE ").Append(PostgreSqlNames.Quote(schema, table)).Append(" (\n")
            .AppendJoin(",\n", lines).Append("\n);\n");

        // Indexes that back a constraint are already written as that constraint.
        const string IndexesSql = $"""
            SELECT pg_get_indexdef(i.indexrelid)
            FROM pg_index i
            WHERE i.indrelid = {Relation}
              AND NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = i.indexrelid AND k.contype IN ('p', 'u', 'x'))
            ORDER BY i.indexrelid
            """;
        await foreach (var index in QueryAsync(IndexesSql, [Text("schema", schema), Text("name", table)], static r => r.GetString(0), cancellationToken)
                           .ConfigureAwait(false))
        {
            script.Append('\n').Append(index).Append(";\n");
        }

        return script.ToString();
    }

    private async Task<string?> SequenceAsync(DatabaseObjectName name, CancellationToken cancellationToken)
    {
        const string Sql = $"""
            SELECT n.nspname, c.relname, format_type(s.seqtypid, NULL), s.seqstart, s.seqincrement, s.seqmin, s.seqmax, s.seqcache, s.seqcycle
            FROM pg_sequence s JOIN pg_class c ON c.oid = s.seqrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE s.seqrelid = {Relation}
            """;
        await foreach (var row in QueryAsync(
                           Sql,
                           [Text("schema", name.Schema), Text("name", name.Name)],
                           static r => (Schema: r.GetString(0), Name: r.GetString(1), Type: r.GetString(2), Start: r.GetInt64(3), Increment: r.GetInt64(4),
                               Min: r.GetInt64(5), Max: r.GetInt64(6), Cache: r.GetInt64(7), Cycle: r.GetBoolean(8)),
                           cancellationToken).ConfigureAwait(false))
        {
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"CREATE SEQUENCE {PostgreSqlNames.Quote(row.Schema, row.Name)} AS {row.Type}\n    START WITH {row.Start} INCREMENT BY {row.Increment}\n    MINVALUE {row.Min} MAXVALUE {row.Max}\n    CACHE {row.Cache}{(row.Cycle ? " CYCLE" : " NO CYCLE")};\n");
        }

        return null;
    }

    /// <summary>Every overload, from the first schema of the search path that has the name (or the given schema).</summary>
    private async Task<string?> RoutineAsync(DatabaseObject target, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT n.nspname, pg_get_functiondef(p.oid)
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE p.proname = @name AND p.prokind::text = @kind
              AND (n.nspname = @schema OR (@schema IS NULL AND n.nspname = ANY(current_schemas(false))))
            ORDER BY array_position(current_schemas(false), n.nspname::text), p.oid
            """;
        string? first = null;
        var script = new StringBuilder();
        await foreach (var row in QueryAsync(
                           Sql,
                           [Text("schema", target.Name.Schema), Text("name", target.Name.Name), Text("kind", target.Kind == DatabaseObjectKinds.Procedure ? "p" : "f")],
                           static r => (Schema: r.GetString(0), Text: r.GetString(1)),
                           cancellationToken).ConfigureAwait(false))
        {
            first ??= row.Schema;
            if (row.Schema != first)
            {
                break;
            }

            script.Append(script.Length == 0 ? string.Empty : "\n").Append(row.Text.TrimEnd()).Append(";\n");
        }

        return script.Length == 0 ? null : script.ToString();
    }

    private async Task<string?> TypeAsync(DatabaseObjectName name, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT n.nspname, t.typname, t.typtype::text, t.oid, format_type(t.typbasetype, t.typtypmod), t.typrelid
            FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE t.typname = @name AND (n.nspname = @schema OR (@schema IS NULL AND n.nspname = ANY(current_schemas(false))))
            ORDER BY array_position(current_schemas(false), n.nspname::text)
            LIMIT 1
            """;
        (string Schema, string Name, string Kind, uint Oid, string BaseType, uint Relation)? type = null;
        await foreach (var row in QueryAsync(
                           Sql,
                           [Text("schema", name.Schema), Text("name", name.Name)],
                           static r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetFieldValue<uint>(3), r.GetString(4), r.GetFieldValue<uint>(5)),
                           cancellationToken).ConfigureAwait(false))
        {
            type = row;
        }

        if (type is not { } found)
        {
            return null;
        }

        var qualified = PostgreSqlNames.Quote(found.Schema, found.Name);
        switch (found.Kind)
        {
            case "e":
                {
                    var labels = new List<string>();
                    await foreach (var label in QueryAsync(
                                       "SELECT e.enumlabel FROM pg_enum e WHERE e.enumtypid = @oid ORDER BY e.enumsortorder",
                                       [Oid("oid", found.Oid)],
                                       static r => r.GetString(0),
                                       cancellationToken).ConfigureAwait(false))
                    {
                        labels.Add(PostgreSqlNames.Literal(label));
                    }

                    return $"CREATE TYPE {qualified} AS ENUM ({string.Join(", ", labels)});\n";
                }

            case "d":
                {
                    var script = new StringBuilder($"CREATE DOMAIN {qualified} AS {found.BaseType}");
                    await foreach (var check in QueryAsync(
                                       "SELECT k.conname, pg_get_constraintdef(k.oid, true) FROM pg_constraint k WHERE k.contypid = @oid ORDER BY k.conname",
                                       [Oid("oid", found.Oid)],
                                       static r => (Name: r.GetString(0), Definition: r.GetString(1)),
                                       cancellationToken).ConfigureAwait(false))
                    {
                        script.Append("\n    CONSTRAINT ").Append(PostgreSqlNames.Quote(check.Name)).Append(' ').Append(check.Definition);
                    }

                    return script.Append(";\n").ToString();
                }

            case "c":
                {
                    var fields = new List<string>();
                    await foreach (var field in QueryAsync(
                                       "SELECT a.attname, format_type(a.atttypid, a.atttypmod) FROM pg_attribute a WHERE a.attrelid = @oid AND a.attnum > 0 AND NOT a.attisdropped ORDER BY a.attnum",
                                       [Oid("oid", found.Relation)],
                                       static r => (Name: r.GetString(0), Type: r.GetString(1)),
                                       cancellationToken).ConfigureAwait(false))
                    {
                        fields.Add($"    {PostgreSqlNames.Quote(field.Name)} {field.Type}");
                    }

                    return $"CREATE TYPE {qualified} AS (\n{string.Join(",\n", fields)}\n);\n";
                }

            default:
                return null;
        }
    }

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter TextArray(string name, IReadOnlyList<string> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() };

    private static NpgsqlParameter Oid(string name, uint value) => new(name, NpgsqlDbType.Oid) { Value = value };

    private async IAsyncEnumerable<T> QueryAsync<T>(
        string sql,
        NpgsqlParameter[] parameters,
        Func<NpgsqlDataReader, T> read,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var command = currentConnection().CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Constant text with fixed literals; every value is a parameter.
            command.CommandText = sql;
#pragma warning restore CA2100
            command.CommandTimeout = 30;
            command.Parameters.AddRange(parameters);

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    yield return read(reader);
                }
            }
        }
    }
}
