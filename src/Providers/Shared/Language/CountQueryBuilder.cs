using JetQueries.Sdk;

namespace JetQueries.Providers.Language;

/// <summary>
/// Wraps a single query: "how many rows would this return" (<c>SELECT COUNT(*) FROM (query) jq_count</c>) and
/// "only the rows matching this condition" (<c>SELECT * FROM (query) jq_filter WHERE (condition)</c>).
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
        if (SingleQuery.Parse(sql, tokens, withAllowedInside) is not { } query)
        {
            return null;
        }

        var inner = orderByNeedsPaging && query.HasPlainOrderBy ? query.WithoutOrderBy : query.Text;
        return $"SELECT COUNT(*) FROM (\n{inner}\n) jq_count";
    }

    /// <summary>
    /// The query keeping only the rows for which <paramref name="condition"/> holds. The condition is written
    /// against the query's columns; it must be one expression (no statement separator, balanced parentheses, no
    /// comments). The query's order is kept: a plain ORDER BY of column names or positions moves outside;
    /// any other stays inside (SQL Server: with <c>OFFSET 0 ROWS</c>, which a derived table needs).
    /// </summary>
    /// <param name="sql">The query.</param>
    /// <param name="tokens">Its tokens, comments included.</param>
    /// <param name="condition">The condition, as typed.</param>
    /// <param name="conditionTokens">Its tokens, comments included.</param>
    /// <param name="withAllowedInside">Whether a query starting with WITH may sit inside a derived table.</param>
    /// <param name="orderByNeedsPaging">Whether a derived table may only have ORDER BY together with OFFSET/FETCH/TOP.</param>
    public static string? BuildFilter(
        string sql, List<LanguageToken> tokens, string condition, List<LanguageToken> conditionTokens, bool withAllowedInside, bool orderByNeedsPaging)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (SingleQuery.Parse(sql, tokens, withAllowedInside) is not { } query || !IsExpression(condition, conditionTokens))
        {
            return null;
        }

        var inner = query.Text;
        var outerOrder = string.Empty;
        if (query.HasPlainOrderBy)
        {
            if (query.OrderByIsSimple)
            {
                inner = query.WithoutOrderBy;
                outerOrder = "\n" + query.OrderBy;
            }
            else if (orderByNeedsPaging)
            {
                inner = query.Text + "\nOFFSET 0 ROWS";
            }
        }

        return $"SELECT * FROM (\n{inner}\n) jq_filter\nWHERE (\n{condition.Trim()}\n){outerOrder}";
    }

    /// <summary>One expression: something to evaluate, no separator, parentheses that close, nothing commented out.</summary>
    private static bool IsExpression(string condition, List<LanguageToken> tokens)
    {
        if (tokens.Count == 0 || tokens.Any(static t => t.Kind == SqlTokenKind.Comment))
        {
            return false;
        }

        var depth = 0;
        foreach (var token in tokens)
        {
            var text = condition.AsSpan(token.Start, token.Length);
            if (text is ";")
            {
                return false;
            }

            if (token.Kind == SqlTokenKind.StringLiteral && !IsClosed(text))
            {
                // A string that never ends would swallow what follows it.
                return false;
            }

            depth += text is "(" ? 1 : text is ")" ? -1 : 0;
            if (depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
    }

    /// <summary>A string literal ends with the quote it opened with (after a prefix such as N or E: N'İ', E'a', $$a$$).</summary>
    private static bool IsClosed(ReadOnlySpan<char> text)
    {
        var open = text.IndexOfAny('\'', '"', '$');
        return open >= 0 && text.Length >= open + 2 && text[^1] == text[open];
    }

    private sealed record SingleQuery(string Text, string WithoutOrderBy, string OrderBy, bool HasPlainOrderBy, bool OrderByIsSimple)
    {
        public static SingleQuery? Parse(string sql, List<LanguageToken> tokens, bool withAllowedInside)
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
            var orderByIndex = -1;
            var hasPaging = false;
            var codeEnd = code.Count;
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
                    codeEnd = i;
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
                        orderByIndex = i;
                        hasPaging = false;
                    }
                    else if (orderByIndex >= 0 && (Is(token, "OFFSET") || Is(token, "FETCH") || Is(token, "LIMIT")))
                    {
                        hasPaging = true;
                    }
                }
            }

            var query = sql[first.Start..end].TrimEnd();
            var hasTop = code.Count > 1 && Is(code[1], "TOP");
            if (orderByIndex < 0 || hasPaging || hasTop)
            {
                return new SingleQuery(query, query, string.Empty, false, false);
            }

            var orderAt = code[orderByIndex].Start;
            var orderBy = sql[orderAt..end].TrimEnd();

            // Simple: column names (one part, quoted or not) or positions, with ASC/DESC/NULLS FIRST/LAST.
            var simple = true;
            for (var i = orderByIndex + 2; i < codeEnd && simple; i++)
            {
                var token = code[i];
                simple = Text(token) == ","
                    || token.Kind == SqlTokenKind.NumberLiteral
                    || (token.Kind is SqlTokenKind.Identifier or SqlTokenKind.Keyword
                        && !Text(token).Contains('.', StringComparison.Ordinal)
                        && (i + 1 >= codeEnd || Text(code[i + 1]) != ".")
                        && (token.Kind == SqlTokenKind.Identifier || Text(token).ToUpperInvariant() is "ASC" or "DESC" or "NULLS" or "FIRST" or "LAST"));
            }

            return new SingleQuery(query, sql[first.Start..orderAt].TrimEnd(), orderBy, true, simple);
        }
    }
}
