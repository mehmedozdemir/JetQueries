using System.Globalization;
using System.Runtime.CompilerServices;
using JetQueries.Sdk;
using Microsoft.Data.SqlClient;

namespace JetQueries.Provider.SqlServer;

/// <summary>
/// Reads the SQL Server catalog one level at a time: databases, schemas, schema objects, triggers of a table.
/// Only <c>sys.*</c> catalog views that need no special permission are used, never DMVs, and user tables are
/// never scanned. Values are parameters; the database name, which cannot be a parameter in a three-part
/// catalog reference, is always bracket-quoted by <see cref="SqlServerNames"/>.
/// </summary>
/// <remarks>
/// Naming convention of the objects returned: a database is <c>(null, null, database)</c>, a schema is
/// <c>(database, null, schema)</c>, everything else is <c>(database, schema, name)</c>.
/// </remarks>
internal sealed partial class SqlServerMetadataProvider(SqlConnection connection) : IMetadataProvider, IObjectDetailsProvider
{
    // sys.objects.type codes per kind. Literals chosen here, never user input.
    private static readonly (DatabaseObjectKinds Kind, string[] Types)[] ObjectTypes =
    [
        (DatabaseObjectKinds.Table, ["U"]),
        (DatabaseObjectKinds.View, ["V"]),
        (DatabaseObjectKinds.Procedure, ["P", "PC", "X"]),
        (DatabaseObjectKinds.Function, ["FN", "IF", "TF", "FS", "FT", "AF"]),
        (DatabaseObjectKinds.Sequence, ["SO"]),
        (DatabaseObjectKinds.Synonym, ["SN"]),
    ];

    public async IAsyncEnumerable<DatabaseObject> GetObjectsAsync(
        DatabaseObject? parent,
        DatabaseObjectKinds kinds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (parent is null)
        {
            if (kinds.HasFlag(DatabaseObjectKinds.Database))
            {
                const string Sql = "SELECT d.name FROM sys.databases d WHERE d.state = 0 AND HAS_DBACCESS(d.name) = 1 ORDER BY d.name;";
                await foreach (var row in QueryAsync(Sql, null, null, static r => r.GetString(0), cancellationToken).ConfigureAwait(false))
                {
                    yield return new DatabaseObject(new DatabaseObjectName(null, null, row), DatabaseObjectKinds.Database);
                }
            }

            yield break;
        }

        switch (parent.Kind)
        {
            case DatabaseObjectKinds.Database when kinds.HasFlag(DatabaseObjectKinds.Schema):
                {
                    var database = parent.Name.Name;

                    // Fixed database roles own schemas of the same name (ids 16384 and up); they hold no user objects.
                    var sql = $"""
                        SELECT s.name
                        FROM {SqlServerNames.CatalogPrefix(database)}sys.schemas s
                        WHERE s.schema_id < 16384 AND s.name NOT IN (N'sys', N'INFORMATION_SCHEMA', N'guest')
                        ORDER BY s.name;
                        """;
                    await foreach (var row in QueryAsync(sql, null, null, static r => r.GetString(0), cancellationToken).ConfigureAwait(false))
                    {
                        yield return new DatabaseObject(new DatabaseObjectName(database, null, row), DatabaseObjectKinds.Schema);
                    }

                    break;
                }

            case DatabaseObjectKinds.Schema:
                {
                    var database = parent.Name.Database;
                    var schema = parent.Name.Name;
                    var sql = BuildSchemaObjectsQuery(database, kinds);
                    if (sql is null)
                    {
                        break;
                    }

                    await foreach (var row in QueryAsync(sql, "@schema", schema, static r => (Name: r.GetString(0), Type: r.GetString(1).Trim()), cancellationToken)
                                       .ConfigureAwait(false))
                    {
                        yield return new DatabaseObject(new DatabaseObjectName(database, schema, row.Name), KindOf(row.Type));
                    }

                    break;
                }

            case DatabaseObjectKinds.Table or DatabaseObjectKinds.View when kinds.HasFlag(DatabaseObjectKinds.Trigger):
                {
                    var sql = $"""
                        SELECT t.name
                        FROM {SqlServerNames.CatalogPrefix(parent.Name.Database)}sys.triggers t
                        WHERE t.parent_id = OBJECT_ID(@name)
                        ORDER BY t.name;
                        """;
                    await foreach (var row in QueryAsync(sql, "@name", SqlServerNames.Quote(parent.Name), static r => r.GetString(0), cancellationToken)
                                       .ConfigureAwait(false))
                    {
                        yield return new DatabaseObject(parent.Name with { Name = row }, DatabaseObjectKinds.Trigger);
                    }

                    break;
                }

            default:
                break;
        }
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DatabaseObjectName tableOrView, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tableOrView);
        var prefix = SqlServerNames.CatalogPrefix(tableOrView.Database);

        // TYPE_NAME() resolves in the connection's current database only, so types are joined instead.
        var sql = $"""
            SELECT c.name, t.name AS type_name, bt.name AS base_type_name, t.is_user_defined,
                   c.max_length, c.precision, c.scale, c.is_nullable, c.column_id,
                   CAST(CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS bit) AS is_primary_key
            FROM {prefix}sys.columns c
            JOIN {prefix}sys.types t ON t.user_type_id = c.user_type_id
            LEFT JOIN {prefix}sys.types bt ON bt.user_type_id = c.system_type_id
            LEFT JOIN (SELECT ic.object_id, ic.column_id
                       FROM {prefix}sys.indexes i
                       JOIN {prefix}sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                       WHERE i.is_primary_key = 1) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(@name)
            ORDER BY c.column_id;
            """;

        var columns = new List<ColumnInfo>();
        await foreach (var column in QueryAsync(
                           sql,
                           "@name",
                           SqlServerNames.Quote(tableOrView),
                           static r => new ColumnInfo(
                               r.GetString(0),
                               SqlServerTypeFormatter.Format(
                                   r.GetString(1),
                                   r.IsDBNull(2) ? null : r.GetString(2),
                                   r.GetInt16(4),
                                   r.GetByte(5),
                                   r.GetByte(6),
                                   r.GetBoolean(3)),
                               r.GetBoolean(7),
                               r.GetInt32(8),
                               r.GetBoolean(9)),
                           cancellationToken).ConfigureAwait(false))
        {
            columns.Add(column);
        }

        return columns;
    }

    public async Task<string?> GetDefinitionAsync(DatabaseObject target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind == DatabaseObjectKinds.Table)
        {
            return await ScriptTableAsync(target.Name, cancellationToken).ConfigureAwait(false);
        }

        if (target.Kind is not (DatabaseObjectKinds.View or DatabaseObjectKinds.Procedure or DatabaseObjectKinds.Function or DatabaseObjectKinds.Trigger))
        {
            return null;
        }

        // sys.sql_modules instead of OBJECT_DEFINITION(), which only looks in the current database.
        var sql = $"SELECT m.definition FROM {SqlServerNames.CatalogPrefix(target.Name.Database)}sys.sql_modules m WHERE m.object_id = OBJECT_ID(@name);";
        await foreach (var definition in QueryAsync(sql, "@name", SqlServerNames.Quote(target.Name), static r => r.IsDBNull(0) ? null : r.GetString(0), cancellationToken)
                           .ConfigureAwait(false))
        {
            // NULL: the module is encrypted or the login may not see its text.
            return definition;
        }

        return null;
    }

    /// <summary>
    /// A table has no stored source text, so one is written from the catalog. It lists columns and the primary
    /// key only and says so: it is for reading, not for recreating the table.
    /// </summary>
    private async Task<string?> ScriptTableAsync(DatabaseObjectName table, CancellationToken cancellationToken)
    {
        var columns = await GetColumnsAsync(table, cancellationToken).ConfigureAwait(false);
        if (columns.Count == 0)
        {
            return null;
        }

        var script = new System.Text.StringBuilder();
        script.AppendLine("-- Written by JetQueries from the catalog: columns and primary key only.");
        script.AppendLine("-- Defaults, identity, computed columns, indexes, foreign keys and other constraints are not shown.");
        script.Append("CREATE TABLE ").AppendLine(SqlServerNames.Quote(table with { Database = null }));
        script.AppendLine("(");
        var key = columns.Where(static c => c.IsPrimaryKey).Select(static c => SqlServerNames.Quote(c.Name)).ToList();
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            script.Append("    ").Append(SqlServerNames.Quote(column.Name)).Append(' ').Append(column.DataType)
                .Append(column.IsNullable ? " NULL" : " NOT NULL")
                .AppendLine(i < columns.Count - 1 || key.Count > 0 ? "," : string.Empty);
        }

        if (key.Count > 0)
        {
            script.Append("    PRIMARY KEY (").AppendJoin(", ", key).AppendLine(")");
        }

        script.AppendLine(");");
        return script.ToString();
    }

    /// <summary>The query listing objects of the requested kinds in one schema, or null when no kind applies.</summary>
    internal static string? BuildSchemaObjectsQuery(string? database, DatabaseObjectKinds kinds)
    {
        var prefix = SqlServerNames.CatalogPrefix(database);
        var types = ObjectTypes.Where(t => kinds.HasFlag(t.Kind)).SelectMany(static t => t.Types).ToArray();
        var parts = new List<string>(2);
        if (types.Length > 0)
        {
            parts.Add($"""
                SELECT o.name, o.type
                FROM {prefix}sys.objects o
                JOIN {prefix}sys.schemas s ON s.schema_id = o.schema_id
                WHERE s.name = @schema AND o.is_ms_shipped = 0 AND o.type IN ({string.Join(", ", types.Select(static t => $"'{t}'"))})
                """);
        }

        if (kinds.HasFlag(DatabaseObjectKinds.Type))
        {
            // User-defined types are not in sys.objects (table types are, but under an internal name).
            parts.Add($"""
                SELECT t.name, 'UT'
                FROM {prefix}sys.types t
                JOIN {prefix}sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = @schema AND t.is_user_defined = 1
                """);
        }

        return parts.Count == 0 ? null : string.Join("\nUNION ALL\n", parts) + "\nORDER BY 1;";
    }

    internal static DatabaseObjectKinds KindOf(string type)
    {
        if (type == "UT")
        {
            return DatabaseObjectKinds.Type;
        }

        foreach (var (kind, types) in ObjectTypes)
        {
            if (Array.IndexOf(types, type) >= 0)
            {
                return kind;
            }
        }

        return DatabaseObjectKinds.None;
    }

    private async IAsyncEnumerable<T> QueryAsync<T>(
        string sql,
        string? parameterName,
        string? parameterValue,
        Func<SqlDataReader, T> read,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Built from constant text and bracket-quoted identifiers only; values are parameters.
            command.CommandText = sql;
#pragma warning restore CA2100
            command.CommandTimeout = 30;
            if (parameterName is not null)
            {
                command.Parameters.Add(parameterName, System.Data.SqlDbType.NVarChar, 1024).Value = parameterValue;
            }

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

/// <summary>Formats catalog type information the way SQL Server tools show it: nvarchar(50), decimal(18,2), varchar(max).</summary>
internal static class SqlServerTypeFormatter
{
    /// <param name="typeName">Name of the column's type; may be an alias type.</param>
    /// <param name="baseTypeName">Name of the system type that defines the length rules, if known.</param>
    /// <param name="maxLength">Length in bytes; -1 for (max).</param>
    /// <param name="precision">Numeric precision.</param>
    /// <param name="scale">Numeric scale or fractional-second digits.</param>
    /// <param name="isUserDefined">Alias types are shown by their own name.</param>
    public static string Format(string typeName, string? baseTypeName, int maxLength, int precision, int scale, bool isUserDefined)
    {
        if (string.IsNullOrEmpty(typeName) || isUserDefined)
        {
            return typeName;
        }

        // Invariant: under the Turkish culture "BINARY".ToLower() would not be "binary".
        return (baseTypeName ?? typeName).ToUpperInvariant() switch
        {
            "NVARCHAR" or "NCHAR" => $"{typeName}({(maxLength == -1 ? "max" : (maxLength / 2).ToString(CultureInfo.InvariantCulture))})",
            "VARCHAR" or "CHAR" or "VARBINARY" or "BINARY" => $"{typeName}({(maxLength == -1 ? "max" : maxLength.ToString(CultureInfo.InvariantCulture))})",
            "DECIMAL" or "NUMERIC" => string.Create(CultureInfo.InvariantCulture, $"{typeName}({precision},{scale})"),
            "DATETIME2" or "DATETIMEOFFSET" or "TIME" => string.Create(CultureInfo.InvariantCulture, $"{typeName}({scale})"),
            "FLOAT" => precision == 53 ? typeName : string.Create(CultureInfo.InvariantCulture, $"{typeName}({precision})"),

            // vector(n) stores 4 bytes per float32 dimension plus an 8-byte header.
            "VECTOR" => maxLength > 8 ? string.Create(CultureInfo.InvariantCulture, $"{typeName}({(maxLength - 8) / 4})") : typeName,
            _ => typeName,
        };
    }
}
