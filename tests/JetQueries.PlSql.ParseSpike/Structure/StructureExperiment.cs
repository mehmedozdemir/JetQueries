using System.Diagnostics;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using JetQueries.Provider.Oracle.Language;

namespace JetQueries.PlSql.ParseSpike.Structure;

/// <summary>
/// Measures the hand-written structural parser on the same corpora as the ANTLR grammar: false errors on valid
/// files, speed, memory, agreement with the ANTLR parse tree where ANTLR succeeds, and behaviour on damaged text.
/// </summary>
internal static class StructureExperiment
{
    private const int Seed = 20261117;

    private static readonly HashSet<PlNodeKind> StatementKinds =
        [PlNodeKind.Block, PlNodeKind.If, PlNodeKind.Loop, PlNodeKind.Case, PlNodeKind.Sql, PlNodeKind.Statement];

    private static readonly HashSet<PlNodeKind> DeclarationKinds =
        [PlNodeKind.Variable, PlNodeKind.Constant, PlNodeKind.ExceptionDeclaration, PlNodeKind.TypeDeclaration, PlNodeKind.Subtype, PlNodeKind.Cursor];

    private static readonly HashSet<int> AntlrRoutineRules =
    [
        PlSqlParser.RULE_procedure_spec, PlSqlParser.RULE_function_spec, PlSqlParser.RULE_procedure_body, PlSqlParser.RULE_function_body,
        PlSqlParser.RULE_create_procedure_body, PlSqlParser.RULE_create_function_body, PlSqlParser.RULE_proc_decl_in_type,
        PlSqlParser.RULE_func_decl_in_type, PlSqlParser.RULE_constructor_declaration, PlSqlParser.RULE_type_procedure_spec,
        PlSqlParser.RULE_type_function_spec, PlSqlParser.RULE_constructor_spec, PlSqlParser.RULE_overriding_function_spec,
        PlSqlParser.RULE_overriding_procedure_spec,
    ];

    public static void Run(IReadOnlyList<(string Path, string Text)> files, string root, bool compareWithAntlr, bool listErrors, Action<string> line)
    {
        var heapAtStart = GC.GetTotalMemory(forceFullCollection: true);
        line("# Hand-written structural parser");
        line(string.Empty);

        // -------------------------------------------------------------------------------------------------
        line("## S1. Package size");
        var first = Time(() => PlStructureParser.Parse("BEGIN NULL; END;"));
        line($"first parse in the process: {first.Milliseconds:F1} ms");
        foreach (var size in new[] { 1_000, 5_000, 20_000 })
        {
            var package = SyntheticPackage.Generate(size);
            var cold = Time(() => PlStructureParser.Parse(package));
            var warm = Enumerable.Range(0, 10).Select(_ => Time(() => PlStructureParser.Parse(package)).Milliseconds).Min();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var held = PlStructureParser.Parse(package);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var lines = SyntheticPackage.CountLines(package);
            line($"synthetic package, {lines,6:N0} lines: diagnostics {held.Diagnostics.Count}, first parse {cold.Milliseconds,6:F1} ms, " +
                 $"warm {warm,6:F2} ms ({lines / warm * 1000:N0} lines/s), {held.TokenCount:N0} tokens, {held.Root.Descendants().Count():N0} nodes, " +
                 $"allocated per parse {allocated / 1024.0 / 1024.0:F2} MB");
        }

        line(string.Empty);

        // -------------------------------------------------------------------------------------------------
        line("## S2. Corpus");
        var coldWatch = Stopwatch.GetTimestamp();
        var parsed = files.Select(f => (f.Path, f.Text, Script: PlStructureParser.Parse(f.Text))).ToList();
        var coldMs = Stopwatch.GetElapsedTime(coldWatch).TotalMilliseconds;
        var warmMs = files.Select(f => Enumerable.Range(0, 3).Select(_ => Time(() => PlStructureParser.Parse(f.Text)).Milliseconds).Min()).ToArray();
        var totalLines = files.Sum(static f => SyntheticPackage.CountLines(f.Text));
        var clean = parsed.Where(static f => f.Script.Diagnostics.Count == 0).ToList();

        line($"files: {files.Count}, lines: {totalLines:N0}");
        line($"no diagnostics: {clean.Count}/{files.Count} ({100.0 * clean.Count / Math.Max(1, files.Count):F1} %)");
        foreach (var group in parsed.GroupBy(static f => Path.GetExtension(f.Path).ToLowerInvariant()).OrderByDescending(static g => g.Count()))
        {
            var ok = group.Count(static f => f.Script.Diagnostics.Count == 0);
            line($"  {group.Key,-5} {ok,4}/{group.Count(),-4} files ({100.0 * ok / group.Count(),5:F1} %)");
        }

        var all = parsed.SelectMany(static f => f.Script.Root.Descendants()).ToList();
        line("found: " + string.Join(", ", all.GroupBy(static n => n.Kind).OrderByDescending(static g => g.Count()).Select(static g => $"{g.Key} {g.Count():N0}")));
        line($"first pass: {coldMs:F0} ms; warm: total {warmMs.Sum():F0} ms ({totalLines / warmMs.Sum() * 1000:N0} lines/s), " +
             $"per file p50 {Stats.Percentile(warmMs, 0.5):F3} / p95 {Stats.Percentile(warmMs, 0.95):F2} / max {warmMs.Max():F2} ms");
        line("slowest files warm: " + string.Join(", ", files.Zip(warmMs).OrderByDescending(static p => p.Second).Take(5)
            .Select(static p => $"{Path.GetFileName(p.First.Path)} ({SyntheticPackage.CountLines(p.First.Text):N0} lines) {p.Second:F2} ms")));
        foreach (var f in parsed.Where(static f => f.Script.Diagnostics.Count > 0).Take(listErrors ? int.MaxValue : 40))
        {
            line($"  DIAGNOSTICS {Path.GetRelativePath(root, f.Path)}: {f.Script.Diagnostics.Count}, first at {Describe(f.Text, f.Script.Diagnostics[0])}");
            foreach (var diagnostic in listErrors ? f.Script.Diagnostics.Skip(1).Take(3) : [])
            {
                line($"      {Describe(f.Text, diagnostic)}");
            }
        }

        all.Clear();
        line(string.Empty);

        // -------------------------------------------------------------------------------------------------
        if (compareWithAntlr)
        {
            CompareWithAntlr(parsed, root, listErrors, line);
        }

        // -------------------------------------------------------------------------------------------------
        Recovery(clean.Select(static f => (f.Path, f.Text, f.Script)).ToList(), line);

        parsed.Clear();
        clean.Clear();
        line(string.Empty);
        line("## S5. Memory");
        line($"managed heap growth after all runs: {(GC.GetTotalMemory(forceFullCollection: true) - heapAtStart) / 1024.0 / 1024.0:F1} MB; " +
             $"peak working set: {Process.GetCurrentProcess().PeakWorkingSet64 / 1024.0 / 1024.0:F0} MB");
    }

    private static void CompareWithAntlr(List<(string Path, string Text, PlScript Script)> parsed, string root, bool listErrors, Action<string> line)
    {
        line("## S3. Agreement with the ANTLR parse tree (files ANTLR parses without errors)");
        int compared = 0, antlrFailed = 0, antlrFailedClean = 0, skipped = 0;
        int antlrRoutines = 0, ownRoutines = 0, sameRoutines = 0, antlrStatements = 0, ownStatements = 0, sameStatements = 0, identicalFiles = 0;
        var shown = 0;
        foreach (var (path, text, script) in parsed)
        {
            // ANTLR counts code points, the hand-written lexer UTF-16 units; offsets only line up without surrogates.
            if (text.AsSpan().IndexOfAnyInRange('\uD800', '\uDFFF') >= 0)
            {
                skipped++;
                continue;
            }

            var antlr = PlSqlParsing.Parse(text);
            if (antlr.SyntaxErrors > 0)
            {
                antlrFailed++;
                antlrFailedClean += script.Diagnostics.Count == 0 ? 1 : 0;
                continue;
            }

            compared++;
            var expectedRoutines = new HashSet<(int Keyword, string Name, bool Body)>();
            var expectedStatements = new HashSet<int>();
            Collect(antlr.Tree, expectedRoutines, expectedStatements);

            var nodes = script.Root.Descendants().ToList();
            var routines = nodes.Where(static n => n.Kind is PlNodeKind.Procedure or PlNodeKind.Function)
                .Select(static n => (n.KeywordStart, n.Name ?? string.Empty, n.HasBody)).ToHashSet();
            var statements = nodes.Where(static n => StatementKinds.Contains(n.Kind)).Select(static n => n.Start).ToHashSet();

            antlrRoutines += expectedRoutines.Count;
            ownRoutines += routines.Count;
            var commonRoutines = routines.Count(expectedRoutines.Contains);
            sameRoutines += commonRoutines;
            antlrStatements += expectedStatements.Count;
            ownStatements += statements.Count;
            var commonStatements = statements.Count(expectedStatements.Contains);
            sameStatements += commonStatements;

            var identical = commonRoutines == routines.Count && commonRoutines == expectedRoutines.Count &&
                            commonStatements == statements.Count && commonStatements == expectedStatements.Count;
            identicalFiles += identical ? 1 : 0;
            if (!identical && (listErrors || shown < 25))
            {
                shown++;
                var missing = expectedRoutines.Except(routines).Select(r => $"-{r.Item2}@{LineOf(text, r.Item1)}{(r.Item3 ? "(body)" : string.Empty)}")
                    .Concat(routines.Except(expectedRoutines).Select(r => $"+{r.Item2}@{LineOf(text, r.Item1)}{(r.Item3 ? "(body)" : string.Empty)}"))
                    .Concat(expectedStatements.Except(statements).Order().Select(s => $"-stmt@{LineOf(text, s)}"))
                    .Concat(statements.Except(expectedStatements).Order().Select(s => $"+stmt@{LineOf(text, s)}"))
                    .Take(6);
                line($"  DIFFERS {Path.GetRelativePath(root, path)}: {string.Join(" ", missing)}");
            }
        }

        line($"compared: {compared} files; ANTLR failed on {antlrFailed} (the hand-written parser reports no diagnostics in {antlrFailedClean} of those); skipped {skipped}");
        line($"routines: ANTLR {antlrRoutines:N0}, hand-written {ownRoutines:N0}, identical keyword offset + name + spec/body {sameRoutines:N0} " +
             $"({100.0 * sameRoutines / Math.Max(1, antlrRoutines):F2} % of ANTLR's)");
        line($"statements: ANTLR {antlrStatements:N0}, hand-written {ownStatements:N0}, identical start offset {sameStatements:N0} " +
             $"({100.0 * sameStatements / Math.Max(1, antlrStatements):F2} % of ANTLR's)");
        line($"files with exactly the same routines and statements: {identicalFiles}/{compared}");
        line("(- only ANTLR has it, + only the hand-written parser has it; numbers are line numbers)");
        line(string.Empty);
    }

    private static void Collect(IParseTree node, HashSet<(int Keyword, string Name, bool Body)> routines, HashSet<int> statements)
    {
        if (node is not ParserRuleContext context)
        {
            return;
        }

        if (context.RuleIndex == PlSqlParser.RULE_statement && context.Start is not null)
        {
            statements.Add(context.Start.StartIndex);
        }
        else if (AntlrRoutineRules.Contains(context.RuleIndex))
        {
            var keyword = -1;
            string? name = null;
            var body = false;
            for (var i = 0; i < context.ChildCount; i++)
            {
                var child = context.GetChild(i);
                if (child is ITerminalNode terminal)
                {
                    if (keyword < 0 && terminal.Symbol.Type is PlSqlParser.PROCEDURE or PlSqlParser.FUNCTION)
                    {
                        keyword = terminal.Symbol.StartIndex;
                    }
                }
                else if (keyword >= 0 && name is null)
                {
                    name = child.GetText();
                }
                else if (child is PlSqlParser.BodyContext)
                {
                    body = true;
                }
            }

            if (keyword >= 0)
            {
                routines.Add((keyword, name ?? string.Empty, body));
            }
        }

        for (var i = 0; i < context.ChildCount; i++)
        {
            Collect(context.GetChild(i), routines, statements);
        }
    }

    private static void Recovery(List<(string Path, string Text, PlScript Script)> subjects, Action<string> line)
    {
        line("## S4. Damaged text");
        line("survival = share of routines, declarations and statements of the intact parse that the damaged parse still");
        line("finds with the same kind, start and end. enclosing routine = the routine around the caret is still found.");
        var random = new Random(Seed);
        var truncation = new Tally();
        var deletion = new Tally();
        var used = 0;
        foreach (var (_, text, script) in subjects)
        {
            var tokens = Tokens(text);
            if (tokens.Count < 40)
            {
                continue;
            }

            used++;
            var nodes = script.Root.Descendants().ToList();
            var constructs = nodes
                .Where(static n => StatementKinds.Contains(n.Kind) || DeclarationKinds.Contains(n.Kind) || (n.Kind is PlNodeKind.Procedure or PlNodeKind.Function && n.HasBody))
                .Select(static n => (n.Kind, n.Start, n.End)).ToList();
            var routines = nodes.Where(static n => n.Kind is PlNodeKind.Procedure or PlNodeKind.Function && n.HasBody).ToList();

            for (var i = 0; i < 3; i++)
            {
                var last = tokens[random.Next(tokens.Count / 10, tokens.Count * 9 / 10)];
                var broken = Time(() => PlStructureParser.Parse(text[..last.End]));
                var found = Keys(broken.Value);
                truncation.Add(broken.Milliseconds, broken.Value.Diagnostics.Count, constructs.Where(c => c.End <= last.Start), null, found);

                var enclosing = routines.Where(r => r.Start <= last.Start && r.End >= last.End).MinBy(static r => r.End - r.Start);
                if (enclosing is not null)
                {
                    // Same routine, and the parser has it open up to the caret.
                    truncation.AddEnclosing(broken.Value.Root.Descendants().Any(n =>
                        n.KeywordStart == enclosing.KeywordStart && n.End == last.End));
                }
            }

            var candidates = tokens.Where(static t => t.Keyword == PlKeyword.End || t.Kind == PlTokenKind.Semicolon).ToList();
            for (var i = 0; i < 3 && candidates.Count > 2; i++)
            {
                var victim = candidates[random.Next(candidates.Count)];
                var damaged = string.Concat(text.AsSpan(0, victim.Start), new string(' ', victim.Length), text.AsSpan(victim.End));
                var broken = Time(() => PlStructureParser.Parse(damaged));
                deletion.Add(
                    broken.Milliseconds,
                    broken.Value.Diagnostics.Count,
                    constructs.Where(c => c.End <= victim.Start),
                    constructs.Where(c => c.Start >= victim.End),
                    Keys(broken.Value));
            }
        }

        // Arbitrary damage: the parser must return, quickly, whatever it is given.
        var fuzzMs = new List<double>();
        foreach (var (_, text, _) in subjects)
        {
            for (var i = 0; i < 4 && text.Length > 50; i++)
            {
                var at = random.Next(text.Length - 40);
                var damaged = (i % 2) switch
                {
                    0 => text[..at],
                    _ => text.Remove(at, random.Next(1, 40)),
                };
                fuzzMs.Add(Time(() => PlStructureParser.Parse(damaged)).Milliseconds);
            }
        }

        line($"subjects: {used} files without diagnostics and at least 40 tokens");
        line($"random cuts and deletions at character level: {fuzzMs.Count:N0} parses, all returned; max {fuzzMs.Max():F2} ms");
        line($"text ends at the caret ({truncation.Samples} samples):");
        truncation.Report(line);
        line($"one END or ';' removed ({deletion.Samples} samples):");
        deletion.Report(line);
    }

    private static HashSet<(PlNodeKind Kind, int Start, int End)> Keys(PlScript script) =>
        script.Root.Descendants().Select(static n => (n.Kind, n.Start, n.End)).ToHashSet();

    private static List<PlToken> Tokens(string text)
    {
        var lexer = new PlLexer(text);
        var tokens = new List<PlToken>();
        for (var token = lexer.Next(); token.Kind != PlTokenKind.Eof; token = lexer.Next())
        {
            tokens.Add(token);
        }

        return tokens;
    }

    private static (T Value, double Milliseconds) Time<T>(Func<T> action)
    {
        var started = Stopwatch.GetTimestamp();
        var value = action();
        return (value, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static int LineOf(string text, int offset) => text.AsSpan(0, Math.Clamp(offset, 0, text.Length)).Count('\n') + 1;

    private static string Describe(string text, PlDiagnostic diagnostic)
    {
        var offset = Math.Clamp(diagnostic.Offset, 0, text.Length);
        var lineStart = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
        return $"{LineOf(text, offset)}:{offset - lineStart} {diagnostic.Message}";
    }

    private sealed class Tally
    {
        private readonly List<double> _milliseconds = [];
        private readonly List<double> _diagnostics = [];
        private int _beforeFound;
        private int _beforeTotal;
        private int _afterFound;
        private int _afterTotal;
        private int _enclosingFound;
        private int _enclosingTotal;

        public int Samples => _milliseconds.Count;

        public void Add(
            double milliseconds,
            int diagnostics,
            IEnumerable<(PlNodeKind Kind, int Start, int End)> before,
            IEnumerable<(PlNodeKind Kind, int Start, int End)>? after,
            HashSet<(PlNodeKind Kind, int Start, int End)> found)
        {
            _milliseconds.Add(milliseconds);
            _diagnostics.Add(diagnostics);
            foreach (var construct in before)
            {
                _beforeTotal++;
                _beforeFound += found.Contains(construct) ? 1 : 0;
            }

            foreach (var construct in after ?? [])
            {
                _afterTotal++;
                _afterFound += found.Contains(construct) ? 1 : 0;
            }
        }

        public void AddEnclosing(bool found)
        {
            _enclosingTotal++;
            _enclosingFound += found ? 1 : 0;
        }

        public void Report(Action<string> line)
        {
            if (Samples == 0)
            {
                line("  no samples");
                return;
            }

            line($"  parse time p50 {Stats.Percentile(_milliseconds, 0.5):F2} / p95 {Stats.Percentile(_milliseconds, 0.95):F2} / max {_milliseconds.Max():F2} ms; " +
                 $"diagnostics p50 {Stats.Percentile(_diagnostics, 0.5):F0} / p95 {Stats.Percentile(_diagnostics, 0.95):F0} / max {_diagnostics.Max():F0}");
            line($"  survival before the damage: {100.0 * _beforeFound / Math.Max(1, _beforeTotal):F1} % of {_beforeTotal:N0}");
            if (_afterTotal > 0)
            {
                line($"  survival after the damage:  {100.0 * _afterFound / _afterTotal:F1} % of {_afterTotal:N0}");
            }

            if (_enclosingTotal > 0)
            {
                line($"  enclosing routine found: {100.0 * _enclosingFound / _enclosingTotal:F1} % of {_enclosingTotal:N0}");
            }
        }
    }
}
