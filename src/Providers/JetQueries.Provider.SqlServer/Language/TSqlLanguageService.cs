using JetQueries.Providers.Language;
using JetQueries.Sdk;

namespace JetQueries.Provider.SqlServer.Language;

/// <summary>
/// Language service for T-SQL: colouring and completion. Both work on tokens, not on a parse tree, because the
/// text being typed is almost never a complete statement.
/// </summary>
internal sealed class TSqlLanguageService : ISqlLanguageService, ISqlFormatter, ISignatureHelpProvider
{
    /// <inheritdoc />
    public string Format(string sql, SqlFormatOptions options) => SqlLayout.Format(sql, TSqlLexer.Tokenize, options);

    public SqlLineTokens TokenizeLine(ReadOnlySpan<char> line, int startState)
    {
        var tokens = new List<LanguageToken>();
        var endState = TSqlLexer.TokenizeLine(line, startState, 0, tokens);
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

        // Parsing is CPU work with no awaits in it: keep it off whoever called.
        return Task.Run(() => TSqlDiagnostics.Check(document.Text), cancellationToken);
    }

    public Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(SqlDocument document, int offset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return TSqlCompletion.CompleteAsync(document, offset, cancellationToken);
    }

    public Task<SignatureHelp?> GetSignatureHelpAsync(SqlDocument document, int offset, CancellationToken cancellationToken) =>
        SqlCallHelp.GetSignatureHelpAsync(TSqlDialect.Instance, document, offset, cancellationToken);

    public Task<SqlDefinition?> GetDefinitionAsync(SqlDocument document, int offset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return TSqlCompletion.FindDefinitionAsync(document, offset, cancellationToken);
    }
}

/// <summary>Splits a script on <c>GO</c> lines the way SQL Server's own tools do.</summary>
internal static class TSqlBatchSplitter
{
    public static IReadOnlyList<ScriptBatch> Split(string script) =>
        [.. SplitWithOffsets(script).Select(static b => b.Batch)];

    /// <summary>The batches together with the character offset each starts at in the script.</summary>
    public static List<(ScriptBatch Batch, int StartOffset)> SplitWithOffsets(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var batches = new List<(ScriptBatch, int)>();
        var tokens = new List<LanguageToken>();
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
                Add(batchStart, lineStart, batchLine, repeat);
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

        Add(Math.Min(batchStart, script.Length), script.Length, batchLine, 1);
        return batches;

        void Add(int start, int end, int startLine, int repeatCount)
        {
            var text = script[start..end];
            if (!string.IsNullOrWhiteSpace(text))
            {
                batches.Add((new ScriptBatch(text, startLine, repeatCount), start));
            }
        }
    }

    /// <summary>A line that is only <c>GO</c>, optionally with a count and a trailing comment.</summary>
    private static bool IsSeparator(ReadOnlySpan<char> line, List<LanguageToken> tokens, out int repeat)
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
