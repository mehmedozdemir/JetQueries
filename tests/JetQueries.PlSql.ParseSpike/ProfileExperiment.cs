using System.Diagnostics;
using Antlr4.Runtime;
using Antlr4.Runtime.Atn;

namespace JetQueries.PlSql.ParseSpike;

/// <summary>
/// Where a warm parse spends its time: ANTLR's own profiler, per grammar decision. A few dominant decisions
/// would mean the grammar can be repaired; cost spread over hundreds would mean it cannot.
/// </summary>
internal static class ProfileExperiment
{
    public static void Run(string path, Action<string> line)
    {
        var text = File.ReadAllText(path);
        var lineCount = text.AsSpan().Count('\n') + 1;

        // Warm the prediction cache the same way the editor would have.
        var cold = PlSqlParsing.Parse(text);
        var warm = Enumerable.Range(0, 3).Select(_ => PlSqlParsing.Parse(text).Milliseconds).Min();
        var sllOnly = Enumerable.Range(0, 3).Select(_ => PlSqlParsing.Parse(text, ParseMode.SllWithRecovery)).MinBy(static o => o.Milliseconds)!;
        var lex = Enumerable.Range(0, 3).Select(_ => PlSqlParsing.Lex(text).Milliseconds).Min();

        line($"## Profile: {Path.GetFileName(path)} ({lineCount:N0} lines)");
        line($"first parse {cold.Milliseconds:F0} ms, warm parse {warm:F0} ms, lex only {lex:F0} ms, " +
             $"syntax errors {cold.SyntaxErrors}, full LL needed: {cold.UsedFullLl}");
        line($"one SLL pass with recovery (warm): {sllOnly.Milliseconds:F0} ms, reporting {sllOnly.SyntaxErrors} errors");

        var lexer = new PlSqlLexer(CharStreams.fromString(text));
        lexer.RemoveErrorListeners();
        var tokens = new CommonTokenStream(lexer);
        tokens.Fill();
        var tokenCount = tokens.GetTokens().Count(static t => t.Channel == TokenConstants.DefaultChannel);

        var parser = new PlSqlParser(tokens);
        parser.RemoveErrorListeners();
        parser.Profile = true;
        parser.Interpreter.PredictionMode = cold.UsedFullLl ? PredictionMode.LL : PredictionMode.SLL;

        var started = Stopwatch.GetTimestamp();
        parser.sql_script();
        var profiled = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        var decisions = parser.ParseInfo.getDecisionInfo();
        var atn = parser.Atn;
        long totalTime = 0, totalInvocations = 0, totalLook = 0, atnTransitions = 0, dfaTransitions = 0, fallbacks = 0, ambiguities = 0;
        foreach (var d in decisions)
        {
            totalTime += d.timeInPrediction;
            totalInvocations += d.invocations;
            totalLook += d.SLL_TotalLook + d.LL_TotalLook;
            atnTransitions += d.SLL_ATNTransitions + d.LL_ATNTransitions;
            dfaTransitions += d.SLL_DFATransitions + d.LL_DFATransitions;
            fallbacks += d.LL_Fallback;
            ambiguities += d.ambiguities.Count;
        }

        line($"profiled parse {profiled:F0} ms; {tokenCount:N0} tokens; {totalInvocations:N0} predictions " +
             $"({(double)totalInvocations / tokenCount:F1} per token); lookahead {totalLook:N0} tokens in total " +
             $"({(double)totalLook / tokenCount:F1} per token)");
        line($"transitions: {dfaTransitions:N0} from the cache (DFA), {atnTransitions:N0} computed (ATN); " +
             $"full-LL fallbacks {fallbacks:N0}; ambiguities {ambiguities:N0}; decisions used {decisions.Count(static d => d.invocations > 0):N0} of {decisions.Length:N0}");

        line("top decisions by time in prediction:");
        line("  share  cumul.  rule                               invocations   max look   total look   ATN trans.  LL fallb.  ambig.");
        double cumulative = 0;
        foreach (var d in decisions.OrderByDescending(static d => d.timeInPrediction).Take(15))
        {
            var share = totalTime == 0 ? 0 : 100.0 * d.timeInPrediction / totalTime;
            cumulative += share;
            var rule = PlSqlParser.ruleNames[atn.GetDecisionState(d.decision).ruleIndex];
            line($"  {share,4:F1}%  {cumulative,5:F1}%  {rule,-34} {d.invocations,11:N0} {Math.Max(d.SLL_MaxLook, d.LL_MaxLook),10:N0} " +
                 $"{d.SLL_TotalLook + d.LL_TotalLook,12:N0} {d.SLL_ATNTransitions + d.LL_ATNTransitions,12:N0} {d.LL_Fallback,10:N0} {d.ambiguities.Count,7:N0}");
        }

        // Where the prediction cache lives: DFA states per decision (shared by all parser instances).
        var dfas = parser.Interpreter.decisionToDFA;
        long totalStates = dfas.Sum(static d => (long)d.states.Count);
        var topStates = dfas.OrderByDescending(static d => d.states.Count).Take(5)
            .Select(d => $"{PlSqlParser.ruleNames[atn.GetDecisionState(d.decision).ruleIndex]} {d.states.Count:N0}");
        line($"prediction cache so far: {totalStates:N0} DFA states; largest: {string.Join(", ", topStates)}");
        line(string.Empty);
    }
}
