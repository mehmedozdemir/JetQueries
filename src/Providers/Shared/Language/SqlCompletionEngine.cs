using JetQueries.Sdk;

namespace JetQueries.Providers.Language;

/// <summary>A token of a whole document.</summary>
internal readonly record struct LanguageToken(SqlTokenKind Kind, int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// What the shared completion and navigation engine needs to know about one SQL dialect. This file is compiled
/// into each provider that uses it (providers may reference nothing but the SDK, and this is not SDK material).
/// </summary>
internal abstract class SqlDialect
{
    /// <summary>All tokens of the text, comments included, white space not.</summary>
    public abstract List<LanguageToken> Tokenize(string text);

    public abstract IReadOnlyCollection<string> Keywords { get; }

    public abstract IReadOnlyCollection<string> Functions { get; }

    /// <summary>Signatures and parameter values of the built-in functions; null when the dialect has none.</summary>
    public virtual FunctionCatalog? Catalog => null;

    /// <summary>Keywords after which a procedure name is expected (T-SQL's EXEC).</summary>
    public virtual IReadOnlyCollection<string> ProcedureCallKeywords => [];

    /// <summary>The kinds of schema objects a name in the text may refer to.</summary>
    public virtual DatabaseObjectKinds ResolvableKinds =>
        DatabaseObjectKinds.Table | DatabaseObjectKinds.View | DatabaseObjectKinds.Procedure | DatabaseObjectKinds.Function;

    /// <summary>Kinds of objects that have routines as members, reached with "name." (Oracle packages).</summary>
    public virtual DatabaseObjectKinds MemberContainers => DatabaseObjectKinds.None;

    /// <summary>Whether "x." may also mean a database (SQL Server's database.schema.object).</summary>
    public virtual bool QualifierMayBeDatabase => false;

    /// <summary>The name a token stands for: quotes removed, and the dialect's case rule applied.</summary>
    public abstract string NameOf(string tokenText);

    /// <summary>The name as it must be written in SQL: quoted only when it has to be.</summary>
    public abstract string QuoteIfNeeded(string name);

    /// <summary>Whether a keyword token separates batches on the client (T-SQL's GO).</summary>
    public virtual bool IsBatchSeparator(ReadOnlySpan<char> keyword) => false;

    /// <summary>The object to list the members of a schema from; a null schema means the default one.</summary>
    public abstract DatabaseObject? SchemaParent(SqlDocument document, string? database, string? schema);

    /// <summary>The full name a dotted name of one to three parts refers to.</summary>
    public abstract DatabaseObjectName ToName(SqlDocument document, IReadOnlyList<string> parts);

    /// <summary>Names declared in the text itself that are visible at an offset (PL/SQL variables and parameters).</summary>
    public virtual IEnumerable<(string Name, string? Detail)> LocalSymbols(string text, int offset) => [];
}

/// <summary>
/// Completion and "go to definition" for SQL dialects. It looks at the tokens around the caret to decide what
/// is wanted — a table, a column of a table in scope, a member after "alias." — and asks the metadata it is
/// given for the names. It never parses: half-typed statements are the normal case, and tokens always exist.
/// </summary>
internal static class SqlCompletionEngine
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
        "LIKE", "BETWEEN", "IS", "RETURN", "IF", "WHILE", "VALUES", "EXISTS", "ELSIF", "LOOP", "BEGIN",
    };

    // Clause keywords that end a FROM list.
    private static readonly HashSet<string> ClauseEnds = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "MINUS", "SET", "VALUES", "OPTION", "ON", "WHEN", "OUTPUT",
        "CONNECT", "START", "FETCH", "OFFSET", "FOR",
    };

    public static async Task<IReadOnlyList<CompletionItem>> CompleteAsync(SqlDialect dialect, SqlDocument document, int offset, CancellationToken cancellationToken)
    {
        var text = document.Text;
        offset = Math.Clamp(offset, 0, text.Length);
        var all = dialect.Tokenize(text);

        // On a parameter that takes a fixed set of values (a datepart, a format): those values, also inside a string.
        var inComment = all.Any(t => t.Kind == SqlTokenKind.Comment && t.Start < offset && offset < t.End);
        if (!inComment && SqlCallHelp.ValueCompletions(dialect, text, all, offset) is { Count: > 0 } values)
        {
            return values;
        }

        // Nothing else is offered inside a comment or a string.
        foreach (var token in all)
        {
            if (token.Kind is not (SqlTokenKind.Comment or SqlTokenKind.StringLiteral) || token.Start >= offset || offset > token.End)
            {
                continue;
            }

            // Right after a closed string or a closed block comment the caret is outside it again.
            var closed = token.Kind == SqlTokenKind.StringLiteral
                ? token.Length > 1 && text[token.End - 1] is '\'' or '$'
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
        if (before >= 0 && tokens[before].End >= offset && IsWordKind(tokens[before].Kind))
        {
            replaceStart = tokens[before].Start;
            prefixStart = text[replaceStart] is '[' or '"' or '`' ? replaceStart + 1 : replaceStart;
            before--;
        }

        var prefix = text[prefixStart..offset];
        var replace = new TextSpan(replaceStart, offset - replaceStart);
        var context = new Context(dialect, text, tokens, document, replace, prefix, cancellationToken);

        // "qualifier." — members of an alias, a table, a schema, a package or a database.
        if (before >= 0 && context.Is(before, "."))
        {
            var parts = new List<string>();
            var i = before - 1;
            while (i >= 0 && IsWordKind(tokens[i].Kind))
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

        // "SELECT … INTO" (PL/SQL, and T-SQL's SELECT INTO) is followed by variables or a new name, not by
        // an existing table; only INSERT INTO and MERGE INTO are.
        var intoOfSelect = context.IsWord(before, "INTO") && !OwnerIs(context, before, "INSERT", "MERGE");
        var expectsTable = (previousKind == SqlTokenKind.Keyword && TableIntroducers.Contains(previous) && !intoOfSelect) ||
                           (previous == "," && scope.InFromList(before)) ||
                           (context.IsWord(before, "FROM") && before > 0 && context.IsWord(before - 1, "DELETE"));
        if (expectsTable)
        {
            return await ObjectsAsync(context, null, null, TableKinds).ConfigureAwait(false);
        }

        if (previousKind == SqlTokenKind.Keyword && dialect.ProcedureCallKeywords.Contains(previous, StringComparer.OrdinalIgnoreCase))
        {
            return await ObjectsAsync(context, null, null, DatabaseObjectKinds.Procedure).ConfigureAwait(false);
        }

        var expectsExpression = before >= 0 &&
                                (intoOfSelect || previousKind == SqlTokenKind.Operator || previous is "," or "(" ||
                                 (previousKind == SqlTokenKind.Keyword && ExpressionIntroducers.Contains(previous)));

        var items = new List<CompletionItem>();
        if (expectsExpression)
        {
            foreach (var (name, detail) in dialect.LocalSymbols(text, offset))
            {
                context.Add(items, name, CompletionItemKind.Variable, detail);
            }

            if (scope.Tables.Count > 0 && document.Metadata is { } metadata)
            {
                foreach (var table in scope.Tables)
                {
                    var columns = await metadata.GetColumnsAsync(dialect.ToName(document, table.Parts), cancellationToken).ConfigureAwait(false);
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
        }

        // Keywords and functions only once something has been typed: an empty list is better than 300 keywords.
        if (prefix.Length > 0)
        {
            if (expectsExpression)
            {
                // Built-ins of the catalog come with their signature and what they do, and are inserted with
                // their parentheses (the caret between them, signature help open).
                var catalog = dialect.Catalog;
                foreach (var function in dialect.Functions.Concat(catalog?.Names ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!function.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var overloads = catalog?.Find(function) ?? [];
                    if (overloads.Count == 0)
                    {
                        context.Add(items, function, CompletionItemKind.Function, null, quote: false);
                        continue;
                    }

                    var first = overloads[0];
                    var takesArguments = overloads.Any(static o => o.Parameters.Count > 0 || o.IsVariadic);
                    items.Add(new CompletionItem(first.Name, CompletionItemKind.Function, replace)
                    {
                        InsertText = first.Name + "()",
                        OpensCall = takesArguments,
                        Detail = first.ShortSignature + (overloads.Count > 1 ? $" +{overloads.Count - 1}" : string.Empty),
                        Documentation = string.Join(
                            "\n",
                            new[] { first.Documentation.Text, first.ReturnType is { } returns ? "→ " + returns : null }.Where(static t => t is not null)),
                    });
                }
            }

            foreach (var keyword in dialect.Keywords)
            {
                context.Add(items, keyword, CompletionItemKind.Keyword, null, quote: false);
            }
        }

        return Sorted(items);
    }

    /// <summary>
    /// What the name at <paramref name="offset"/> refers to: a table or view (by its name or through an alias,
    /// optionally with the column that was pointed at), a routine, or a member of a package.
    /// </summary>
    public static async Task<SqlDefinition?> FindDefinitionAsync(SqlDialect dialect, SqlDocument document, int offset, CancellationToken cancellationToken)
    {
        var metadata = document.Metadata;
        if (metadata is null)
        {
            return null;
        }

        var text = document.Text;
        var tokens = dialect.Tokenize(text).Where(static t => t.Kind != SqlTokenKind.Comment).ToList();
        var index = tokens.FindIndex(t => t.Kind == SqlTokenKind.Identifier && t.Start <= offset && offset <= t.End);
        if (index < 0)
        {
            return null;
        }

        var context = new Context(dialect, text, tokens, document, new TextSpan(offset, 0), string.Empty, cancellationToken);

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
            var target = await ResolveAsync(context, dialect.ToName(document, table.Parts)).ConfigureAwait(false);
            return target is null ? null : new SqlDefinition(target, MemberName: parts.Count == 2 && pointed == 1 ? parts[1] : null);
        }

        // [database.][schema.]object — only when the object part itself was pointed at.
        if (pointed == parts.Count - 1 && parts.Count <= 3 &&
            await ResolveAsync(context, dialect.ToName(document, parts)).ConfigureAwait(false) is { } found)
        {
            return new SqlDefinition(found);
        }

        // [schema.]package.member — the package, with the member that was pointed at; or the package part itself.
        if (dialect.MemberContainers != DatabaseObjectKinds.None && parts.Count is 2 or 3)
        {
            var containerParts = pointed == parts.Count - 1 ? parts[..^1] : parts[..(pointed + 1)];
            if (await ResolveAsync(context, dialect.ToName(document, containerParts)).ConfigureAwait(false) is { } container &&
                (container.Kind & dialect.MemberContainers) != 0)
            {
                return new SqlDefinition(container, MemberName: pointed == parts.Count - 1 ? parts[^1] : null);
            }
        }

        // A bare column name: the table in scope that has it.
        if (parts.Count == 1)
        {
            foreach (var candidate in scope.Tables)
            {
                var name = dialect.ToName(document, candidate.Parts);
                var columns = await metadata.GetColumnsAsync(name, cancellationToken).ConfigureAwait(false);
                if (columns.Any(c => string.Equals(c.Name, parts[0], StringComparison.OrdinalIgnoreCase)) &&
                    await ResolveAsync(context, name).ConfigureAwait(false) is { } owner)
                {
                    return new SqlDefinition(owner, MemberName: parts[0]);
                }
            }
        }

        return null;
    }

    /// <summary>Whether the nearest statement keyword before a token is one of the given ones.</summary>
    private static bool OwnerIs(Context context, int index, params string[] keywords)
    {
        for (var i = index; i >= 0; i--)
        {
            if (context.Tokens[i].Kind == SqlTokenKind.Keyword && StatementOwners.Contains(context.Text(i)))
            {
                return keywords.Contains(context.Text(i), StringComparer.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static bool IsWordKind(SqlTokenKind kind) =>
        kind is SqlTokenKind.Identifier or SqlTokenKind.Keyword or SqlTokenKind.Function or SqlTokenKind.DataType;

    /// <summary>Finds the object with this name in its schema, which also tells what kind of object it is.</summary>
    private static async Task<DatabaseObject?> ResolveAsync(Context context, DatabaseObjectName name)
    {
        if (context.Document.Metadata is not { } metadata)
        {
            return null;
        }

        var parent = context.Dialect.SchemaParent(context.Document, name.Database, name.Schema);
        await foreach (var item in metadata.GetObjectsAsync(parent, context.Dialect.ResolvableKinds, context.CancellationToken).ConfigureAwait(false))
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
        var dialect = context.Dialect;
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
                foreach (var column in await metadata.GetColumnsAsync(dialect.ToName(context.Document, table.Parts), context.CancellationToken).ConfigureAwait(false))
                {
                    context.Add(items, column.Name, CompletionItemKind.Column, column.DataType + (column.IsPrimaryKey ? " · PK" : string.Empty));
                }

                // Columns keep their table order: that is how people know their tables. No columns means the
                // word was not a table after all ("FROM kart." reads "kart" as one): try the other meanings.
                if (items.Count > 0)
                {
                    return items;
                }
            }

            // A package of the default schema: its routines.
            if (await ContainerMembersAsync(context, [parts[0]]).ConfigureAwait(false) is { Count: > 0 } members)
            {
                return members;
            }

            // A schema: its objects.
            var inSchema = await ObjectsAsync(context, null, parts[0], dialect.ResolvableKinds).ConfigureAwait(false);
            if (inSchema.Count > 0 || !dialect.QualifierMayBeDatabase)
            {
                return inSchema;
            }

            // A database: its schemas.
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

        if (parts.Count != 2)
        {
            return [];
        }

        // schema.package. — or database.schema.
        if (await ContainerMembersAsync(context, parts).ConfigureAwait(false) is { Count: > 0 } routines)
        {
            return routines;
        }

        return dialect.QualifierMayBeDatabase
            ? await ObjectsAsync(context, parts[0], parts[1], dialect.ResolvableKinds).ConfigureAwait(false)
            : [];
    }

    /// <summary>The routines of the package a dotted name refers to; empty if it is not one.</summary>
    private static async Task<IReadOnlyList<CompletionItem>> ContainerMembersAsync(Context context, IReadOnlyList<string> parts)
    {
        var dialect = context.Dialect;
        if (dialect.MemberContainers == DatabaseObjectKinds.None ||
            await ResolveAsync(context, dialect.ToName(context.Document, parts)).ConfigureAwait(false) is not { } container ||
            (container.Kind & dialect.MemberContainers) == 0)
        {
            return [];
        }

        var items = new List<CompletionItem>();
        await foreach (var member in context.Document.Metadata!.GetObjectsAsync(
                           container, DatabaseObjectKinds.Procedure | DatabaseObjectKinds.Function, context.CancellationToken).ConfigureAwait(false))
        {
            context.Add(
                items,
                member.Name.Name,
                member.Kind == DatabaseObjectKinds.Function ? CompletionItemKind.Function : CompletionItemKind.Procedure,
                container.Name.Name);
        }

        return Sorted(items);
    }

    private static async Task<IReadOnlyList<CompletionItem>> ObjectsAsync(Context context, string? database, string? schema, DatabaseObjectKinds kinds)
    {
        var metadata = context.Document.Metadata;
        if (metadata is null)
        {
            return [];
        }

        var items = new List<CompletionItem>();
        var parent = context.Dialect.SchemaParent(context.Document, database, schema);
        var detail = parent?.Name.Name;
        await foreach (var item in metadata.GetObjectsAsync(parent, kinds, context.CancellationToken).ConfigureAwait(false))
        {
            var kind = item.Kind switch
            {
                DatabaseObjectKinds.View => CompletionItemKind.View,
                DatabaseObjectKinds.Procedure => CompletionItemKind.Procedure,
                DatabaseObjectKinds.Function => CompletionItemKind.Function,
                DatabaseObjectKinds.Package => CompletionItemKind.Package,
                DatabaseObjectKinds.Sequence => CompletionItemKind.Sequence,
                _ => CompletionItemKind.Table,
            };
            context.Add(items, item.Name.Name, kind, detail);
        }

        return Sorted(items);
    }

    private static IReadOnlyList<CompletionItem> Sorted(List<CompletionItem> items) =>
        [.. items
            .DistinctBy(static i => (i.Label.ToUpperInvariant(), i.Kind))
            .OrderBy(static i => i.Kind switch
            {
                CompletionItemKind.Variable => 0,
                CompletionItemKind.Column => 1,
                CompletionItemKind.Alias => 2,
                CompletionItemKind.Table or CompletionItemKind.View => 3,
                CompletionItemKind.Schema or CompletionItemKind.Database => 4,
                CompletionItemKind.Package or CompletionItemKind.Sequence => 5,
                CompletionItemKind.Procedure or CompletionItemKind.Function => 6,
                _ => 7,
            })
            .ThenBy(static i => i.Label, StringComparer.OrdinalIgnoreCase)
            .Take(MaxItems)];

    /// <summary>Everything a request carries around.</summary>
    private sealed class Context(
        SqlDialect dialect, string text, List<LanguageToken> tokens, SqlDocument document, TextSpan replace, string prefix, CancellationToken cancellationToken)
    {
        public SqlDialect Dialect { get; } = dialect;

        public List<LanguageToken> Tokens { get; } = tokens;

        public SqlDocument Document { get; } = document;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public string Text(int index) => text.Substring(Tokens[index].Start, Tokens[index].Length);

        public bool Is(int index, string value) => index >= 0 && index < Tokens.Count && text.AsSpan(Tokens[index].Start, Tokens[index].Length).SequenceEqual(value);

        public bool IsWord(int index, string keyword) =>
            index >= 0 && index < Tokens.Count && Tokens[index].Kind == SqlTokenKind.Keyword &&
            text.AsSpan(Tokens[index].Start, Tokens[index].Length).Equals(keyword, StringComparison.OrdinalIgnoreCase);

        public bool IsBatchSeparator(int index) =>
            index >= 0 && index < Tokens.Count && Tokens[index].Kind == SqlTokenKind.Keyword &&
            Dialect.IsBatchSeparator(text.AsSpan(Tokens[index].Start, Tokens[index].Length));

        /// <summary>The name a token stands for.</summary>
        public string Name(int index) => Dialect.NameOf(Text(index));

        public void Add(List<CompletionItem> items, string label, CompletionItemKind kind, string? detail, bool quote = true)
        {
            if (label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new CompletionItem(label, kind, replace) { InsertText = quote ? Dialect.QuoteIfNeeded(label) : label, Detail = detail });
            }
        }
    }

    /// <summary>A table named in the statement around the caret.</summary>
    private sealed record TableReference(IReadOnlyList<string> Parts, string? Alias);

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
                    else if (depth == 0 && (value == ";" || context.IsBatchSeparator(i)))
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
                                     (context.IsBatchSeparator(i) || (StatementOwners.Contains(value) && !context.IsWord(i - 1, "INSERT") && !IsInsertSelect(context, owner, i)))))
                {
                    break;
                }

                var isKeyword = tokens[i].Kind == SqlTokenKind.Keyword;
                if (isKeyword && ClauseEnds.Contains(value) && fromStart >= 0)
                {
                    _fromLists.Add((fromStart, i - 1));
                    fromStart = -1;
                }

                // "SELECT … INTO variable" in PL/SQL names variables, not a table; INSERT INTO does name one.
                var introduces = (isKeyword && TableIntroducers.Contains(value) &&
                                  !(value.Equals("INTO", StringComparison.OrdinalIgnoreCase) && !context.IsWord(owner, "INSERT") && !context.IsWord(owner, "MERGE"))) ||
                                 (value == "," && fromStart >= 0) ||
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
