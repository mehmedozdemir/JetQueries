using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using JetQueries.Provider.Oracle.Language;
using JetQueries.Sdk;
using Oracle.ManagedDataAccess.Client;

namespace JetQueries.Provider.Oracle;

/// <summary>
/// Reads the Oracle data dictionary one level at a time: schemas, then the objects of a schema. Only ALL_*
/// views are used, which every user may query and which show exactly what that user may see. Every value is a
/// bind variable; no name is ever put into SQL text.
/// </summary>
/// <remarks>
/// Naming convention of the objects returned: a schema is <c>(null, null, SCHEMA)</c>, everything else is
/// <c>(null, SCHEMA, NAME)</c>, with names as the dictionary stores them (upper case unless created quoted).
/// </remarks>
internal sealed partial class OracleMetadataProvider(OracleConnection connection) : IMetadataProvider
{
    // ALL_OBJECTS.OBJECT_TYPE per kind. Literals chosen here, never user input.
    private static readonly (DatabaseObjectKinds Kind, string Type)[] ObjectTypes =
    [
        (DatabaseObjectKinds.Table, "TABLE"),
        (DatabaseObjectKinds.View, "VIEW"),
        (DatabaseObjectKinds.Procedure, "PROCEDURE"),
        (DatabaseObjectKinds.Function, "FUNCTION"),
        (DatabaseObjectKinds.Package, "PACKAGE"),
        (DatabaseObjectKinds.PackageBody, "PACKAGE BODY"),
        (DatabaseObjectKinds.Sequence, "SEQUENCE"),
        (DatabaseObjectKinds.Synonym, "SYNONYM"),
        (DatabaseObjectKinds.Trigger, "TRIGGER"),
        (DatabaseObjectKinds.Type, "TYPE"),
    ];

    public async IAsyncEnumerable<DatabaseObject> GetObjectsAsync(
        DatabaseObject? parent,
        DatabaseObjectKinds kinds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (parent is null)
        {
            if (kinds.HasFlag(DatabaseObjectKinds.Schema))
            {
                // Oracle's own schemas (SYS, SYSTEM, XDB, …) are left out; the connected user is always shown.
                const string Sql = "SELECT u.username FROM all_users u WHERE u.oracle_maintained = 'N' OR u.username = USER ORDER BY u.username";
                await foreach (var row in QueryAsync(Sql, [], static r => r.GetString(0), cancellationToken).ConfigureAwait(false))
                {
                    yield return new DatabaseObject(new DatabaseObjectName(null, null, row), DatabaseObjectKinds.Schema);
                }
            }
        }

        // Objects of a schema; at the root, objects of the session's current schema (what an unqualified name means).
        if (parent is null || parent.Kind == DatabaseObjectKinds.Schema)
        {
            var types = ObjectTypes.Where(t => kinds.HasFlag(t.Kind)).Select(static t => t.Type).ToArray();
            if (types.Length == 0)
            {
                yield break;
            }

            // Generated objects (system-named LOB and nested-table segments) and the recycle bin are noise.
            var sql = $"""
                SELECT o.owner, o.object_name, o.object_type
                FROM all_objects o
                WHERE o.owner = NVL(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'))
                  AND o.object_type IN ({string.Join(", ", types.Select(static t => $"'{t}'"))})
                  AND o.generated = 'N' AND o.secondary = 'N' AND o.object_name NOT LIKE 'BIN$%'
                ORDER BY o.object_name, o.object_type
                """;
            await foreach (var row in QueryAsync(
                               sql,
                               [("owner", parent?.Name.Name)],
                               static r => (Owner: r.GetString(0), Name: r.GetString(1), Type: r.GetString(2)),
                               cancellationToken).ConfigureAwait(false))
            {
                yield return new DatabaseObject(new DatabaseObjectName(null, row.Owner, row.Name), ObjectTypes.First(t => t.Type == row.Type).Kind);
            }

            yield break;
        }

        // The routines a package declares.
        if (parent.Kind == DatabaseObjectKinds.Package && (kinds & (DatabaseObjectKinds.Procedure | DatabaseObjectKinds.Function)) != 0)
        {
            // A function is a subprogram with a return value, which ALL_ARGUMENTS lists at position 0.
            const string Sql = """
                SELECT p.procedure_name,
                       (SELECT COUNT(*) FROM all_arguments a
                        WHERE a.owner = p.owner AND a.package_name = p.object_name AND a.object_name = p.procedure_name AND a.position = 0) AS returns
                FROM all_procedures p
                WHERE p.owner = NVL(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')) AND p.object_name = :name AND p.procedure_name IS NOT NULL
                ORDER BY p.subprogram_id
                """;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var row in QueryAsync(
                               Sql,
                               [("owner", parent.Name.Schema), ("name", parent.Name.Name)],
                               static r => (Name: r.GetString(0), Returns: (ToInt(r, 1) ?? 0) > 0),
                               cancellationToken).ConfigureAwait(false))
            {
                var kind = row.Returns ? DatabaseObjectKinds.Function : DatabaseObjectKinds.Procedure;

                // Overloads appear once.
                if (kinds.HasFlag(kind) && seen.Add(row.Name))
                {
                    yield return new DatabaseObject(new DatabaseObjectName(null, parent.Name.Schema, row.Name), kind);
                }
            }

            yield break;
        }

        if (parent.Kind is DatabaseObjectKinds.Table or DatabaseObjectKinds.View && kinds.HasFlag(DatabaseObjectKinds.Trigger))
        {
            const string Sql = "SELECT t.owner, t.trigger_name FROM all_triggers t WHERE t.table_owner = :owner AND t.table_name = :name ORDER BY t.trigger_name";
            await foreach (var row in QueryAsync(
                               Sql,
                               [("owner", parent.Name.Schema), ("name", parent.Name.Name)],
                               static r => (Owner: r.GetString(0), Name: r.GetString(1)),
                               cancellationToken).ConfigureAwait(false))
            {
                yield return new DatabaseObject(new DatabaseObjectName(null, row.Owner, row.Name), DatabaseObjectKinds.Trigger);
            }
        }
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DatabaseObjectName tableOrView, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tableOrView);
        const string Sql = """
            SELECT c.column_name, c.data_type, c.data_length, c.data_precision, c.data_scale, c.nullable, c.column_id,
                   c.char_length, c.char_used,
                   (SELECT COUNT(*)
                    FROM all_constraints k
                    JOIN all_cons_columns kc ON kc.owner = k.owner AND kc.constraint_name = k.constraint_name
                    WHERE k.constraint_type = 'P' AND k.owner = c.owner AND k.table_name = c.table_name AND kc.column_name = c.column_name) AS is_key
            FROM all_tab_columns c
            WHERE c.owner = NVL(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')) AND c.table_name = :name
            ORDER BY c.column_id
            """;

        var columns = new List<ColumnInfo>();
        await foreach (var column in QueryAsync(
                           Sql,
                           [("owner", tableOrView.Schema), ("name", tableOrView.Name)],
                           static r => new ColumnInfo(
                               r.GetString(0),
                               OracleTypeFormatter.Format(
                                   r.GetString(1),
                                   ToInt(r, 2) ?? 0,
                                   ToInt(r, 3),
                                   ToInt(r, 4),
                                   ToInt(r, 7) ?? 0,
                                   r.IsDBNull(8) ? null : r.GetString(8)),
                               r.GetString(5) == "Y",
                               ToInt(r, 6) ?? 0,
                               (ToInt(r, 9) ?? 0) > 0),
                           cancellationToken).ConfigureAwait(false))
        {
            columns.Add(column);
        }

        return columns;
    }

    public async Task<string?> GetDefinitionAsync(DatabaseObject target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var owner = target.Name.Schema;
        var name = target.Name.Name;
        switch (target.Kind)
        {
            case DatabaseObjectKinds.View:
                {
                    // ALL_VIEWS.TEXT is a LONG: the command is told to fetch all of it.
                    const string Sql = "SELECT v.owner, v.text FROM all_views v WHERE v.owner = NVL(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')) AND v.view_name = :name";
                    await foreach (var row in QueryAsync(Sql, [("owner", owner), ("name", name)], static r => (Owner: r.GetString(0), Text: r.GetString(1)), cancellationToken)
                                       .ConfigureAwait(false))
                    {
                        return $"CREATE OR REPLACE VIEW {OracleNames.Quote(row.Owner)}.{OracleNames.Quote(name)} AS\n{row.Text.TrimEnd()};\n";
                    }

                    return null;
                }

            case DatabaseObjectKinds.Procedure or DatabaseObjectKinds.Function or DatabaseObjectKinds.Package or DatabaseObjectKinds.PackageBody
                or DatabaseObjectKinds.Trigger or DatabaseObjectKinds.Type:
                {
                    // ALL_SOURCE holds the text from the object's kind onwards ("PROCEDURE p IS …"), one row per line.
                    const string Sql = """
                        SELECT s.text FROM all_source s
                        WHERE s.owner = NVL(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')) AND s.name = :name AND s.type = :type
                        ORDER BY s.line
                        """;
                    var type = ObjectTypes.First(t => t.Kind == target.Kind).Type;
                    var source = new StringBuilder();
                    await foreach (var line in QueryAsync(Sql, [("owner", owner), ("name", name), ("type", type)], static r => r.IsDBNull(0) ? string.Empty : r.GetString(0), cancellationToken)
                                       .ConfigureAwait(false))
                    {
                        source.Append(line);
                    }

                    return source.Length == 0 ? null : "CREATE OR REPLACE " + source.ToString().TrimEnd() + "\n/\n";
                }

            case DatabaseObjectKinds.Table or DatabaseObjectKinds.Sequence or DatabaseObjectKinds.Synonym:
                {
                    // Oracle writes the real DDL itself. Without the privilege to see another schema's object
                    // it raises an error; that is "no definition available", not a failure.
                    const string Sql = "SELECT DBMS_METADATA.GET_DDL(:type, :name, NVL(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'))) FROM dual";
                    var type = ObjectTypes.First(t => t.Kind == target.Kind).Type;
                    try
                    {
                        await foreach (var ddl in QueryAsync(Sql, [("type", type), ("name", name), ("owner", owner)], static r => r.IsDBNull(0) ? null : r.GetString(0), cancellationToken)
                                           .ConfigureAwait(false))
                        {
                            return ddl is null ? null : ddl.Trim() + ";\n";
                        }
                    }
                    catch (OracleException) when (!cancellationToken.IsCancellationRequested)
                    {
                        return null;
                    }

                    return null;
                }

            default:
                return null;
        }
    }

    private static int? ToInt(OracleDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private async IAsyncEnumerable<T> QueryAsync<T>(
        string sql,
        (string Name, string? Value)[] parameters,
        Func<OracleDataReader, T> read,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Constant text with fixed literals; every value is a bind variable.
            command.CommandText = sql;
#pragma warning restore CA2100
            command.BindByName = true;
            command.CommandTimeout = 30;
            command.InitialLONGFetchSize = -1;
            command.InitialLOBFetchSize = -1;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.Add(name, OracleDbType.Varchar2, (object?)value ?? DBNull.Value, ParameterDirection.Input);
            }

            var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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

/// <summary>Formats dictionary type information the way Oracle tools show it: VARCHAR2(50 CHAR), NUMBER(12,2), RAW(16).</summary>
internal static class OracleTypeFormatter
{
    /// <param name="dataType">ALL_TAB_COLUMNS.DATA_TYPE; some types already carry their size, such as TIMESTAMP(6).</param>
    /// <param name="dataLength">Length in bytes.</param>
    /// <param name="precision">Numeric precision, if declared.</param>
    /// <param name="scale">Numeric scale, if declared.</param>
    /// <param name="charLength">Length in characters for character types.</param>
    /// <param name="charUsed">"C" when the length was declared in characters, "B" in bytes.</param>
    public static string Format(string dataType, int dataLength, int? precision, int? scale, int charLength, string? charUsed) => dataType switch
    {
        "VARCHAR2" or "CHAR" => string.Create(CultureInfo.InvariantCulture, $"{dataType}({charLength}{(charUsed == "C" ? " CHAR" : string.Empty)})"),
        "NVARCHAR2" or "NCHAR" => string.Create(CultureInfo.InvariantCulture, $"{dataType}({charLength})"),
        "RAW" => string.Create(CultureInfo.InvariantCulture, $"RAW({dataLength})"),
        "NUMBER" when precision is null => scale is > 0 ? string.Create(CultureInfo.InvariantCulture, $"NUMBER(*,{scale})") : "NUMBER",
        "NUMBER" => scale is null or 0
            ? string.Create(CultureInfo.InvariantCulture, $"NUMBER({precision})")
            : string.Create(CultureInfo.InvariantCulture, $"NUMBER({precision},{scale})"),
        "FLOAT" when precision is not null and not 126 => string.Create(CultureInfo.InvariantCulture, $"FLOAT({precision})"),
        _ => dataType,
    };
}

/// <summary>
/// Cuts a script into the pieces Oracle accepts one at a time, the way SQL*Plus does: a SQL statement ends at
/// its ';' (which is not sent), a PL/SQL unit at the '/' line (its final ';' is part of it and is sent).
/// SQL*Plus commands are not SQL and are dropped, except EXEC, which is a PL/SQL call in disguise.
/// </summary>
internal static class OracleScriptSplitter
{
    public static IReadOnlyList<ScriptBatch> Split(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var batches = new List<ScriptBatch>();
        foreach (var node in PlStructureParser.Parse(script).Root.Children ?? [])
        {
            var text = script[node.Start..Math.Min(node.End, script.Length)];
            switch (node.Kind)
            {
                case PlNodeKind.SqlPlusCommand:
                    {
                        var space = text.IndexOfAny([' ', '\t']);
                        var word = space < 0 ? text : text[..space];
                        if (space > 0 && (word.Equals("EXEC", StringComparison.OrdinalIgnoreCase) || word.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase)))
                        {
                            var call = text[space..].Trim().TrimEnd(';').TrimEnd();
                            if (call.Length > 0)
                            {
                                batches.Add(new ScriptBatch($"BEGIN {call}; END;", LineOf(script, node.Start)));
                            }
                        }

                        break;
                    }

                case PlNodeKind.SqlStatement:
                    {
                        var statement = text.TrimEnd().TrimEnd(';').TrimEnd();
                        if (statement.Length > 0)
                        {
                            batches.Add(new ScriptBatch(statement, LineOf(script, node.Start)));
                        }

                        break;
                    }

                default:
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        batches.Add(new ScriptBatch(text, LineOf(script, node.Start)));
                    }

                    break;
            }
        }

        return batches;
    }

    private static int LineOf(string script, int offset) => script.AsSpan(0, offset).Count('\n');
}
