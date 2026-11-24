using System.Collections.Frozen;
using JetQueries.Providers.Language;
using JetQueries.Sdk;

namespace JetQueries.Provider.Sqlite.Language;

/// <summary>SQLite's lexical rules and word lists.</summary>
internal static class SqliteSyntax
{
    // SQLite's keyword list (sqlite.org/lang_keywords.html). Any of them used as a name is safest quoted.
    private static readonly string[] AllKeywords =
    [
        "ABORT", "ACTION", "ADD", "AFTER", "ALL", "ALTER", "ALWAYS", "ANALYZE", "AND", "AS", "ASC", "ATTACH", "AUTOINCREMENT", "BEFORE",
        "BEGIN", "BETWEEN", "BY", "CASCADE", "CASE", "CAST", "CHECK", "COLLATE", "COLUMN", "COMMIT", "CONFLICT", "CONSTRAINT", "CREATE",
        "CROSS", "CURRENT", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "DATABASE", "DEFAULT", "DEFERRABLE", "DEFERRED", "DELETE",
        "DESC", "DETACH", "DISTINCT", "DO", "DROP", "EACH", "ELSE", "END", "ESCAPE", "EXCEPT", "EXCLUDE", "EXCLUSIVE", "EXISTS", "EXPLAIN",
        "FAIL", "FILTER", "FIRST", "FOLLOWING", "FOR", "FOREIGN", "FROM", "FULL", "GENERATED", "GLOB", "GROUP", "GROUPS", "HAVING", "IF",
        "IGNORE", "IMMEDIATE", "IN", "INDEX", "INDEXED", "INITIALLY", "INNER", "INSERT", "INSTEAD", "INTERSECT", "INTO", "IS", "ISNULL",
        "JOIN", "KEY", "LAST", "LEFT", "LIKE", "LIMIT", "MATCH", "MATERIALIZED", "NATURAL", "NO", "NOT", "NOTHING", "NOTNULL", "NULL",
        "NULLS", "OF", "OFFSET", "ON", "OR", "ORDER", "OTHERS", "OUTER", "OVER", "PARTITION", "PLAN", "PRAGMA", "PRECEDING", "PRIMARY",
        "QUERY", "RAISE", "RANGE", "RECURSIVE", "REFERENCES", "REGEXP", "REINDEX", "RELEASE", "RENAME", "REPLACE", "RESTRICT", "RETURNING",
        "RIGHT", "ROLLBACK", "ROW", "ROWS", "SAVEPOINT", "SELECT", "SET", "STRICT", "TABLE", "TEMP", "TEMPORARY", "THEN", "TIES", "TO",
        "TRANSACTION", "TRIGGER", "UNBOUNDED", "UNION", "UNIQUE", "UPDATE", "USING", "VACUUM", "VALUES", "VIEW", "VIRTUAL", "WHEN", "WHERE",
        "WINDOW", "WITH", "WITHOUT",
    ];

    public static GenericSqlTokenizer Tokenizer { get; } = new(new GenericSqlSyntax
    {
        Keywords = Set(AllKeywords),
        Functions = Set(
            "ABS", "AVG", "CHANGES", "CHAR", "COALESCE", "CONCAT", "CONCAT_WS", "COUNT", "DATE", "DATETIME", "DENSE_RANK", "FIRST_VALUE",
            "FORMAT", "GLOB", "GROUP_CONCAT", "HEX", "IFNULL", "IIF", "INSTR", "JSON", "JSON_ARRAY", "JSON_EACH", "JSON_EXTRACT",
            "JSON_GROUP_ARRAY", "JSON_OBJECT", "JULIANDAY", "LAG", "LAST_INSERT_ROWID", "LAST_VALUE", "LEAD", "LENGTH", "LOWER", "LTRIM",
            "MAX", "MIN", "NTILE", "NULLIF", "PRINTF", "QUOTE", "RANDOM", "RANDOMBLOB", "RANK", "REPLACE", "ROUND", "ROW_NUMBER", "RTRIM",
            "STRFTIME", "STRING_AGG", "SUBSTR", "SUBSTRING", "SUM", "TIME", "TOTAL", "TOTAL_CHANGES", "TRIM", "TYPEOF", "UNICODE",
            "UNIXEPOCH", "UPPER", "ZEROBLOB"),
        Types = Set("ANY", "BLOB", "BOOLEAN", "CHARACTER", "DATETIME", "DECIMAL", "DOUBLE", "FLOAT", "INT", "INTEGER", "NUMERIC", "REAL", "TEXT", "VARCHAR"),
        Reserved = Set(AllKeywords),
        BracketIdentifiers = true,
        BacktickIdentifiers = true,
        ParameterPrefixes = "?:@$",
    });

    private static FrozenSet<string> Set(params string[] words) => FrozenSet.ToFrozenSet(words, StringComparer.OrdinalIgnoreCase);
}

/// <summary>What the shared completion engine needs to know about SQLite.</summary>
internal sealed class SqliteDialect() : GenericSqlDialect(SqliteSyntax.Tokenizer)
{
    public static SqliteDialect Instance { get; } = new();

    public override FunctionCatalog Catalog => SqliteFunctions.Catalog;

    public override DatabaseObjectKinds ResolvableKinds => DatabaseObjectKinds.Table | DatabaseObjectKinds.View;

    /// <summary>SQLite keeps names as written and compares them without regard to case.</summary>
    public override string NameOf(string tokenText) => Unquote(tokenText);

    public override string QuoteIfNeeded(string name)
    {
        var regular = name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_');
        foreach (var c in name)
        {
            regular &= char.IsLetterOrDigit(c) || c == '_';
        }

        return regular && !Tokenizer.IsReserved(name) ? name : DoubleQuote(name);
    }

    /// <summary>No schema means the usual lookup (temp, main, then attached databases), done by the provider's metadata.</summary>
    public override DatabaseObject? SchemaParent(SqlDocument document, string? database, string? schema) =>
        schema is null ? null : new DatabaseObject(new DatabaseObjectName(null, null, schema), DatabaseObjectKinds.Schema);

    public override DatabaseObjectName ToName(SqlDocument document, IReadOnlyList<string> parts) =>
        parts.Count == 1 ? new DatabaseObjectName(null, null, parts[0]) : new DatabaseObjectName(null, parts[^2], parts[^1]);
}
