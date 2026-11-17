using JetQueries.Sdk;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace JetQueries.Provider.SqlServer.Language;

/// <summary>
/// Syntax errors from Microsoft's own T-SQL parser (ScriptDom), the one SQL Server's tools use. It checks
/// syntax only: a misspelt table or column is not an error here, the server reports that when the text runs.
/// </summary>
internal static class TSqlDiagnostics
{
    private const int MaxErrors = 50;

    // Beyond this the text is a data script, not something being edited; parsing it on every pause is wasteful.
    private const int MaxLength = 2_000_000;

    public static IReadOnlyList<SqlDiagnostic> Check(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
        {
            return [];
        }

        // The newest grammar: it accepts everything older servers accept. Each batch is parsed on its own,
        // split the way the script will be sent: the parser itself rejects "GO 3", which SQL Server's tools allow.
        var parser = new TSql180Parser(initialQuotedIdentifiers: true);
        var errors = new List<(int Offset, string Message)>();
        foreach (var (batch, start) in TSqlBatchSplitter.SplitWithOffsets(text))
        {
            using var reader = new StringReader(batch.Text);
            parser.Parse(reader, out var batchErrors);
            errors.AddRange(batchErrors.Select(e => (start + e.Offset, e.Message)));
            if (errors.Count >= MaxErrors)
            {
                break;
            }
        }

        if (errors.Count == 0)
        {
            return [];
        }

        var tokens = TSqlLexer.Tokenize(text);
        var diagnostics = new List<SqlDiagnostic>(Math.Min(errors.Count, MaxErrors));
        foreach (var error in errors.Take(MaxErrors))
        {
            var offset = Math.Clamp(error.Offset, 0, text.Length);

            // Underline the whole token the parser stopped at; at the very end, the last character.
            var index = tokens.FindIndex(t => t.Start <= offset && offset < t.End);
            var span = index >= 0
                ? new TextSpan(tokens[index].Start, tokens[index].Length)
                : new TextSpan(Math.Max(0, Math.Min(offset, text.Length - 1)), Math.Min(1, text.Length));
            diagnostics.Add(new SqlDiagnostic(span, error.Message, SqlDiagnosticSeverity.Error));
        }

        return diagnostics;
    }
}
