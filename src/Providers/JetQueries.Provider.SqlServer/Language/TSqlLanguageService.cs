using JetQueries.Sdk;

namespace JetQueries.Provider.SqlServer.Language;

/// <summary>
/// Language service for T-SQL: colouring and completion. Both work on tokens, not on a parse tree, because the
/// text being typed is almost never a complete statement.
/// </summary>
internal sealed class TSqlLanguageService : ISqlLanguageService
{
    public SqlLineTokens TokenizeLine(ReadOnlySpan<char> line, int startState)
    {
        var tokens = new List<TSqlToken>();
        var endState = TSqlLexer.TokenizeLine(line, startState, 0, tokens);
        var result = new SqlToken[tokens.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = new SqlToken(new TextSpan(tokens[i].Start, tokens[i].Length), tokens[i].Kind);
        }

        return new SqlLineTokens(result, endState);
    }

    public Task<IReadOnlyList<SqlDiagnostic>> GetDiagnosticsAsync(SqlDocument document, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SqlDiagnostic>>([]);

    public Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(SqlDocument document, int offset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return TSqlCompletion.CompleteAsync(document, offset, cancellationToken);
    }

    public Task<SqlDefinition?> GetDefinitionAsync(SqlDocument document, int offset, CancellationToken cancellationToken) =>
        Task.FromResult<SqlDefinition?>(null);
}

/// <summary>Splits a script on <c>GO</c> lines the way SQL Server's own tools do.</summary>
internal static class TSqlBatchSplitter
{
    public static IReadOnlyList<ScriptBatch> Split(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var batches = new List<ScriptBatch>();
        var tokens = new List<TSqlToken>();
        var state = 0;
        var lineStart = 0;
        var lineNumber = 0;
        var batchStart = 0;
        var batchLine = 0;

        while (lineStart <= script.Length)
        {
            var newLine = script.IndexOf('\n', lineStart);
            var lineEnd = newLine < 0 ? script.Length : newLine;
            var line = script.AsSpan(lineStart, lineEnd - lineStart);

            // GO only counts when the line does not start inside a string or comment.
            var startsClean = state == 0;
            tokens.Clear();
            state = TSqlLexer.TokenizeLine(line.TrimEnd('\r'), state, 0, tokens);
            if (startsClean && IsSeparator(line, tokens, out var repeat))
            {
                Add(script[batchStart..lineStart], batchLine, repeat);
                batchStart = newLine < 0 ? script.Length : newLine + 1;
                batchLine = lineNumber + 1;
            }

            if (newLine < 0)
            {
                break;
            }

            lineStart = newLine + 1;
            lineNumber++;
        }

        Add(script[Math.Min(batchStart, script.Length)..], batchLine, 1);
        return batches;

        void Add(string text, int startLine, int repeatCount)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                batches.Add(new ScriptBatch(text, startLine, repeatCount));
            }
        }
    }

    /// <summary>A line that is only <c>GO</c>, optionally with a count and a trailing comment.</summary>
    private static bool IsSeparator(ReadOnlySpan<char> line, List<TSqlToken> tokens, out int repeat)
    {
        repeat = 1;
        var significant = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == SqlTokenKind.Comment)
            {
                continue;
            }

            var text = line.Slice(token.Start, token.Length);
            if (significant == 0 && text.Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                significant++;
            }
            else if (significant == 1 && token.Kind == SqlTokenKind.NumberLiteral && int.TryParse(text, out var count) && count > 0)
            {
                repeat = count;
                significant++;
            }
            else
            {
                return false;
            }
        }

        return significant > 0;
    }
}
