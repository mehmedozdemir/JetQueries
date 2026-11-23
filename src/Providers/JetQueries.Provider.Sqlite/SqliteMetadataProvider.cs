using System.Runtime.CompilerServices;
using System.Text;
using JetQueries.Sdk;
using Microsoft.Data.Sqlite;

namespace JetQueries.Provider.Sqlite;

/// <summary>
/// Reads an SQLite database's schema table (<c>sqlite_schema</c>) and table-valued pragmas. Values are
/// parameters; a schema name (main, temp or an attached database) is only used after it was found in
/// <c>pragma_database_list</c>, and then quoted by <see cref="SqliteNames"/>.
/// </summary>
/// <remarks>
/// Naming convention: objects are <c>(null, schema, name)</c> with names as written in their CREATE
/// statement. A null schema means SQLite's own lookup order: temp, main, then attached databases.
/// </remarks>
internal sealed class SqliteMetadataProvider(SqliteConnection connection) : IMetadataProvider
{
    public async IAsyncEnumerable<DatabaseObject> GetObjectsAsync(
        DatabaseObject? parent,
        DatabaseObjectKinds kinds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var databases = await DatabasesAsync(cancellationToken).ConfigureAwait(false);
        if (parent is null && kinds.HasFlag(DatabaseObjectKinds.Schema))
        {
            foreach (var database in databases)
            {
                yield return new DatabaseObject(new DatabaseObjectName(null, null, database), DatabaseObjectKinds.Schema);
            }
        }

        if (parent is not null && parent.Kind != DatabaseObjectKinds.Schema)
        {
            yield break;
        }

        var types = new List<string>();
        if (kinds.HasFlag(DatabaseObjectKinds.Table))
        {
            types.Add("table");
        }

        if (kinds.HasFlag(DatabaseObjectKinds.View))
        {
            types.Add("view");
        }

        if (types.Count == 0 || Find(databases, parent?.Name.Name ?? "main") is not { } schema)
        {
            yield break;
        }

        // SQLite's own tables (sqlite_sequence, sqlite_stat1) are internal.
        var sql = $"""
            SELECT s.name, s.type FROM {SqliteNames.Quote(schema)}.sqlite_schema s
            WHERE s.type IN ({string.Join(", ", types.Select(static t => $"'{t}'"))}) AND s.name NOT LIKE 'sqlite!_%' ESCAPE '!'
            ORDER BY s.name COLLATE NOCASE
            """;
        await foreach (var row in QueryAsync(sql, [], static r => (Name: r.GetString(0), Type: r.GetString(1)), cancellationToken).ConfigureAwait(false))
        {
            yield return new DatabaseObject(
                new DatabaseObjectName(null, schema, row.Name),
                row.Type == "view" ? DatabaseObjectKinds.View : DatabaseObjectKinds.Table);
        }
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DatabaseObjectName tableOrView, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tableOrView);

        // table_xinfo also lists generated columns (hidden 2 and 3); hidden 1 is a virtual table's internal column.
        var withSchema = tableOrView.Schema is not null;
        var sql = withSchema
            ? "SELECT name, type, \"notnull\", cid, pk FROM pragma_table_xinfo(@name, @schema) WHERE hidden <> 1 ORDER BY cid"
            : "SELECT name, type, \"notnull\", cid, pk FROM pragma_table_xinfo(@name) WHERE hidden <> 1 ORDER BY cid";
        (string, object?)[] parameters = withSchema ? [("@name", tableOrView.Name), ("@schema", tableOrView.Schema)] : [("@name", tableOrView.Name)];

        var columns = new List<ColumnInfo>();
        await foreach (var column in QueryAsync(
                           sql,
                           parameters,
                           static r => new ColumnInfo(r.GetString(0), r.GetString(1), r.GetInt64(2) == 0, (int)r.GetInt64(3) + 1, r.GetInt64(4) > 0),
                           cancellationToken).ConfigureAwait(false))
        {
            columns.Add(column);
        }

        return columns;
    }

    /// <summary>The CREATE statement SQLite stored; for a table, followed by its indexes and triggers.</summary>
    public async Task<string?> GetDefinitionAsync(DatabaseObject target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind is not (DatabaseObjectKinds.Table or DatabaseObjectKinds.View))
        {
            return null;
        }

        var databases = await DatabasesAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<string> lookup = target.Name.Schema is { } given
            ? Find(databases, given) is { } found ? [found] : []
            : databases.OrderBy(static d => d.Equals("temp", StringComparison.OrdinalIgnoreCase) ? 0 : d.Equals("main", StringComparison.OrdinalIgnoreCase) ? 1 : 2);

        foreach (var schema in lookup)
        {
            var source = SqliteNames.Quote(schema) + ".sqlite_schema";
            string? name = null;
            var script = new StringBuilder();
            await foreach (var row in QueryAsync(
                               $"SELECT s.name, s.sql FROM {source} s WHERE s.name = @name COLLATE NOCASE AND s.type IN ('table', 'view') AND s.sql IS NOT NULL",
                               [("@name", target.Name.Name)],
                               static r => (Name: r.GetString(0), Sql: r.GetString(1)),
                               cancellationToken).ConfigureAwait(false))
            {
                name = row.Name;
                script.Append(row.Sql.TrimEnd().TrimEnd(';')).Append(";\n");
            }

            if (name is null)
            {
                continue;
            }

            await foreach (var sql in QueryAsync(
                               $"SELECT s.sql FROM {source} s WHERE s.tbl_name = @name AND s.type IN ('index', 'trigger') AND s.sql IS NOT NULL ORDER BY s.type, s.name",
                               [("@name", name)],
                               static r => r.GetString(0),
                               cancellationToken).ConfigureAwait(false))
            {
                script.Append('\n').Append(sql.TrimEnd().TrimEnd(';')).Append(";\n");
            }

            return script.ToString();
        }

        return null;
    }

    private static string? Find(IReadOnlyList<string> databases, string name) =>
        databases.FirstOrDefault(d => d.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>main, temp (once it exists) and attached databases, in that order.</summary>
    private async Task<IReadOnlyList<string>> DatabasesAsync(CancellationToken cancellationToken)
    {
        var names = new List<string>();
        await foreach (var name in QueryAsync("SELECT name FROM pragma_database_list ORDER BY seq", [], static r => r.GetString(0), cancellationToken).ConfigureAwait(false))
        {
            names.Add(name);
        }

        return names;
    }

    private async IAsyncEnumerable<T> QueryAsync<T>(
        string sql,
        (string Name, object? Value)[] parameters,
        Func<SqliteDataReader, T> read,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Constant text; schema names come from pragma_database_list and are quoted; values are parameters.
            command.CommandText = sql;
#pragma warning restore CA2100
            command.CommandTimeout = 30;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
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
