using JetQueries.Sdk;

namespace JetQueries.Providers.Language;

/// <summary>A call the caret is in: the routine's dotted name, and which argument the caret is on.</summary>
/// <param name="Parts">The routine's name parts as written (unquoted).</param>
/// <param name="NameStart">Offset of the name's first character.</param>
/// <param name="Argument">Zero-based index of the argument the caret is on.</param>
/// <param name="IsExec">A T-SQL EXEC call (arguments without parentheses).</param>
internal sealed record CallSite(IReadOnlyList<string> Parts, int NameStart, int Argument, bool IsExec);

/// <summary>
/// Signature help and parameter values on the shared engine: which call the caret is in (by counting brackets
/// and commas back from the caret, as the completion engine reads tokens, without parsing), what the dialect's
/// catalog or the database says about that routine, and the values its current parameter takes.
/// </summary>
internal static class SqlCallHelp
{
    private const int MaxLookBack = 4000;

    /// <summary>The call around <paramref name="offset"/>; null outside any call.</summary>
    public static CallSite? FindCall(SqlDialect dialect, string text, List<LanguageToken> tokens, int offset)
    {
        var depth = 0;
        var commas = 0;
        var last = tokens.FindLastIndex(t => t.Start < offset);
        for (var i = last; i >= 0 && last - i < MaxLookBack; i--)
        {
            var token = tokens[i];
            if (token.Kind is SqlTokenKind.Comment)
            {
                continue;
            }

            // Inside a string the caret is still in the argument the string is.
            if (token.Kind == SqlTokenKind.StringLiteral)
            {
                continue;
            }

            var value = text.AsSpan(token.Start, token.Length);
            if (value is ";" || (token.Kind == SqlTokenKind.Keyword && dialect.IsBatchSeparator(value)))
            {
                return null;
            }

            if (value is ")")
            {
                depth++;
            }
            else if (value is "(")
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }

                if (NameBefore(dialect, text, tokens, i) is { } named)
                {
                    return new CallSite(named.Parts, named.Start, commas, IsExec: false);
                }

                // A grouping bracket: the caret is inside one argument of whatever call is around it.
                commas = 0;
            }
            else if (value is "," && depth == 0)
            {
                commas++;
            }
            else if (depth == 0 && token.Kind == SqlTokenKind.Keyword && dialect.ProcedureCallKeywords.Contains(value.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                // EXEC name arg, arg — the name is the dotted name right after the keyword.
                var parts = new List<string>();
                var j = i + 1;
                var start = j < tokens.Count ? tokens[j].Start : offset;
                while (j < tokens.Count && tokens[j].End <= offset && IsName(tokens[j].Kind))
                {
                    parts.Add(dialect.NameOf(text.Substring(tokens[j].Start, tokens[j].Length)));
                    if (j + 1 < tokens.Count && text.AsSpan(tokens[j + 1].Start, tokens[j + 1].Length) is ".")
                    {
                        j += 2;
                    }
                    else
                    {
                        j++;
                        break;
                    }
                }

                // The caret must be past the name (in the arguments), not on it.
                return parts.Count > 0 && tokens[j - 1].End < offset ? new CallSite(parts, start, commas, IsExec: true) : null;
            }
        }

        return null;
    }

    /// <summary>The signatures of the call around the caret, with the argument the caret is on.</summary>
    public static async Task<SignatureHelp?> GetSignatureHelpAsync(SqlDialect dialect, SqlDocument document, int offset, CancellationToken cancellationToken)
    {
        var text = document.Text;
        offset = Math.Clamp(offset, 0, text.Length);
        var tokens = dialect.Tokenize(text);
        if (FindCall(dialect, text, tokens, offset) is not { } call)
        {
            return null;
        }

        var signatures = await SignaturesAsync(dialect, document, call, cancellationToken).ConfigureAwait(false);
        if (signatures.Count == 0)
        {
            return null;
        }

        // The first overload with room for the argument the caret is on.
        var active = signatures.ToList().FindIndex(s => s.IsVariadic || s.Parameters.Count > call.Argument);
        active = active < 0 ? 0 : active;
        var parameter = signatures[active].IsVariadic ? Math.Min(call.Argument, Math.Max(0, signatures[active].Parameters.Count - 1)) : call.Argument;
        return new SignatureHelp(signatures, active, parameter, call.NameStart);
    }

    /// <summary>
    /// The values the parameter under the caret takes (DATEADD's datepart, CONVERT's styles…), as completions
    /// replacing what is typed there; null when the caret is not on such a parameter.
    /// </summary>
    public static IReadOnlyList<CompletionItem>? ValueCompletions(SqlDialect dialect, string text, List<LanguageToken> tokens, int offset)
    {
        if (dialect.Catalog is not { } catalog || FindCall(dialect, text, tokens, offset) is not { IsExec: false } call)
        {
            return null;
        }

        var values = catalog.Find(call.Parts[^1])
            .Select(f => call.Argument < f.Parameters.Count ? f.Parameters[call.Argument] : f.IsVariadic && f.Parameters.Count > 0 ? f.Parameters[^1] : null)
            .FirstOrDefault(static p => p is { Values.Count: > 0 });
        if (values is null)
        {
            return null;
        }

        // What is typed of the argument so far: a word, or a string being written.
        var replace = new TextSpan(offset, 0);
        var typed = string.Empty;
        var at = tokens.FindLastIndex(t => t.Start < offset && t.Kind != SqlTokenKind.Comment);
        if (at >= 0 && tokens[at].End >= offset && tokens[at].Kind is SqlTokenKind.Identifier or SqlTokenKind.Keyword or SqlTokenKind.Function
                or SqlTokenKind.DataType or SqlTokenKind.StringLiteral or SqlTokenKind.NumberLiteral)
        {
            replace = new TextSpan(tokens[at].Start, tokens[at].End - tokens[at].Start);
            typed = text[tokens[at].Start..offset];
        }

        var match = typed.TrimStart('\'');
        return
        [
            .. values.Values
                .Where(v => match.Length == 0 || v.Text.TrimStart('\'').StartsWith(match, StringComparison.OrdinalIgnoreCase))
                .Select(v => new CompletionItem(v.Text, CompletionItemKind.Value, replace) { Detail = v.Description.Text, Documentation = values.Name }),
        ];
    }

    /// <summary>The catalog's overloads for a built-in, or the database's signatures for a user routine.</summary>
    private static async Task<IReadOnlyList<SqlSignature>> SignaturesAsync(SqlDialect dialect, SqlDocument document, CallSite call, CancellationToken cancellationToken)
    {
        if (call.Parts.Count == 1 && !call.IsExec && dialect.Catalog?.Find(call.Parts[0]) is { Count: > 0 } builtIn)
        {
            return [.. builtIn.Select(static f => f.ToSignature())];
        }

        if (document.Metadata is not IRoutineMetadataProvider routines)
        {
            return [];
        }

        var name = dialect.ToName(document, call.Parts);
        var kind = call.IsExec ? DatabaseObjectKinds.Procedure : DatabaseObjectKinds.Function;
        try
        {
            return await routines.GetSignaturesAsync(new DatabaseObject(name, kind), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Signature help is a convenience: a catalog error means no help, never a broken editor.
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }

    private static (List<string> Parts, int Start)? NameBefore(SqlDialect dialect, string text, List<LanguageToken> tokens, int open)
    {
        var parts = new List<string>();
        var i = open - 1;
        var start = -1;
        while (i >= 0 && IsName(tokens[i].Kind))
        {
            parts.Insert(0, dialect.NameOf(text.Substring(tokens[i].Start, tokens[i].Length)));
            start = tokens[i].Start;
            if (i - 1 >= 0 && text.AsSpan(tokens[i - 1].Start, tokens[i - 1].Length) is ".")
            {
                i -= 2;
            }
            else
            {
                break;
            }
        }

        // Words that are followed by a list, not a call: IN (…), VALUES (…), AS (…), table (columns).
        if (parts.Count == 0 || (parts.Count == 1 && NotCalls.Contains(parts[0])))
        {
            return null;
        }

        return (parts, start);
    }

    private static readonly HashSet<string> NotCalls = new(StringComparer.OrdinalIgnoreCase)
    {
        "IN", "VALUES", "AS", "EXISTS", "ON", "AND", "OR", "NOT", "WHERE", "SELECT", "FROM", "JOIN", "WITH", "OVER", "INTO", "TABLE", "USING",
        "BY", "WHEN", "THEN", "ELSE", "RETURN", "RETURNS", "IF", "WHILE", "DECLARE", "BEGIN", "END", "KEY", "CHECK", "UNIQUE", "DEFAULT",
    };

    private static bool IsName(SqlTokenKind kind) =>
        kind is SqlTokenKind.Identifier or SqlTokenKind.Function or SqlTokenKind.Keyword or SqlTokenKind.DataType;
}
