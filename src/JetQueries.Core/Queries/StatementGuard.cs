using JetQueries.Core.Sql;
using JetQueries.Sdk;

namespace JetQueries.Core.Queries;

/// <summary>What a piece of SQL would do, as far as its keywords tell.</summary>
/// <param name="WritesData">It contains a statement that can change data, structure or permissions.</param>
/// <param name="DestructiveActions">
/// What should be confirmed on a production connection: <c>DROP</c>, <c>TRUNCATE</c>,
/// <c>UPDATE without WHERE</c>, <c>DELETE without WHERE</c>. Each at most once.
/// </param>
public sealed record StatementRisk(bool WritesData, IReadOnlyList<string> DestructiveActions);

/// <summary>
/// Classifies SQL by its keywords, ignoring strings, comments and quoted names. It errs on the side of caution:
/// anything that might write counts as writing. This is a guard against accidents in the client, not a
/// security boundary — a determined user gets past it with dynamic SQL, and real protection is a database
/// login without write permission.
/// </summary>
public static class StatementGuard
{
    private static readonly HashSet<string> WritingKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "DROP", "TRUNCATE", "ALTER", "CREATE", "EXEC", "EXECUTE", "CALL", "GRANT", "REVOKE", "DENY",

        // SELECT … INTO creates a table.
        "INTO",

        // A block (BEGIN … END, BEGIN TRANSACTION) can call or contain anything; there is no telling from outside.
        "BEGIN",
    };

    // Keywords that begin a new statement when they appear outside parentheses.
    private static readonly HashSet<string> StatementStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "DROP", "TRUNCATE", "ALTER", "CREATE", "EXEC", "EXECUTE", "GO", "COMMIT",
        "ROLLBACK", "DECLARE", "IF", "WHILE", "BEGIN", "END", "PRINT", "USE", "GRANT", "REVOKE", "DENY", "CALL", "RETURN",
    };

    /// <summary>Analyzes the SQL text that is about to be sent.</summary>
    /// <param name="sql">The text; may hold several statements.</param>
    public static StatementRisk Analyze(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var words = Words(sql);
        var writes = false;
        var destructive = new List<string>();
        void Flag(string action)
        {
            if (!destructive.Contains(action))
            {
                destructive.Add(action);
            }
        }

        for (var i = 0; i < words.Count; i++)
        {
            var (text, depth, isKeyword) = words[i];
            if (!isKeyword)
            {
                continue;
            }

            writes |= WritingKeywords.Contains(text);
            if (Is(text, "DROP") || Is(text, "TRUNCATE"))
            {
                Flag(text.ToUpperInvariant());
            }
            else if (Is(text, "UPDATE") || Is(text, "DELETE"))
            {
                // Not statements of their own: SELECT … FOR UPDATE, ON DELETE CASCADE, MERGE … THEN UPDATE,
                // CREATE TRIGGER … AFTER INSERT, UPDATE.
                var previous = i > 0 ? words[i - 1].Text : string.Empty;
                if (Is(previous, "FOR") || Is(previous, "ON") || Is(previous, "THEN") || Is(previous, "AFTER") || Is(previous, "OF") ||
                    Is(previous, "OR") || Is(previous, "INSERT") || Is(previous, "UPDATE") || Is(previous, "DELETE") || previous == ",")
                {
                    continue;
                }

                var hasWhere = false;
                for (var j = i + 1; j < words.Count; j++)
                {
                    var next = words[j];
                    if (next.Depth < depth || (next.Depth == depth && (next.Text == ";" || (next.IsKeyword && StatementStarts.Contains(next.Text)))))
                    {
                        break;
                    }

                    if (next.Depth == depth && next.IsKeyword && Is(next.Text, "WHERE"))
                    {
                        hasWhere = true;
                        break;
                    }
                }

                if (!hasWhere)
                {
                    Flag(text.ToUpperInvariant() + " without WHERE");
                }
            }
        }

        return new StatementRisk(writes, destructive);
    }

    private static bool Is(string text, string keyword) => string.Equals(text, keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>Keywords, plain names, ';' and ',' with their parenthesis depth; strings, comments and quoted names dropped.</summary>
    private static List<(string Text, int Depth, bool IsKeyword)> Words(string sql)
    {
        var words = new List<(string, int, bool)>();
        var state = BasicSqlLanguageService.StateNormal;
        var depth = 0;
        foreach (var line in sql.AsSpan().EnumerateLines())
        {
            var result = BasicSqlLanguageService.Instance.TokenizeLine(line, state);
            state = result.EndState;
            foreach (var token in result.Tokens)
            {
                var text = line.Slice(token.Span.Start, token.Span.Length);
                switch (token.Kind)
                {
                    case SqlTokenKind.Keyword:
                        words.Add((text.ToString(), depth, true));
                        break;
                    case SqlTokenKind.Identifier when text[0] is not ('"' or '['):
                        // EXEC and friends are keywords in the list; other bare words still separate keywords.
                        words.Add((text.ToString(), depth, WritingKeywords.Contains(text.ToString())));
                        break;
                    case SqlTokenKind.Punctuation when text is "(":
                        depth++;
                        break;
                    case SqlTokenKind.Punctuation when text is ")":
                        depth = Math.Max(0, depth - 1);
                        break;
                    case SqlTokenKind.Punctuation when text is ";" or ",":
                        words.Add((text.ToString(), depth, false));
                        break;
                    default:
                        break;
                }
            }
        }

        return words;
    }
}

/// <summary>Thrown when a statement that can change data is sent on a connection marked read-only.</summary>
public sealed class ReadOnlyConnectionException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public ReadOnlyConnectionException()
        : base("This connection is marked read-only; statements that can change data are not sent.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public ReadOnlyConnectionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ReadOnlyConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
