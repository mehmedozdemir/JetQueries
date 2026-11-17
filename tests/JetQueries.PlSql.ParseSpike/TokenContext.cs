using Antlr4.Runtime;

namespace JetQueries.PlSql.ParseSpike;

/// <summary>Which part of a query the caret is in.</summary>
internal enum QueryClause
{
    None,
    SelectList,
    Into,
    From,
    Where,
    GroupBy,
    OrderBy,
}

/// <summary>
/// The token-based fallback named in the risk table of ANALIZ.md: answers the two questions completion asks most
/// — "which clause is the caret in?" and "which tables are in scope?" — from the token stream alone, without a
/// parse tree. It only needs the lexer, so it costs microseconds and cannot be confused by broken syntax.
/// </summary>
internal static class TokenContext
{
    public static (QueryClause Clause, HashSet<string> Tables) Analyze(IReadOnlyList<IToken> tokens, int lastIndex)
    {
        // Walk back to the start of the innermost query the caret is in: stop at the parenthesis that opened it
        // or at a statement boundary. Tokens inside nested, already closed parentheses are skipped.
        var scope = new List<IToken>();
        var depth = 0;
        for (var i = lastIndex; i >= 0; i--)
        {
            var type = tokens[i].Type;
            if (type == PlSqlParser.RIGHT_PAREN)
            {
                depth++;
            }
            else if (type == PlSqlParser.LEFT_PAREN)
            {
                if (depth > 0)
                {
                    depth--;
                }
                else if (i < lastIndex && tokens[i + 1].Type is PlSqlParser.SELECT or PlSqlParser.WITH)
                {
                    break; // The parenthesis opens the subquery the caret is in.
                }

                // Otherwise it is an unclosed expression or argument list: keep looking outwards.
            }
            else if (depth == 0)
            {
                if (type == PlSqlParser.SEMICOLON)
                {
                    break;
                }

                scope.Add(tokens[i]);
            }
        }

        scope.Reverse();

        // The last SELECT at this level starts the current query block (earlier ones belong to UNION branches
        // or to the statement that contains the query).
        var select = scope.FindLastIndex(static t => t.Type == PlSqlParser.SELECT);
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (select < 0)
        {
            return (QueryClause.None, tables);
        }

        var clause = QueryClause.SelectList;
        var expectTable = false;
        for (var i = select + 1; i < scope.Count; i++)
        {
            var type = scope[i].Type;
            switch (type)
            {
                case PlSqlParser.INTO or PlSqlParser.BULK when clause == QueryClause.SelectList:
                    clause = QueryClause.Into;
                    break;
                case PlSqlParser.FROM:
                    clause = QueryClause.From;
                    expectTable = true;
                    break;
                case PlSqlParser.JOIN when clause == QueryClause.From:
                    expectTable = true;
                    break;
                case PlSqlParser.COMMA when clause == QueryClause.From:
                    expectTable = true;
                    break;
                case PlSqlParser.WHERE:
                    clause = QueryClause.Where;
                    break;
                case PlSqlParser.GROUP or PlSqlParser.HAVING:
                    clause = QueryClause.GroupBy;
                    break;
                case PlSqlParser.ORDER:
                    clause = QueryClause.OrderBy;
                    break;
                default:
                    if (expectTable && clause == QueryClause.From && IsNamePart(scope[i]))
                    {
                        // schema.table: keep extending while the tokens alternate name '.' name.
                        var name = scope[i].Text;
                        while (i + 2 < scope.Count && scope[i + 1].Type == PlSqlParser.PERIOD && IsNamePart(scope[i + 2]))
                        {
                            name += "." + scope[i + 2].Text;
                            i += 2;
                        }

                        tables.Add(name);
                    }

                    expectTable = false;
                    break;
            }
        }

        return (clause, tables);
    }

    private static bool IsNamePart(IToken token) =>
        token.Text.Length > 0 && (char.IsLetter(token.Text[0]) || token.Text[0] is '_' or '"');
}
