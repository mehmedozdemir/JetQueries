using JetQueries.Sdk;

namespace JetQueries.Providers.Language;

/// <summary>
/// Builds "how many rows would this return" for a single query: <c>SELECT COUNT(*) FROM (query) jq_count</c>.
/// Works on tokens, so strings and comments never confuse it.
/// </summary>
internal static class CountQueryBuilder
{
    /// <param name="sql">The query.</param>
    /// <param name="tokens">Its tokens, comments included.</param>
    /// <param name="withAllowedInside">Whether a query starting with WITH may sit inside a derived table (not in SQL Server).</param>
    /// <param name="orderByNeedsPaging">
    /// Whether a derived table may only have ORDER BY together with OFFSET/FETCH/TOP (SQL Server): a plain trailing
    /// ORDER BY is then left out, which does not change the count.
    /// </param>
    public static string? Build(string sql, List<LanguageToken> tokens, bool withAllowedInside, bool orderByNeedsPaging)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var code = tokens.Where(static t => t.Kind != SqlTokenKind.Comment).ToList();
        if (code.Count == 0)
        {
            return null;
        }

        string Text(LanguageToken token) => sql.Substring(token.Start, token.Length);
        bool Is(LanguageToken token, string word) => Text(token).Equals(word, StringComparison.OrdinalIgnoreCase);

        var first = code[0];
        if (!Is(first, "SELECT") && !(Is(first, "WITH") && withAllowedInside))
        {
            return null;
        }

        // A single statement: a semicolon may only end it; no batch separator either.
        var end = code[^1].End;
        var depth = 0;
        var orderByAt = -1;
        var hasPaging = false;
        for (var i = 0; i < code.Count; i++)
        {
            var token = code[i];
            var text = Text(token);
            if (text == "(")
            {
                depth++;
            }
            else if (text == ")")
            {
                depth--;
            }
            else if (text == ";")
            {
                if (i + 1 < code.Count)
                {
                    return null;
                }

                end = token.Start;
            }
            else if (depth == 0 && token.Kind is SqlTokenKind.Keyword or SqlTokenKind.Identifier)
            {
                if (Is(token, "GO") || Is(token, "INTO"))
                {
                    // A batch separator, or SELECT … INTO (which creates a table): not a plain query.
                    return null;
                }

                if (Is(token, "ORDER") && i + 1 < code.Count && Is(code[i + 1], "BY"))
                {
                    orderByAt = token.Start;
                    hasPaging = false;
                }
                else if (orderByAt >= 0 && (Is(token, "OFFSET") || Is(token, "FETCH")))
                {
                    hasPaging = true;
                }
            }
        }

        var query = sql[first.Start..end].TrimEnd();
        if (orderByNeedsPaging && orderByAt >= 0 && !hasPaging && !HasTop(sql, code))
        {
            query = sql[first.Start..orderByAt].TrimEnd();
        }

        return $"SELECT COUNT(*) FROM (\n{query}\n) jq_count";
    }

    private static bool HasTop(string sql, List<LanguageToken> code) =>
        code.Count > 1 && sql.AsSpan(code[1].Start, code[1].Length).Equals("TOP", StringComparison.OrdinalIgnoreCase);
}
