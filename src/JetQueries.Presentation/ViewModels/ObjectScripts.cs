using System.Text;
using JetQueries.Core.Connections;
using JetQueries.Core.Metadata;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>What the explorer writes for an object.</summary>
public enum ObjectScriptKind
{
    /// <summary>The first rows (opened and run at once).</summary>
    SelectRows,

    /// <summary>SELECT of every column.</summary>
    Select,

    /// <summary>INSERT with a placeholder per column.</summary>
    Insert,

    /// <summary>UPDATE of the non-key columns by primary key.</summary>
    Update,

    /// <summary>DELETE by primary key.</summary>
    Delete,

    /// <summary>The object's definition (CREATE …), as the server or the catalog gives it.</summary>
    Create,

    /// <summary>The definition in the form that changes the object (ALTER …, CREATE OR REPLACE …).</summary>
    Alter,

    /// <summary>DROP of the object.</summary>
    Drop,
}

/// <summary>
/// Writes SELECT / INSERT / UPDATE / DELETE templates and definitions of explorer objects. Names are quoted by the
/// provider's quoting helper. Placeholders are NULLs with the column's type in a comment; an UPDATE or DELETE
/// without a primary key gets <c>WHERE 1 = 0</c>, so a template run by accident changes nothing.
/// </summary>
public static class ObjectScripts
{
    /// <summary>Rows the explorer's "Select first rows" reads (as SSMS does).</summary>
    public const int FirstRows = 1000;

    /// <summary>The script; null when there is nothing to write (no definition available).</summary>
    /// <param name="session">The object's connection.</param>
    /// <param name="metadata">Cached metadata.</param>
    /// <param name="target">The table, view or routine.</param>
    /// <param name="kind">What to write.</param>
    /// <param name="cancellationToken">Cancels reading metadata.</param>
    public static async Task<string?> BuildAsync(
        ConnectionSession session, IMetadataService metadata, DatabaseObject target, ObjectScriptKind kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(target);
        var provider = session.Provider;
        switch (kind)
        {
            case ObjectScriptKind.Create:
                return await new SessionMetadataProvider(session, metadata, target.Name.Database).GetDefinitionAsync(target, cancellationToken).ConfigureAwait(false);
            case ObjectScriptKind.Alter:
            {
                if (provider is not IObjectScripting alterScripting ||
                    await new SessionMetadataProvider(session, metadata, target.Name.Database).GetDefinitionAsync(target, cancellationToken).ConfigureAwait(false) is not { } definition)
                {
                    return null;
                }

                return alterScripting.ScriptAlter(target, definition);
            }

            case ObjectScriptKind.Drop:
                return provider is IObjectScripting dropScripting ? dropScripting.ScriptDrop(target) : null;
        }

        var name = Qualified(provider, target.Name);
        var columns = await metadata.GetColumnsAsync(session, target.Name, refresh: false, cancellationToken).ConfigureAwait(false);
        var quoted = columns.OrderBy(static c => c.Ordinal).Select(c => provider.QuoteIdentifier(c.Name)).ToList();
        static string Comment(ColumnInfo column) => column.DataType + (column.IsNullable ? string.Empty : " NOT NULL") + (column.IsPrimaryKey ? " · PK" : string.Empty);

        switch (kind)
        {
            case ObjectScriptKind.SelectRows:
                return provider is IRowLimitedSelect limited
                    ? limited.SelectRows(name, quoted, FirstRows) + ";"
                    : "SELECT " + string.Join(", ", quoted.DefaultIfEmpty("*")) + "\nFROM " + name + ";";
            case ObjectScriptKind.Select:
                return "SELECT\n    " + string.Join(",\n    ", quoted.DefaultIfEmpty("*")) + "\nFROM " + name + ";";
            case ObjectScriptKind.Insert:
            {
                var text = new StringBuilder();
                text.Append("INSERT INTO ").Append(name).Append(" (").AppendJoin(", ", quoted).Append(")\nVALUES (\n");
                for (var i = 0; i < columns.Count; i++)
                {
                    text.Append("    NULL").Append(i < columns.Count - 1 ? "," : " ").Append(" -- ").Append(columns[i].Name).Append(' ').Append(Comment(columns[i])).Append('\n');
                }

                return text.Append(");").ToString();
            }

            case ObjectScriptKind.Update:
            {
                var keys = columns.Where(static c => c.IsPrimaryKey).ToList();
                var values = columns.Where(static c => !c.IsPrimaryKey).ToList();
                if (values.Count == 0)
                {
                    values = [.. columns];
                }

                var text = new StringBuilder("UPDATE ").Append(name).Append('\n');
                for (var i = 0; i < values.Count; i++)
                {
                    text.Append(i == 0 ? "SET " : "    ").Append(provider.QuoteIdentifier(values[i].Name)).Append(" = NULL")
                        .Append(i < values.Count - 1 ? "," : string.Empty).Append(" -- ").Append(Comment(values[i])).Append('\n');
                }

                return text.Append(Where(provider, keys)).ToString();
            }

            case ObjectScriptKind.Delete:
                return "DELETE FROM " + name + "\n" + Where(provider, [.. columns.Where(static c => c.IsPrimaryKey)]);
            default:
                return null;
        }
    }

    /// <summary>Which scripts make sense for an object kind.</summary>
    public static bool HasData(DatabaseObjectKinds kind) => kind is DatabaseObjectKinds.Table or DatabaseObjectKinds.View;

    /// <summary>Whether ALTER can be written for a kind (programmable objects; tables are changed column by column).</summary>
    public static bool CanAlter(DatabaseObjectKinds kind) =>
        kind is DatabaseObjectKinds.View or DatabaseObjectKinds.Procedure or DatabaseObjectKinds.Function or DatabaseObjectKinds.Package
            or DatabaseObjectKinds.PackageBody or DatabaseObjectKinds.Trigger;

    /// <summary>Whether a definition can be asked for.</summary>
    public static bool HasDefinition(DatabaseObjectKinds kind) =>
        kind is DatabaseObjectKinds.Table or DatabaseObjectKinds.View or DatabaseObjectKinds.Procedure or DatabaseObjectKinds.Function
            or DatabaseObjectKinds.Package or DatabaseObjectKinds.PackageBody or DatabaseObjectKinds.Sequence or DatabaseObjectKinds.Trigger
            or DatabaseObjectKinds.Type;

    /// <summary>schema.name (or name alone), each part quoted; the database is the query tab's own.</summary>
    public static string Qualified(IDatabaseProvider provider, DatabaseObjectName name)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(name);
        return name.Schema is { Length: > 0 } schema ? provider.QuoteIdentifier(schema) + "." + provider.QuoteIdentifier(name.Name) : provider.QuoteIdentifier(name.Name);
    }

    private static string Where(IDatabaseProvider provider, List<ColumnInfo> keys)
    {
        if (keys.Count == 0)
        {
            // No primary key to go by: nothing matches until the user writes the condition.
            return "WHERE 1 = 0; -- no primary key: write the condition";
        }

        return "WHERE " + string.Join("\n  AND ", keys.Select(k => provider.QuoteIdentifier(k.Name) + " = NULL")) + "; -- " +
            string.Join(", ", keys.Select(static k => k.Name + " " + k.DataType));
    }
}
