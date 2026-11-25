using JetQueries.Providers.Language;
using JetQueries.Sdk;

namespace JetQueries.Providers.Language;

/// <summary>A dialect described by the generic tokenizer's rules.</summary>
internal abstract class GenericSqlDialect(GenericSqlTokenizer tokenizer) : SqlDialect
{
    public GenericSqlTokenizer Tokenizer => tokenizer;

    public override IReadOnlyCollection<string> Keywords => tokenizer.Syntax.Keywords;

    public override IReadOnlyCollection<string> Functions => tokenizer.Syntax.Functions;

    public override List<LanguageToken> Tokenize(string text) => tokenizer.Tokenize(text);

    /// <summary>Removes the quotes of a quoted name and undoes doubled quote characters inside it.</summary>
    protected static string Unquote(string tokenText)
    {
        if (tokenText.Length == 0)
        {
            return tokenText;
        }

        var (open, close) = tokenText[0] switch
        {
            '"' => ('"', '"'),
            '`' => ('`', '`'),
            '[' => ('[', ']'),
            _ => ('\0', '\0'),
        };
        if (open == '\0')
        {
            return tokenText;
        }

        var inner = tokenText.Length >= 2 && tokenText[^1] == close ? tokenText[1..^1] : tokenText[1..];
        return open == close ? inner.Replace(new string(close, 2), close.ToString(), StringComparison.Ordinal) : inner;
    }

    /// <summary>Double-quotes a name, doubling any double quote inside it.</summary>
    protected static string DoubleQuote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>
/// Language service for a dialect without a parser of its own: colouring, completion and go to definition on
/// tokens and metadata, and an error for a string or comment that is never closed. Syntax beyond that is left
/// to the database, which reports it when the statement runs.
/// </summary>
internal sealed class GenericLanguageService(GenericSqlDialect dialect) : ISqlLanguageService, ISqlMetadataWarmUp, ISqlFormatter, ISignatureHelpProvider
{
    /// <inheritdoc />
    public string Format(string sql, SqlFormatOptions options) => SqlLayout.Format(sql, dialect.Tokenize, options);

    public SqlLineTokens TokenizeLine(ReadOnlySpan<char> line, int startState)
    {
        var tokens = new List<LanguageToken>();
        var endState = dialect.Tokenizer.TokenizeLine(line, startState, 0, tokens);
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
        return Task.Run(() => dialect.Tokenizer.Diagnose(document.Text), cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> WarmUpAsync(SqlDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return SqlCompletionEngine.WarmUpAsync(dialect, document, cancellationToken);
    }

    public Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(SqlDocument document, int offset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return SqlCompletionEngine.CompleteAsync(dialect, document, offset, cancellationToken);
    }

    public Task<SignatureHelp?> GetSignatureHelpAsync(SqlDocument document, int offset, CancellationToken cancellationToken) =>
        SqlCallHelp.GetSignatureHelpAsync(dialect, document, offset, cancellationToken);

    public Task<SqlDefinition?> GetDefinitionAsync(SqlDocument document, int offset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return SqlCompletionEngine.FindDefinitionAsync(dialect, document, offset, cancellationToken);
    }
}
