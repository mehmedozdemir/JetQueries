using JetQueries.Providers.Language;
using JetQueries.Sdk;

namespace JetQueries.Provider.SqlServer.Language;

/// <summary>What the shared completion engine needs to know about T-SQL.</summary>
internal sealed class TSqlDialect : SqlDialect
{
    private static readonly string[] CallKeywords = ["EXEC", "EXECUTE"];

    public static TSqlDialect Instance { get; } = new();

    public override IReadOnlyCollection<string> Keywords => TSqlLexer.AllKeywords;

    public override IReadOnlyCollection<string> Functions => TSqlLexer.AllFunctions;

    public override IReadOnlyCollection<string> ProcedureCallKeywords => CallKeywords;

    public override bool QualifierMayBeDatabase => true;

    public override List<LanguageToken> Tokenize(string text) => TSqlLexer.Tokenize(text);

    public override bool IsBatchSeparator(ReadOnlySpan<char> keyword) => keyword.Equals("GO", StringComparison.OrdinalIgnoreCase);

    /// <summary>Brackets or double quotes removed; T-SQL names keep their case and are compared without it.</summary>
    public override string NameOf(string tokenText)
    {
        if (tokenText.Length >= 2 && tokenText[0] == '[')
        {
            return tokenText[1..(tokenText[^1] == ']' ? ^1 : ^0)].Replace("]]", "]", StringComparison.Ordinal);
        }

        return tokenText.Length >= 2 && tokenText[0] == '"' ? tokenText[1..(tokenText[^1] == '"' ? ^1 : ^0)] : tokenText;
    }

    public override string QuoteIfNeeded(string name)
    {
        var regular = name.Length > 0 && (char.IsLetter(name[0]) || name[0] is '_' or '#' or '@');
        for (var i = 1; regular && i < name.Length; i++)
        {
            regular = TSqlLexer.IsNamePart(name[i]);
        }

        return regular && !TSqlLexer.IsKeyword(name) ? name : SqlServerNames.Quote(name);
    }

    public override DatabaseObject SchemaParent(SqlDocument document, string? database, string? schema) =>
        new(new DatabaseObjectName(database, null, schema ?? DefaultSchema(document)), DatabaseObjectKinds.Schema);

    public override DatabaseObjectName ToName(SqlDocument document, IReadOnlyList<string> parts) => parts.Count switch
    {
        1 => new DatabaseObjectName(null, DefaultSchema(document), parts[0]),
        2 => new DatabaseObjectName(null, parts[0], parts[1]),
        _ => new DatabaseObjectName(parts[^3], parts[^2], parts[^1]),
    };

    // The login's default schema is not known here; dbo is by far the common case.
    private static string DefaultSchema(SqlDocument document) => document.DefaultSchema ?? "dbo";
}

/// <summary>Completion and navigation for T-SQL, on the engine shared between providers.</summary>
internal static class TSqlCompletion
{
    public static Task<IReadOnlyList<CompletionItem>> CompleteAsync(SqlDocument document, int offset, CancellationToken cancellationToken) =>
        SqlCompletionEngine.CompleteAsync(TSqlDialect.Instance, document, offset, cancellationToken);

    public static Task<SqlDefinition?> FindDefinitionAsync(SqlDocument document, int offset, CancellationToken cancellationToken) =>
        SqlCompletionEngine.FindDefinitionAsync(TSqlDialect.Instance, document, offset, cancellationToken);
}
