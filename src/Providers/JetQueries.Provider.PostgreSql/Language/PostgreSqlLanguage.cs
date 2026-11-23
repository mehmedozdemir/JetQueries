using System.Collections.Frozen;
using JetQueries.Providers.Language;
using JetQueries.Sdk;

namespace JetQueries.Provider.PostgreSql.Language;

/// <summary>PostgreSQL's lexical rules and word lists.</summary>
internal static class PostgreSqlSyntax
{
    public static GenericSqlTokenizer Tokenizer { get; } = new(new GenericSqlSyntax
    {
        Keywords = Set(
            "ABORT", "ADD", "ALL", "ALTER", "ALWAYS", "ANALYZE", "AND", "ANY", "ARRAY", "AS", "ASC", "ATOMIC", "AUTHORIZATION", "BEGIN",
            "BETWEEN", "BOTH", "BY", "CALL", "CASCADE", "CASE", "CAST", "CHECK", "CLOSE", "CLUSTER", "COLLATE", "COLUMN", "COMMENT", "COMMIT",
            "CONCURRENTLY", "CONFLICT", "CONSTRAINT", "COPY", "CREATE", "CROSS", "CURRENT", "CURSOR", "DATABASE", "DEALLOCATE", "DECLARE",
            "DEFAULT", "DEFERRABLE", "DELETE", "DESC", "DISCARD", "DISTINCT", "DO", "DOMAIN", "DROP", "EACH", "ELSE", "END", "ENUM", "ESCAPE",
            "EXCEPT", "EXCLUDE", "EXECUTE", "EXISTS", "EXPLAIN", "EXTENSION", "FALSE", "FETCH", "FILTER", "FIRST", "FOLLOWING", "FOR",
            "FOREIGN", "FROM", "FULL", "FUNCTION", "GENERATED", "GRANT", "GROUP", "GROUPING", "HAVING", "IDENTITY", "IF", "ILIKE", "IMMUTABLE",
            "IN", "INCLUDE", "INDEX", "INHERITS", "INNER", "INSERT", "INSTEAD", "INTERSECT", "INTO", "IS", "ISNULL", "JOIN", "KEY", "LANGUAGE",
            "LAST", "LATERAL", "LEADING", "LEFT", "LIKE", "LIMIT", "LISTEN", "LOCK", "MATERIALIZED", "MERGE", "NATURAL", "NO", "NOT",
            "NOTHING", "NOTIFY", "NOTNULL", "NOWAIT", "NULL", "NULLS", "OF", "OFFSET", "ON", "ONLY", "OR", "ORDER", "OUT", "OUTER", "OVER",
            "OWNED", "OWNER", "PARTITION", "POLICY", "PRECEDING", "PREPARE", "PRIMARY", "PROCEDURE", "RANGE", "RECURSIVE", "REFERENCES",
            "REFRESH", "REINDEX", "RELEASE", "RENAME", "REPLACE", "RESET", "RESTRICT", "RETURN", "RETURNING", "RETURNS", "REVOKE", "RIGHT",
            "ROLE", "ROLLBACK", "ROW", "ROWS", "RULE", "SAVEPOINT", "SCHEMA", "SECURITY", "SELECT", "SEQUENCE", "SET", "SETOF", "SHOW",
            "SIMILAR", "SKIP", "SOME", "STABLE", "STRICT", "TABLE", "TABLESAMPLE", "TABLESPACE", "TEMP", "TEMPORARY", "THEN", "TIES", "TO",
            "TRAILING", "TRANSACTION", "TRIGGER", "TRUE", "TRUNCATE", "TYPE", "UNBOUNDED", "UNION", "UNIQUE", "UNLOGGED", "UPDATE", "USING",
            "VACUUM", "VALUES", "VARIADIC", "VIEW", "VOLATILE", "WHEN", "WHERE", "WINDOW", "WITH", "WITHIN", "WITHOUT"),
        Functions = Set(
            "ABS", "AGE", "ARRAY_AGG", "ARRAY_LENGTH", "ARRAY_TO_STRING", "AVG", "BOOL_AND", "BOOL_OR", "BTRIM", "CEIL", "CHAR_LENGTH",
            "CLOCK_TIMESTAMP", "COALESCE", "CONCAT", "CONCAT_WS", "COUNT", "CURRENT_DATE", "CURRENT_SCHEMA", "CURRENT_TIME",
            "CURRENT_TIMESTAMP", "CURRENT_USER", "DATE_PART", "DATE_TRUNC", "DENSE_RANK", "EXTRACT", "FIRST_VALUE", "FLOOR", "FORMAT",
            "GEN_RANDOM_UUID", "GENERATE_SERIES", "GREATEST", "INITCAP", "JSON_AGG", "JSON_BUILD_OBJECT", "JSONB_AGG", "JSONB_BUILD_OBJECT",
            "JSONB_SET", "LAG", "LAST_VALUE", "LEAD", "LEAST", "LEFT", "LENGTH", "LOWER", "LPAD", "LTRIM", "MAX", "MIN", "MOD", "NEXTVAL",
            "NOW", "NULLIF", "POSITION", "POWER", "RANDOM", "RANK", "REGEXP_MATCH", "REGEXP_REPLACE", "REPLACE", "RIGHT", "ROUND",
            "ROW_NUMBER", "RPAD", "RTRIM", "SPLIT_PART", "STRING_AGG", "STRPOS", "SUBSTRING", "SUM", "TO_CHAR", "TO_DATE", "TO_JSON",
            "TO_JSONB", "TO_NUMBER", "TO_TIMESTAMP", "TRANSLATE", "TRIM", "TRUNC", "UNNEST", "UPPER", "CURRVAL", "SETVAL"),
        Types = Set(
            "BIGINT", "BIGSERIAL", "BIT", "BOOL", "BOOLEAN", "BYTEA", "CHAR", "CHARACTER", "CIDR", "DATE", "DECIMAL", "DOUBLE", "FLOAT4",
            "FLOAT8", "INET", "INT", "INT2", "INT4", "INT8", "INTEGER", "INTERVAL", "JSON", "JSONB", "MACADDR", "MONEY", "NUMERIC", "OID",
            "PRECISION", "REAL", "REGCLASS", "SERIAL", "SMALLINT", "SMALLSERIAL", "TEXT", "TIME", "TIMESTAMP", "TIMESTAMPTZ", "TIMETZ",
            "TSQUERY", "TSVECTOR", "UUID", "VARCHAR", "VARYING", "XML", "ZONE"),

        // The reserved words of PostgreSQL's keyword appendix: a name equal to one of these must be quoted.
        Reserved = Set(
            "ALL", "ANALYSE", "ANALYZE", "AND", "ANY", "ARRAY", "AS", "ASC", "ASYMMETRIC", "AUTHORIZATION", "BINARY", "BOTH", "CASE", "CAST",
            "CHECK", "COLLATE", "COLLATION", "COLUMN", "CONCURRENTLY", "CONSTRAINT", "CREATE", "CROSS", "CURRENT_CATALOG", "CURRENT_DATE",
            "CURRENT_ROLE", "CURRENT_SCHEMA", "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "DEFAULT", "DEFERRABLE", "DESC",
            "DISTINCT", "DO", "ELSE", "END", "EXCEPT", "FALSE", "FETCH", "FOR", "FOREIGN", "FREEZE", "FROM", "FULL", "GRANT", "GROUP",
            "HAVING", "ILIKE", "IN", "INITIALLY", "INNER", "INTERSECT", "INTO", "IS", "ISNULL", "JOIN", "LATERAL", "LEADING", "LEFT", "LIKE",
            "LIMIT", "LOCALTIME", "LOCALTIMESTAMP", "NATURAL", "NOT", "NOTNULL", "NULL", "OFFSET", "ON", "ONLY", "OR", "ORDER", "OUTER",
            "OVERLAPS", "PLACING", "PRIMARY", "REFERENCES", "RETURNING", "RIGHT", "SELECT", "SESSION_USER", "SIMILAR", "SOME", "SYMMETRIC",
            "SYSTEM_USER", "TABLE", "TABLESAMPLE", "THEN", "TO", "TRAILING", "TRUE", "UNION", "UNIQUE", "USER", "USING", "VARIADIC",
            "VERBOSE", "WHEN", "WHERE", "WINDOW", "WITH"),
        NestedComments = true,
        DollarQuotes = true,
        EscapeStrings = true,
        ParameterPrefixes = "$:",
    });

    private static FrozenSet<string> Set(params string[] words) => FrozenSet.ToFrozenSet(words, StringComparer.OrdinalIgnoreCase);
}

/// <summary>What the shared completion engine needs to know about PostgreSQL.</summary>
internal sealed class PostgreSqlDialect() : GenericSqlDialect(PostgreSqlSyntax.Tokenizer)
{
    public static PostgreSqlDialect Instance { get; } = new();

    public override IReadOnlyCollection<string> ProcedureCallKeywords { get; } = ["CALL"];

    public override DatabaseObjectKinds ResolvableKinds =>
        DatabaseObjectKinds.Table | DatabaseObjectKinds.View | DatabaseObjectKinds.Procedure | DatabaseObjectKinds.Function | DatabaseObjectKinds.Sequence;

    /// <summary>
    /// A quoted name is taken as written; an unquoted one is folded to lower case. Like PostgreSQL itself, only
    /// ASCII letters are folded: an unquoted "İ" stays "İ" (and Turkish-culture rules never apply).
    /// </summary>
    public override string NameOf(string tokenText) =>
        tokenText.StartsWith('"') ? Unquote(tokenText) : FoldAscii(tokenText);

    /// <summary>Names are inserted as stored. A name that would change unquoted — capitals, spaces, a reserved word — gets quotes.</summary>
    public override string QuoteIfNeeded(string name)
    {
        var regular = name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_');
        foreach (var c in name)
        {
            regular &= (char.IsLetterOrDigit(c) && !char.IsUpper(c)) || c is '_' or '$';
        }

        return regular && !Tokenizer.IsReserved(name) ? name : DoubleQuote(name);
    }

    /// <summary>No schema means the session's search path, which the provider's metadata resolves itself.</summary>
    public override DatabaseObject? SchemaParent(SqlDocument document, string? database, string? schema) =>
        (schema ?? document.DefaultSchema) is { } name ? new DatabaseObject(new DatabaseObjectName(null, null, name), DatabaseObjectKinds.Schema) : null;

    public override DatabaseObjectName ToName(SqlDocument document, IReadOnlyList<string> parts) =>
        parts.Count == 1
            ? new DatabaseObjectName(null, document.DefaultSchema, parts[0])
            : new DatabaseObjectName(null, parts[^2], parts[^1]);

    internal static string FoldAscii(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (var i = 0; i < span.Length; i++)
        {
            var c = source[i];
            span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }
    });
}
