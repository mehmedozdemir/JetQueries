using JetQueries.Sdk;

namespace JetQueries.Providers.Language;

/// <summary>Where a statement has a body with semicolons of its own that must not end it.</summary>
internal enum StatementBlockRule
{
    /// <summary>PostgreSQL: <c>BEGIN ATOMIC … END</c> in a SQL-standard function body.</summary>
    BeginAtomic,

    /// <summary>SQLite: the <c>BEGIN … END</c> body of <c>CREATE TRIGGER</c>.</summary>
    TriggerBody,
}

/// <summary>
/// Cuts a script into statements at the semicolons that end them, the way psql and the sqlite3 shell do, so
/// that each runs and reports on its own. Semicolons inside strings, comments, quoted names and statement
/// bodies (see <see cref="StatementBlockRule"/>) do not count.
/// </summary>
internal static class SqlStatementSplitter
{
    public static IReadOnlyList<ScriptBatch> Split(string script, List<LanguageToken> tokens, StatementBlockRule rule)
    {
        ArgumentNullException.ThrowIfNull(script);
        var batches = new List<ScriptBatch>();
        var start = -1;
        var depth = 0;
        var isTrigger = false;
        string? first = null;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == SqlTokenKind.Comment)
            {
                continue;
            }

            var text = script.AsSpan(token.Start, token.Length);
            if (start < 0)
            {
                start = token.Start;
                first = text.ToString();
                isTrigger = false;
            }

            if (token.Kind == SqlTokenKind.Punctuation && text is ";" && depth == 0)
            {
                Add(start, token.Start);
                start = -1;
                continue;
            }

            if (token.Kind != SqlTokenKind.Keyword && token.Kind != SqlTokenKind.Identifier)
            {
                continue;
            }

            if (text.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase) && string.Equals(first, "CREATE", StringComparison.OrdinalIgnoreCase))
            {
                isTrigger = true;
            }
            else if (text.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                var opens = rule switch
                {
                    StatementBlockRule.BeginAtomic => NextWord(script, tokens, i).Equals("ATOMIC", StringComparison.OrdinalIgnoreCase),
                    _ => isTrigger && depth == 0,
                };
                depth += opens ? 1 : 0;
            }
            else if (depth > 0 && text.Equals("CASE", StringComparison.OrdinalIgnoreCase))
            {
                depth++;
            }
            else if (depth > 0 && text.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                depth--;
            }
        }

        if (start >= 0)
        {
            Add(start, script.Length);
        }

        return batches;

        void Add(int from, int to)
        {
            var statement = script[from..to].TrimEnd();
            if (statement.Length > 0)
            {
                batches.Add(new ScriptBatch(statement, script.AsSpan(0, from).Count('\n')));
            }
        }
    }

    private static ReadOnlySpan<char> NextWord(string script, List<LanguageToken> tokens, int index)
    {
        for (var i = index + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != SqlTokenKind.Comment)
            {
                return script.AsSpan(tokens[i].Start, tokens[i].Length);
            }
        }

        return [];
    }
}
