using JetQueries.Sdk;

namespace JetQueries.Provider.SqlServer.Language;

/// <summary>
/// Completion for T-SQL. It looks at the tokens around the caret to decide what is wanted — a table, a column
/// of a table in scope, a member after "alias." — and asks the metadata it is given for the names. It never
/// parses: half-typed statements are the normal case, and tokens always exist.
/// </summary>
internal static class TSqlCompletion
{
    private const int MaxItems = 400;

    private static readonly DatabaseObjectKinds TableKinds = DatabaseObjectKinds.Table | DatabaseObjectKinds.View;

    private static readonly HashSet<string> StatementOwners = new(StringComparer.OrdinalIgnoreCase) { "SELECT", "UPDATE", "DELETE", "INSERT", "MERGE" };

    // After these a table name is expected.
    private static readonly HashSet<string> TableIntroducers = new(StringComparer.OrdinalIgnoreCase) { "FROM", "JOIN", "INTO", "UPDATE", "TABLE", "APPLY" };

    // After these (or an operator, a comma, an opening parenthesis) an expression is expected.
    private static readonly HashSet<string> ExpressionIntroducers = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WHERE", "ON", "AND", "OR", "NOT", "SET", "BY", "HAVING", "WHEN", "THEN", "ELSE", "CASE", "DISTINCT", "TOP", "IN",
        "LIKE", "BETWEEN", "IS", "RETURN", "IF", "WHILE", "VALUES", "EXISTS",
    };

    // Clause keywords that end a FROM list.
    private static readonly HashSet<string> ClauseEnds = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "SET", "VALUES", "OPTION", "ON", "WHEN", "OUTPUT",
    };

    public static async Task<IReadOnlyList<CompletionItem>> CompleteAsync(SqlDocument document, int offset, CancellationToken cancellationToken)
    {
        var text = document.Text;
        offset = Math.Clamp(offset, 0, text.Length);
        var all = TSqlLexer.Tokenize(text);

        // Nothing is offered inside a comment or a string.
        foreach (var token in all)
        {
            if (token.Kind is not (SqlTokenKind.Comment or SqlTokenKind.StringLiteral) || token.Start >= offset || offset > token.End)
            {
                continue;
            }

            // Right after a closed string or a closed block comment the caret is outside it again.
            var closed = token.Kind == SqlTokenKind.StringLiteral
                ? token.Length > 1 && text[token.End - 1] == '\''
                : token.Length >= 4 && text.AsSpan(token.Start, 2) is "/*" && text.AsSpan(token.End - 2, 2) is "*/";
            if (offset < token.End || !closed)
            {
                return [];
            }
        }

        var tokens = all.Where(static t => t.Kind != SqlTokenKind.Comment).ToList();

        // The word being typed, if the caret is at the end of (or inside) a name.
        var prefixStart = offset;
        var replaceStart = offset;
        var before = tokens.FindLastIndex(t => t.Start < offset);
        if (before >= 0 && tokens[before].End >= offset && tokens[before].Kind is SqlTokenKind.Identifier or SqlTokenKind.Keyword or SqlTokenKind.Function or SqlTokenKind.DataType)
        {
            replaceStart = tokens[before].Start;
            prefixStart = text[replaceStart] is '[' or '"' ? replaceStart + 1 : replaceStart;
            before--;
        }

        var prefix = text[prefixStart..offset];
        var replace = new TextSpan(replaceStart, offset - replaceStart);
        var context = new Context(text, tokens, document, replace, prefix, cancellationToken);

        // "qualifier." — members of an alias, a table, a schema or a database.
        if (before >= 0 && context.Is(before, "."))
        {
            var parts = new List<string>();
            var i = before - 1;
            while (i >= 0 && tokens[i].Kind is SqlTokenKind.Identifier or SqlTokenKind.Keyword or SqlTokenKind.Function or SqlTokenKind.DataType)
            {
                parts.Insert(0, context.Name(i));
                if (i - 1 >= 0 && context.Is(i - 1, "."))
                {
                    i -= 2;
                }
                else
                {
                    break;
                }
            }

            return parts.Count == 0 ? [] : await MembersAsync(context, parts, before).ConfigureAwait(false);
        }

        var previous = before >= 0 ? context.Text(before) : string.Empty;
        var previousKind = before >= 0 ? tokens[before].Kind : SqlTokenKind.Punctuation;
        var scope = Scope.Find(context, before);

        var expectsTable = (previousKind == SqlTokenKind.Keyword && TableIntroducers.Contains(previous)) ||
                           (previous == "," && scope.InFromList(before)) ||
                           (context.IsWord(before, "FROM") && before > 0 && context.IsWord(before - 1, "DELETE"));
        if (expectsTable)
        {
            return await TablesAsync(context, null, null).ConfigureAwait(false);
        }

        if (previousKind == SqlTokenKind.Keyword && (previous.Equals("EXEC", StringComparison.OrdinalIgnoreCase) || previous.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase)))
        {
            return await ObjectsAsync(context, null, context.DefaultSchema, DatabaseObjectKinds.Procedure).ConfigureAwait(false);
        }

        var expectsExpression = before < 0
            ? false
            : previousKind == SqlTokenKind.Operator || previous is "," or "(" ||
              (previousKind == SqlTokenKind.Keyword && ExpressionIntroducers.Contains(previous));

        var items = new List<CompletionItem>();
        if (expectsExpression && scope.Tables.Count > 0)
        {
            foreach (var table in scope.Tables)
            {
                var columns = await document.Metadata!.GetColumnsAsync(table.ToName(context.DefaultSchema), cancellationToken).ConfigureAwait(false);
                foreach (var column in columns)
                {
                    context.Add(items, column.Name, CompletionItemKind.Column, $"{column.DataType} · {table.Alias ?? table.Parts[^1]}");
                }
            }

            foreach (var table in scope.Tables)
            {
                context.Add(items, table.Alias ?? table.Parts[^1], CompletionItemKind.Alias, string.Join('.', table.Parts));
            }
        }

        // Keywords and functions only once something has been typed: an empty list is better than 300 keywords.
        if (prefix.Length > 0)
        {
            if (expectsExpression)
            {
                foreach (var function in TSqlLexer.AllFunctions)
                {
                    context.Add(items, function, CompletionItemKind.Function, null, quote: false);
                }
            }

            foreach (var keyword in TSqlLexer.AllKeywords)
            {
                context.Add(items, keyword, CompletionItemKind.Keyword, null, quote: false);
            }
        }

        return Sorted(items);
    }

    /// <summary>
    /// What the name at <paramref name="offset"/> refers to: a table or view (by its name or through an alias,
    /// optionally with the column that was pointed at), a procedure or a function.
    /// </summary>
    public static async Task<SqlDefinition?> FindDefinitionAsync(SqlDocument document, int offset, CancellationToken cancellationToken)
    {
        var metadata = document.Metadata;
        if (metadata is null)
        {
            return null;
        }

        var text = document.Text;
        var tokens = TSqlLexer.Tokenize(text).Where(static t => t.Kind != SqlTokenKind.Comment).ToList();
        var index = tokens.FindIndex(t => t.Kind == SqlTokenKind.Identifier && t.Start <= offset && offset <= t.End);
        if (index < 0)
        {
            return null;
        }

        var context = new Context(text, tokens, document, new TextSpan(offset, 0), string.Empty, cancellationToken);

        // The whole dotted name around the pointed-at part.
        var first = index;
        while (first - 2 >= 0 && context.Is(first - 1, ".") && tokens[first - 2].Kind == SqlTokenKind.Identifier)
        {
            first -= 2;
        }

        var last = index;
        while (last + 2 < tokens.Count && context.Is(last + 1, ".") && tokens[last + 2].Kind == SqlTokenKind.Identifier)
        {
            last += 2;
        }

        var parts = new List<string>();
        for (var i = first; i <= last; i += 2)
        {
            parts.Add(context.Name(i));
        }

        var pointed = (index - first) / 2;
        var scope = Scope.Find(context, index);

        // alias, alias.column, table.column
        var table = scope.Tables.FirstOrDefault(t => string.Equals(t.Alias, parts[0], StringComparison.OrdinalIgnoreCase)) ??
                    (parts.Count <= 2 ? scope.Tables.FirstOrDefault(t => t.Alias is null && t.Parts.Count == 1 && string.Equals(t.Parts[0], parts[0], StringComparison.OrdinalIgnoreCase)) : null);
        if (table is not null && parts.Count <= 2 && !(parts.Count == 1 && table.Alias is null))
        {
            var target = await ResolveAsync(metadata, table.ToName(context.DefaultSchema), cancellationToken).ConfigureAwait(false);
            return target is null ? null : new SqlDefinition(target, MemberName: parts.Count == 2 && pointed == 1 ? parts[1] : null);
        }

        // [database.][schema.]object — only when the object part itself was pointed at.
        if (pointed == parts.Count - 1 && parts.Count <= 3)
        {
            var name = new TableReference(parts, null).ToName(context.DefaultSchema);
            if (await ResolveAsync(metadata, name, cancellationToken).ConfigureAwait(false) is { } found)
            {
                return new SqlDefinition(found);
            }
        }

        // A bare column name: the table in scope that has it.
        if (parts.Count == 1)
        {
            foreach (var candidate in scope.Tables)
            {
                var name = candidate.ToName(context.DefaultSchema);
                var columns = await metadata.GetColumnsAsync(name, cancellationToken).ConfigureAwait(false);
                if (columns.Any(c => string.Equals(c.Name, parts[0], StringComparison.OrdinalIgnoreCase)) &&
                    await ResolveAsync(metadata, name, cancellationToken).ConfigureAwait(false) is { } owner)
                {
                    return new SqlDefinition(owner, MemberName: parts[0]);
                }
            }
        }

        return null;
    }

    /// <summary>Finds the object with this name in its schema, which also tells what kind of object it is.</summary>
    private static async Task<DatabaseObject?> ResolveAsync(IMetadataProvider metadata, DatabaseObjectName name, CancellationToken cancellationToken)
    {
        var schema = new DatabaseObject(new DatabaseObjectName(name.Database, null, name.Schema ?? "dbo"), DatabaseObjectKinds.Schema);
        await foreach (var item in metadata.GetObjectsAsync(schema, TableKinds | DatabaseObjectKinds.Procedure | DatabaseObjectKinds.Function, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (string.Equals(item.Name.Name, name.Name, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<CompletionItem>> MembersAsync(Context context, List<string> parts, int dotIndex)
    {
        var metadata = context.Document.Metadata;
        if (metadata is null)
        {
            return [];
        }

        if (parts.Count == 1)
        {
            // An alias or a table of the statement: its columns.
            var scope = Scope.Find(context, dotIndex);
            var table = scope.Tables.FirstOrDefault(t => string.Equals(t.Alias, parts[0], StringComparison.OrdinalIgnoreCase)) ??
                        scope.Tables.FirstOrDefault(t => t.Alias is null && string.Equals(t.Parts[^1], parts[0], StringComparison.OrdinalIgnoreCase));
            if (table is not null)
            {
                var items = new List<CompletionItem>();
                foreach (var column in await metadata.GetColumnsAsync(table.ToName(context.DefaultSchema), context.CancellationToken).ConfigureAwait(false))
                {
                    context.Add(items, column.Name, CompletionItemKind.Column, column.DataType + (column.IsPrimaryKey ? " · PK" : string.Empty));
                }

                // Columns keep their table order: that is how people know their tables. No columns means the
                // word was not a table after all ("FROM kart." reads "kart" as one): try it as a schema.
                if (items.Count > 0)
                {
                    return items;
                }
            }

            // Otherwise a schema of the current database …
            var inSchema = await ObjectsAsync(context, null, parts[0], TableKinds | DatabaseObjectKinds.Procedure | DatabaseObjectKinds.Function).ConfigureAwait(false);
            if (inSchema.Count > 0)
            {
                return inSchema;
            }

            // … or a database: its schemas.
            var schemas = new List<CompletionItem>();
            await foreach (var schema in metadata.GetObjectsAsync(
                               new DatabaseObject(new DatabaseObjectName(null, null, parts[0]), DatabaseObjectKinds.Database),
                               DatabaseObjectKinds.Schema,
                               context.CancellationToken).ConfigureAwait(false))
            {
                context.Add(schemas, schema.Name.Name, CompletionItemKind.Schema, null);
            }

            return Sorted(schemas);
        }

        // database.schema.
        return parts.Count == 2
            ? await ObjectsAsync(context, parts[0], parts[1], TableKinds | DatabaseObjectKinds.Procedure | DatabaseObjectKinds.Function).ConfigureAwait(false)
            : [];
    }

    private static Task<IReadOnlyList<CompletionItem>> TablesAsync(Context context, string? database, string? schema) =>
        ObjectsAsync(context, database, schema ?? context.DefaultSchema, TableKinds);

    private static async Task<IReadOnlyList<CompletionItem>> ObjectsAsync(Context context, string? database, string schema, DatabaseObjectKinds kinds)
    {
        var metadata = context.Document.Metadata;
        if (metadata is null)
        {
            return [];
        }

        var items = new List<CompletionItem>();
        var parent = new DatabaseObject(new DatabaseObjectName(database, null, schema), DatabaseObjectKinds.Schema);
        await foreach (var item in metadata.GetObjectsAsync(parent, kinds, context.CancellationToken).ConfigureAwait(false))
        {
            var kind = item.Kind switch
            {
                DatabaseObjectKinds.View => CompletionItemKind.View,
                DatabaseObjectKinds.Procedure => CompletionItemKind.Procedure,
                DatabaseObjectKinds.Function => CompletionItemKind.Function,
                _ => CompletionItemKind.Table,
            };
            context.Add(items, item.Name.Name, kind, schema);
        }

        return Sorted(items);
    }

    private static IReadOnlyList<CompletionItem> Sorted(List<CompletionItem> items) =>
        [.. items
            .DistinctBy(static i => (i.Label.ToUpperInvariant(), i.Kind))
            .OrderBy(static i => i.Kind switch
            {
                CompletionItemKind.Column => 0,
                CompletionItemKind.Alias => 1,
                CompletionItemKind.Table or CompletionItemKind.View => 2,
                CompletionItemKind.Schema or CompletionItemKind.Database => 3,
                CompletionItemKind.Procedure or CompletionItemKind.Function => 4,
                _ => 5,
            })
            .ThenBy(static i => i.Label, StringComparer.OrdinalIgnoreCase)
            .Take(MaxItems)];

    /// <summary>Everything a completion request carries around.</summary>
    private sealed class Context(string text, List<TSqlToken> tokens, SqlDocument document, TextSpan replace, string prefix, CancellationToken cancellationToken)
    {
        public List<TSqlToken> Tokens { get; } = tokens;

        public SqlDocument Document { get; } = document;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public string DefaultSchema { get; } = document.DefaultSchema ?? "dbo";

        public string Text(int index) => text.Substring(Tokens[index].Start, Tokens[index].Length);

        public bool Is(int index, string value) => index >= 0 && index < Tokens.Count && text.AsSpan(Tokens[index].Start, Tokens[index].Length).SequenceEqual(value);

        public bool IsWord(int index, string keyword) =>
            index >= 0 && index < Tokens.Count && Tokens[index].Kind == SqlTokenKind.Keyword &&
            text.AsSpan(Tokens[index].Start, Tokens[index].Length).Equals(keyword, StringComparison.OrdinalIgnoreCase);

        /// <summary>A name token without its brackets or quotes.</summary>
        public string Name(int index)
        {
            var value = Text(index);
            if (value.Length >= 2 && value[0] == '[')
            {
                return value[1..(value[^1] == ']' ? ^1 : ^0)].Replace("]]", "]", StringComparison.Ordinal);
            }

            return value.Length >= 2 && value[0] == '"' ? value[1..(value[^1] == '"' ? ^1 : ^0)] : value;
        }

        public void Add(List<CompletionItem> items, string label, CompletionItemKind kind, string? detail, bool quote = true)
        {
            if (label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new CompletionItem(label, kind, replace) { InsertText = quote ? QuoteIfNeeded(label) : label, Detail = detail });
            }
        }

        private static string QuoteIfNeeded(string name)
        {
            var regular = name.Length > 0 && (char.IsLetter(name[0]) || name[0] is '_' or '#' or '@');
            for (var i = 1; regular && i < name.Length; i++)
            {
                regular = TSqlLexer.IsNamePart(name[i]);
            }

            return regular && !TSqlLexer.IsKeyword(name) ? name : SqlServerNames.Quote(name);
        }
    }

    /// <summary>A table named in the statement around the caret.</summary>
    private sealed record TableReference(IReadOnlyList<string> Parts, string? Alias)
    {
        public DatabaseObjectName ToName(string defaultSchema) => Parts.Count switch
        {
            1 => new DatabaseObjectName(null, defaultSchema, Parts[0]),
            2 => new DatabaseObjectName(null, Parts[0], Parts[1]),
            _ => new DatabaseObjectName(Parts[^3], Parts[^2], Parts[^1]),
        };
    }

    /// <summary>The statement (or subquery) the caret is in, and the tables it names — before and after the caret.</summary>
    private sealed class Scope
    {
        private readonly List<(int Start, int End)> _fromLists = [];

        public List<TableReference> Tables { get; } = [];

        public bool InFromList(int tokenIndex) => _fromLists.Exists(range => tokenIndex >= range.Start && tokenIndex <= range.End);

        public static Scope Find(Context context, int from)
        {
            var scope = new Scope();
            var tokens = context.Tokens;
            var index = from;

            // Innermost statement first, then the ones around it: a subquery may use the outer tables.
            for (var level = 0; level < 4 && index >= 0; level++)
            {
                var depth = 0;
                var owner = -1;
                for (var i = index; i >= 0; i--)
                {
                    var value = context.Text(i);
                    if (value == ")")
                    {
                        depth++;
                    }
                    else if (value == "(")
                    {
                        if (depth > 0)
                        {
                            depth--;
                        }
                    }
                    else if (depth == 0 && (value == ";" || context.IsWord(i, "GO")))
                    {
                        break;
                    }
                    else if (depth == 0 && tokens[i].Kind == SqlTokenKind.Keyword && StatementOwners.Contains(value))
                    {
                        owner = i;
                        break;
                    }
                }

                if (owner < 0)
                {
                    break;
                }

                scope.Collect(context, owner);
                index = owner - 1;
            }

            return scope;
        }

        private void Collect(Context context, int owner)
        {
            var tokens = context.Tokens;
            var depth = 0;
            var fromStart = -1;
            for (var i = owner; i < tokens.Count; i++)
            {
                var value = context.Text(i);
                if (value == "(")
                {
                    depth++;
                    continue;
                }

                if (value == ")")
                {
                    if (depth == 0)
                    {
                        break;
                    }

                    depth--;
                    continue;
                }

                if (depth > 0)
                {
                    continue;
                }

                if (value == ";" || (i > owner && tokens[i].Kind == SqlTokenKind.Keyword &&
                                     (context.IsWord(i, "GO") || (StatementOwners.Contains(value) && !context.IsWord(i - 1, "INSERT") && !IsInsertSelect(context, owner, i)))))
                {
                    break;
                }

                var isKeyword = tokens[i].Kind == SqlTokenKind.Keyword;
                if (isKeyword && ClauseEnds.Contains(value) && fromStart >= 0)
                {
                    _fromLists.Add((fromStart, i - 1));
                    fromStart = -1;
                }

                var introduces = (isKeyword && TableIntroducers.Contains(value)) || (value == "," && fromStart >= 0) ||
                                 (isKeyword && value.Equals("DELETE", StringComparison.OrdinalIgnoreCase) && !context.IsWord(i + 1, "FROM"));
                if (!introduces)
                {
                    continue;
                }

                if (isKeyword && value.Equals("FROM", StringComparison.OrdinalIgnoreCase))
                {
                    fromStart = i;
                }

                var reference = ReadTable(context, i + 1, out var next);
                if (reference is not null && !Tables.Exists(t => t.Alias == reference.Alias && t.Parts.SequenceEqual(reference.Parts)))
                {
                    Tables.Add(reference);
                }

                i = Math.Max(i, next - 1);
            }

            if (fromStart >= 0)
            {
                _fromLists.Add((fromStart, tokens.Count));
            }
        }

        // INSERT INTO t (…) SELECT … FROM s: the SELECT belongs to the INSERT.
        private static bool IsInsertSelect(Context context, int owner, int index) => context.IsWord(owner, "INSERT") && context.IsWord(index, "SELECT");

        /// <summary>Reads "name[.name…] [AS] [alias]" starting at <paramref name="index"/>.</summary>
        private static TableReference? ReadTable(Context context, int index, out int next)
        {
            var tokens = context.Tokens;
            var parts = new List<string>();
            var i = index;
            while (i < tokens.Count && tokens[i].Kind == SqlTokenKind.Identifier)
            {
                parts.Add(context.Name(i));
                i++;
                if (context.Is(i, ".") && i + 1 < tokens.Count && tokens[i + 1].Kind == SqlTokenKind.Identifier)
                {
                    i++;
                }
                else
                {
                    break;
                }
            }

            next = i;
            if (parts.Count == 0)
            {
                return null;
            }

            if (context.IsWord(i, "AS"))
            {
                i++;
            }

            string? alias = null;
            if (i < tokens.Count && tokens[i].Kind == SqlTokenKind.Identifier && !context.Is(i + 1, "."))
            {
                alias = context.Name(i);
                i++;
            }

            next = i;
            return new TableReference(parts, alias);
        }
    }
}
