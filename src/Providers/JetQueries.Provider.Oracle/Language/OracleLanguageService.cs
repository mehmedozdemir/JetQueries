using JetQueries.Providers.Language;
using JetQueries.Sdk;

namespace JetQueries.Provider.Oracle.Language;

/// <summary>What the shared completion engine needs to know about Oracle SQL and PL/SQL.</summary>
internal sealed class OracleDialect : SqlDialect
{
    public static OracleDialect Instance { get; } = new();

    public override IReadOnlyCollection<string> Keywords => PlSqlTokenizer.AllKeywords;

    public override IReadOnlyCollection<string> Functions => PlSqlTokenizer.AllFunctions;

    public override FunctionCatalog Catalog => OracleFunctions.Catalog;

    public override DatabaseObjectKinds ResolvableKinds =>
        DatabaseObjectKinds.Table | DatabaseObjectKinds.View | DatabaseObjectKinds.Procedure | DatabaseObjectKinds.Function |
        DatabaseObjectKinds.Package | DatabaseObjectKinds.Sequence;

    public override DatabaseObjectKinds MemberContainers => DatabaseObjectKinds.Package;

    public override List<LanguageToken> Tokenize(string text) => PlSqlTokenizer.Tokenize(text);

    /// <summary>A quoted name is taken as written; an unquoted one is stored by Oracle in upper case.</summary>
    public override string NameOf(string tokenText) =>
        tokenText.Length >= 2 && tokenText[0] == '"'
            ? tokenText[1..(tokenText[^1] == '"' ? ^1 : ^0)]
            : tokenText.ToUpperInvariant();

    /// <summary>
    /// Names are inserted as the dictionary has them. Only a name that would not survive unquoted — lower or
    /// mixed case, spaces, a reserved word — gets quotes.
    /// </summary>
    public override string QuoteIfNeeded(string name)
    {
        var regular = name.Length > 0 && char.IsLetter(name[0]);
        foreach (var c in name)
        {
            regular &= (char.IsLetterOrDigit(c) && !char.IsLower(c)) || c is '_' or '$' or '#';
        }

        return regular && !PlSqlTokenizer.IsReserved(name) ? name : "\"" + name + "\"";
    }

    /// <summary>No schema means the session's current schema, which the provider's metadata resolves itself.</summary>
    public override DatabaseObject? SchemaParent(SqlDocument document, string? database, string? schema) =>
        (schema ?? document.DefaultSchema) is { } owner
            ? new DatabaseObject(new DatabaseObjectName(null, null, owner), DatabaseObjectKinds.Schema)
            : null;

    public override DatabaseObjectName ToName(SqlDocument document, IReadOnlyList<string> parts) =>
        parts.Count == 1
            ? new DatabaseObjectName(null, document.DefaultSchema, parts[0])
            : new DatabaseObjectName(null, parts[^2], parts[^1]);

    /// <summary>
    /// Parameters, variables, constants, cursors and sibling routines declared in the text and visible at the
    /// offset, from the structural parser: innermost routine first, then what encloses it.
    /// </summary>
    public override IEnumerable<(string Name, string? Detail)> LocalSymbols(string text, int offset)
    {
        var found = new List<(string, string?)>();
        Visit(PlStructureParser.Parse(text).Root);
        found.Reverse();
        return found;

        void Visit(PlNode node)
        {
            var children = node.Children;
            if (children is null)
            {
                return;
            }

            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (child.Start >= offset)
                {
                    break;
                }

                switch (child.Kind)
                {
                    case PlNodeKind.Parameter or PlNodeKind.Variable or PlNodeKind.Constant or PlNodeKind.Cursor
                        or PlNodeKind.ExceptionDeclaration when child.Name is { } name:
                        found.Add((PlainName(name), Describe(text, child)));
                        break;
                    case PlNodeKind.Procedure or PlNodeKind.Function when child.Name is { } routine && node.Kind != PlNodeKind.Script:
                        found.Add((PlainName(routine), child.Kind == PlNodeKind.Function ? "function" : "procedure"));
                        break;
                    default:
                        break;
                }

                // Go into whatever the caret is inside; an unfinished last unit counts as containing it.
                var contains = offset <= child.End || i == children.Count - 1;
                if (contains && child.Kind is not (PlNodeKind.Parameter or PlNodeKind.Variable or PlNodeKind.Constant or PlNodeKind.Cursor
                        or PlNodeKind.Sql or PlNodeKind.Statement or PlNodeKind.SqlStatement or PlNodeKind.SqlPlusCommand))
                {
                    Visit(child);
                }
            }
        }
    }

    private static string PlainName(string name)
    {
        var dot = name.LastIndexOf('.');
        return (dot >= 0 ? name[(dot + 1)..] : name).Trim('"');
    }

    /// <summary>The declaration's type as written: "IN NUMBER", "VARCHAR2(40)", "CONSTANT PLS_INTEGER".</summary>
    private static string? Describe(string text, PlNode node)
    {
        if (node.NameStart < 0 || node.Name is null)
        {
            return null;
        }

        var from = node.NameStart + node.Name.Length;
        var rest = text.AsSpan(from, Math.Max(0, Math.Min(node.End, text.Length) - from));
        var cut = rest.IndexOfAny(";\n");
        rest = cut >= 0 ? rest[..cut] : rest;
        var assign = rest.IndexOf(":=", StringComparison.Ordinal);
        rest = assign >= 0 ? rest[..assign] : rest;
        var detail = rest.Trim().TrimEnd(',').Trim().ToString();
        return detail.Length is 0 or > 60 ? null : detail;
    }
}

/// <summary>
/// Language service for Oracle SQL and PL/SQL. Colouring and completion work on tokens; structure (which
/// routine the caret is in, what it declares, where a block is not closed) comes from the hand-written
/// structural parser chosen in ADR 0003. Expression and SQL syntax is not checked here: Oracle reports that
/// when the code is compiled or run.
/// </summary>
internal sealed class OracleLanguageService : ISqlLanguageService, ISqlMetadataWarmUp, ISqlFormatter, ISignatureHelpProvider
{
    /// <inheritdoc />
    public string Format(string sql, SqlFormatOptions options) => SqlLayout.Format(sql, PlSqlTokenizer.Tokenize, options);

    public SqlLineTokens TokenizeLine(ReadOnlySpan<char> line, int startState)
    {
        var tokens = new List<LanguageToken>();
        var endState = PlSqlTokenizer.TokenizeLine(line, startState, 0, tokens);
        var result = new SqlToken[tokens.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = new SqlToken(new TextSpan(tokens[i].Start, tokens[i].Length), tokens[i].Kind);
        }

        return new SqlLineTokens(result, endState);
    }

    public Task<IReadOnlyList<SqlDiagnostic>> GetDiagnosticsAsync(SqlDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Task.Run<IReadOnlyList<SqlDiagnostic>>(
            () =>
            {
                var text = document.Text;
                var problems = PlStructureParser.Parse(text).Diagnostics;
                if (problems.Count == 0)
                {
                    return [];
                }

                var tokens = PlSqlTokenizer.Tokenize(text);
                var diagnostics = new List<SqlDiagnostic>(problems.Count);
                foreach (var problem in problems)
                {
                    var offset = Math.Clamp(problem.Offset, 0, text.Length);
                    var index = tokens.FindIndex(t => t.Start <= offset && offset < t.End);
                    var span = index >= 0
                        ? new TextSpan(tokens[index].Start, tokens[index].Length)
                        : new TextSpan(Math.Max(0, Math.Min(offset, text.Length - 1)), Math.Min(1, text.Length));
                    diagnostics.Add(new SqlDiagnostic(span, problem.Message, SqlDiagnosticSeverity.Error));
                }

                return diagnostics;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> WarmUpAsync(SqlDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return SqlCompletionEngine.WarmUpAsync(OracleDialect.Instance, document, cancellationToken);
    }

    public Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(SqlDocument document, int offset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return SqlCompletionEngine.CompleteAsync(OracleDialect.Instance, document, offset, cancellationToken);
    }

    public Task<SignatureHelp?> GetSignatureHelpAsync(SqlDocument document, int offset, CancellationToken cancellationToken) =>
        SqlCallHelp.GetSignatureHelpAsync(OracleDialect.Instance, document, offset, cancellationToken);

    public Task<SqlDefinition?> GetDefinitionAsync(SqlDocument document, int offset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return SqlCompletionEngine.FindDefinitionAsync(OracleDialect.Instance, document, offset, cancellationToken);
    }
}
