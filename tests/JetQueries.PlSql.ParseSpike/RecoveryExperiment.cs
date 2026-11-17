using Antlr4.Runtime;

namespace JetQueries.PlSql.ParseSpike;

/// <summary>
/// Breaks valid PL/SQL the way an editor sees it — text that stops at the caret, or a missing <c>END</c> / <c>;</c>
/// — and measures how much of the intact parse the parser (and the token-based fallback) still recovers.
/// </summary>
internal static class RecoveryExperiment
{
    private const int Seed = 20261117;

    // Constructs IntelliSense and navigation depend on.
    private static readonly (string Name, int Rule)[] Constructs =
    [
        ("statement", PlSqlParser.RULE_statement),
        ("declaration", PlSqlParser.RULE_declare_spec),
        ("query block", PlSqlParser.RULE_query_block),
        ("table reference", PlSqlParser.RULE_table_ref_aux),
        ("procedure body", PlSqlParser.RULE_procedure_body),
        ("function body", PlSqlParser.RULE_function_body),
    ];

    private static readonly HashSet<int> ConstructRules = [.. Constructs.Select(static c => c.Rule)];

    public static void Run(string title, IReadOnlyList<(string Path, string Text)> subjects, int cutsPerFile, Action<string> line)
    {
        line(string.Empty);
        line($"### {title}");

        foreach (var mode in new[] { ParseMode.TwoStage, ParseMode.SllWithRecovery })
        {
            // Same seed for both modes: they are judged on exactly the same damaged texts.
            var random = new Random(Seed);
            var truncation = new Tally();
            var deletion = new Tally();
            var intactMs = new List<double>();

            foreach (var (_, text) in subjects)
            {
                var intact = PlSqlParsing.Parse(text);
                var tokens = PlSqlParsing.SignificantTokens(intact.Tokens);
                if (intact.SyntaxErrors > 0 || tokens.Count < 40)
                {
                    continue;
                }

                intactMs.Add(intact.Milliseconds);
                var contexts = PlSqlParsing.Contexts(intact.Tree, ConstructRules);
                var paths = PlSqlParsing.TerminalRulePaths(intact.Tree);
                var blocks = PlSqlParsing.QueryBlocks(intact.Tree);

                for (var i = 0; i < cutsPerFile; i++)
                {
                    // The caret is right after a random token in the middle 80 % of the file.
                    var lastIndex = random.Next(tokens.Count / 10, tokens.Count * 9 / 10);
                    var last = tokens[lastIndex];
                    var broken = PlSqlParsing.Parse(text[..(last.StopIndex + 1)], mode);
                    truncation.Add(broken, contexts.Where(c => c.Stop < last.StartIndex));

                    var intactPath = paths.GetValueOrDefault(last.StartIndex);
                    var brokenPath = PlSqlParsing.TerminalRulePaths(broken.Tree).GetValueOrDefault(last.StartIndex);
                    truncation.AddPath(intactPath, brokenPath);

                    // Only carets inside a SELECT: the fallback is written for queries, and rules such as
                    // where_clause are shared with UPDATE and DELETE.
                    var expectedClause = ClauseOf(intactPath);
                    if (expectedClause != QueryClause.None && intactPath!.Contains(PlSqlParser.RULE_query_block))
                    {
                        var expectedTables = TablesInScope(blocks, last);
                        var (tokenClause, tokenTables) = TokenContext.Analyze(tokens, lastIndex);
                        truncation.AddQueryContext(
                            expectedClause,
                            expectedTables,
                            ClauseOf(brokenPath),
                            TablesInScope(PlSqlParsing.QueryBlocks(broken.Tree), last),
                            tokenClause,
                            tokenTables);
                    }
                }

                // One END or semicolon goes missing somewhere; everything else is intact.
                var candidates = tokens.Where(static t => t.Type is PlSqlParser.END or PlSqlParser.SEMICOLON).ToList();
                for (var i = 0; i < cutsPerFile && candidates.Count > 2; i++)
                {
                    var victim = candidates[random.Next(candidates.Count)];
                    var broken = PlSqlParsing.Parse(
                        string.Concat(text.AsSpan(0, victim.StartIndex), new string(' ', victim.Text.Length), text.AsSpan(victim.StopIndex + 1)),
                        mode);
                    deletion.Add(
                        broken,
                        contexts.Where(c => c.Stop < victim.StartIndex),
                        contexts.Where(c => c.Start > victim.StopIndex));
                }
            }

            if (intactMs.Count == 0)
            {
                line("no subjects");
                return;
            }

            line($"[{mode}] subjects: {intactMs.Count}, intact parse p50 {Stats.Percentile(intactMs, 0.5):F1} ms");
            line($"  text ends at the caret ({truncation.Samples} samples):");
            truncation.Report(line, withAfter: false);
            line($"  one END or ';' removed ({deletion.Samples} samples):");
            deletion.Report(line, withAfter: true);
        }
    }

    /// <summary>The query clause a token is in, read off its rule path (innermost query wins).</summary>
    private static QueryClause ClauseOf(int[]? path)
    {
        if (path is null)
        {
            return QueryClause.None;
        }

        for (var i = path.Length - 1; i >= 0; i--)
        {
            switch (path[i])
            {
                case PlSqlParser.RULE_selected_list or PlSqlParser.RULE_query_block: return QueryClause.SelectList;
                case PlSqlParser.RULE_into_clause: return QueryClause.Into;
                case PlSqlParser.RULE_from_clause: return QueryClause.From;
                case PlSqlParser.RULE_where_clause: return QueryClause.Where;
                case PlSqlParser.RULE_group_by_clause: return QueryClause.GroupBy;
                case PlSqlParser.RULE_order_by_clause: return QueryClause.OrderBy;
                default: break;
            }
        }

        return QueryClause.None;
    }

    /// <summary>Tables of the innermost query block around the caret that were written before the caret.</summary>
    private static HashSet<string> TablesInScope(List<QueryBlockInfo> blocks, IToken caret)
    {
        var innermost = blocks
            .Where(b => b.Start <= caret.StartIndex && b.Stop >= caret.StopIndex)
            .MinBy(static b => (long)b.Stop - b.Start);
        return innermost is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(innermost.Tables.Where(t => t.Stop <= caret.StopIndex).Select(static t => t.Name), StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Tally
    {
        private readonly Dictionary<int, (int Found, int Total)> _before = [];
        private readonly Dictionary<int, (int Found, int Total)> _after = [];
        private readonly List<double> _milliseconds = [];
        private readonly List<double> _errors = [];
        private int _pathSamples;
        private int _pathExact;
        private double _pathPrefixShare;
        private int _querySamples;
        private int _treeClause;
        private int _treeTables;
        private int _tokenClause;
        private int _tokenTables;

        public int Samples => _milliseconds.Count;

        public void Add(
            ParseOutcome broken,
            IEnumerable<(int Rule, int Start, int Stop)> before,
            IEnumerable<(int Rule, int Start, int Stop)>? after = null)
        {
            _milliseconds.Add(broken.Milliseconds);
            _errors.Add(broken.SyntaxErrors);
            var recovered = PlSqlParsing.Contexts(broken.Tree, ConstructRules);
            Count(_before, before, recovered);
            if (after is not null)
            {
                Count(_after, after, recovered);
            }
        }

        public void AddPath(int[]? intact, int[]? broken)
        {
            if (intact is null)
            {
                return;
            }

            _pathSamples++;
            var common = 0;
            if (broken is not null)
            {
                while (common < intact.Length && common < broken.Length && intact[common] == broken[common])
                {
                    common++;
                }

                if (common == intact.Length && broken.Length == intact.Length)
                {
                    _pathExact++;
                }
            }

            _pathPrefixShare += (double)common / intact.Length;
        }

        public void AddQueryContext(
            QueryClause expectedClause,
            HashSet<string> expectedTables,
            QueryClause treeClause,
            HashSet<string> treeTables,
            QueryClause tokenClause,
            HashSet<string> tokenTables)
        {
            _querySamples++;
            _treeClause += treeClause == expectedClause ? 1 : 0;
            _treeTables += treeTables.SetEquals(expectedTables) ? 1 : 0;
            _tokenClause += tokenClause == expectedClause ? 1 : 0;
            _tokenTables += tokenTables.SetEquals(expectedTables) ? 1 : 0;
        }

        public void Report(Action<string> line, bool withAfter)
        {
            line($"    parse time p50 {Stats.Percentile(_milliseconds, 0.5):F1} / p95 {Stats.Percentile(_milliseconds, 0.95):F1} / max {_milliseconds.Max():F1} ms; reported syntax errors p50 {Stats.Percentile(_errors, 0.5):F0} / max {_errors.Max():F0}");
            line($"    survival before the damage: {Describe(_before)}");
            if (withAfter)
            {
                line($"    survival after the damage:  {Describe(_after)}");
            }

            if (_pathSamples > 0)
            {
                line($"    path match at the caret: exact {100.0 * _pathExact / _pathSamples:F0} %, average shared depth {100.0 * _pathPrefixShare / _pathSamples:F0} %");
            }

            if (_querySamples > 0)
            {
                line($"    carets inside a query ({_querySamples}): clause correct - broken tree {100.0 * _treeClause / _querySamples:F0} %, token fallback {100.0 * _tokenClause / _querySamples:F0} %; " +
                     $"tables in scope correct - broken tree {100.0 * _treeTables / _querySamples:F0} %, token fallback {100.0 * _tokenTables / _querySamples:F0} %");
            }
        }

        private static void Count(
            Dictionary<int, (int Found, int Total)> tally,
            IEnumerable<(int Rule, int Start, int Stop)> expected,
            HashSet<(int Rule, int Start, int Stop)> recovered)
        {
            foreach (var context in expected)
            {
                var (found, total) = tally.GetValueOrDefault(context.Rule);
                tally[context.Rule] = (found + (recovered.Contains(context) ? 1 : 0), total + 1);
            }
        }

        private static string Describe(Dictionary<int, (int Found, int Total)> tally)
        {
            var parts = Constructs
                .Where(c => tally.ContainsKey(c.Rule))
                .Select(c => $"{c.Name} {100.0 * tally[c.Rule].Found / tally[c.Rule].Total:F0} % of {tally[c.Rule].Total:N0}");
            var found = tally.Values.Sum(static v => v.Found);
            var total = tally.Values.Sum(static v => v.Total);
            return total == 0 ? "nothing to compare" : $"overall {100.0 * found / total:F0} % ({string.Join("; ", parts)})";
        }
    }
}

internal static class Stats
{
    public static double Percentile(IReadOnlyCollection<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? double.NaN : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}
