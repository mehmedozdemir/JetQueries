using System.Diagnostics;
using System.Globalization;
using System.Text;
using Antlr4.Runtime;
using JetQueries.PlSql.ParseSpike;

// Usage: JetQueries.PlSql.ParseSpike [--corpus <dir>]... [--report <file>]
// Without --corpus, the bundled grammars-v4 example files are used.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var corpusDirectories = new List<string>();
string? reportPath = null;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--corpus")
    {
        corpusDirectories.Add(args[++i]);
    }
    else if (args[i] == "--report")
    {
        reportPath = args[++i];
    }
}

var skipRecovery = args.Contains("--skip-recovery");
var listErrors = args.Contains("--list-errors");

if (corpusDirectories.Count == 0)
{
    corpusDirectories.Add(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Corpus"));
}

var memoryAtStart = GC.GetTotalMemory(forceFullCollection: true);
var report = new StringBuilder();
void Line(string text = "")
{
    Console.WriteLine(text);
    report.AppendLine(text);
}

double HeapGrowthMb() => (GC.GetTotalMemory(forceFullCollection: true) - memoryAtStart) / 1024.0 / 1024.0;

#if DEBUG
Line("WARNING: Debug build. Timings are only meaningful in Release (dotnet run -c Release).");
#endif
Line($"PL/SQL parse spike - ANTLR {typeof(Parser).Assembly.GetName().Version}, .NET {Environment.Version}, {Environment.ProcessorCount} logical cores");
Line();

// ---------------------------------------------------------------------------------------------------------
// 1. Cold start and a large package
// ---------------------------------------------------------------------------------------------------------
var first = PlSqlParsing.Parse("SELECT 1 FROM dual;");
Line("## 1. Cold start and package size");
Line($"first parse in the process (trivial SELECT; loads the ATN): {first.Milliseconds:F0} ms");

foreach (var size in new[] { 1_000, 5_000, 20_000 })
{
    var package = SyntheticPackage.Generate(size);
    var lines = SyntheticPackage.CountLines(package);
    var cold = PlSqlParsing.Parse(package);
    var warm = Enumerable.Range(0, 5).Select(_ => PlSqlParsing.Parse(package).Milliseconds).Min();
    var lex = Enumerable.Range(0, 5).Select(_ => PlSqlParsing.Lex(package).Milliseconds).Min();

    var before = GC.GetTotalMemory(forceFullCollection: true);
    var held = PlSqlParsing.Parse(package);
    var treeBytes = GC.GetTotalMemory(forceFullCollection: true) - before;
    GC.KeepAlive(held);

    Line($"synthetic package, {lines,6:N0} lines, {package.Length / 1024.0,6:F0} KB, {PlSqlParsing.SignificantTokens(held.Tokens).Count,7:N0} tokens: " +
         $"errors {cold.SyntaxErrors}{(cold.FirstError is null ? string.Empty : $" (first: {cold.FirstError})")}, " +
         $"first parse {cold.Milliseconds,6:F0} ms, warm parse {warm,6:F1} ms ({lines / warm * 1000:N0} lines/s), " +
         $"lex only {lex,5:F1} ms, tree + tokens {treeBytes / 1024.0 / 1024.0:F1} MB, full LL needed: {cold.UsedFullLl}");
}

Line($"managed heap growth so far (prediction cache after one kind of package): {HeapGrowthMb():F0} MB");
Line();

// ---------------------------------------------------------------------------------------------------------
// 2. Corpus of valid files
// ---------------------------------------------------------------------------------------------------------
var files = corpusDirectories
    .Where(Directory.Exists)
    .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
    .Where(static f => f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".pks", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".pkb", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".pls", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".plb", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".prc", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".fnc", StringComparison.OrdinalIgnoreCase))
    .Order(StringComparer.Ordinal)
    .Select(f => (Path: f, Text: File.ReadAllText(f)))
    .ToList();

Line("## 2. Corpus of valid files");
Line($"corpus: {string.Join(", ", corpusDirectories.Select(Path.GetFullPath))}");

var clean = new List<(string Path, string Text)>();
if (files.Count == 0)
{
    Line("no files found");
}
else
{
    var coldTotal = Stopwatch.GetTimestamp();
    var firstPass = files.Select(f => (f.Path, f.Text, Outcome: PlSqlParsing.Parse(f.Text))).ToList();
    var coldMs = Stopwatch.GetElapsedTime(coldTotal).TotalMilliseconds;

    // Warm: best of three per file, the state an editor session is in after the first few parses.
    var warmMs = files.Select(f => Enumerable.Range(0, 3).Select(_ => PlSqlParsing.Parse(f.Text).Milliseconds).Min()).ToArray();
    var lexMs = files.Select(f => Enumerable.Range(0, 3).Select(_ => PlSqlParsing.Lex(f.Text).Milliseconds).Min()).Sum();

    var failed = firstPass.Where(static f => f.Outcome.SyntaxErrors > 0).ToList();
    clean = firstPass.Where(static f => f.Outcome.SyntaxErrors == 0).Select(static f => (f.Path, f.Text)).ToList();
    var sllFalseErrors = clean.Where(static f => PlSqlParsing.Parse(f.Text, ParseMode.SllWithRecovery).SyntaxErrors > 0).Select(static f => Path.GetFileName(f.Path)).ToList();
    Line($"valid files that SLL-with-recovery alone reports errors in (false positives): {sllFalseErrors.Count}/{clean.Count}{(sllFalseErrors.Count is > 0 and <= 12 ? " (" + string.Join(", ", sllFalseErrors) + ")" : string.Empty)}");
    var totalLines = files.Sum(static f => SyntheticPackage.CountLines(f.Text));
    var largest = files.Zip(warmMs).MaxBy(static p => p.First.Text.Length);

    Line($"files: {files.Count}, lines: {totalLines:N0}, largest file: {SyntheticPackage.CountLines(largest.First.Text):N0} lines ({Path.GetFileName(largest.First.Path)}, warm {largest.Second:F1} ms)");
    Line($"parsed without syntax errors: {clean.Count}/{files.Count} ({100.0 * clean.Count / files.Count:F1} %)");
    Line($"needed the full-LL second stage: {firstPass.Count(static f => f.Outcome.UsedFullLl)} files");
    Line($"first pass over the corpus (cold prediction cache): {coldMs:F0} ms");
    Line($"warm: total {warmMs.Sum():F0} ms ({totalLines / warmMs.Sum() * 1000:N0} lines/s), per file p50 {Stats.Percentile(warmMs, 0.50):F2} / p95 {Stats.Percentile(warmMs, 0.95):F2} / max {warmMs.Max():F1} ms; lex only: {lexMs:F0} ms");
    Line("slowest files on the cold first pass: " + string.Join(", ", firstPass.OrderByDescending(static f => f.Outcome.Milliseconds).Take(5).Select(static f => $"{Path.GetFileName(f.Path)} {f.Outcome.Milliseconds:F0} ms")));
    Line("slowest files warm: " + string.Join(", ", files.Zip(warmMs).OrderByDescending(static p => p.Second).Take(5).Select(static p => $"{Path.GetFileName(p.First.Path)} {p.Second:F1} ms")));
    foreach (var f in failed.Take(40))
    {
        Line($"  FAILED {Path.GetRelativePath(corpusDirectories[0], f.Path)}: {f.Outcome.SyntaxErrors} errors, first at {f.Outcome.FirstError}");
        foreach (var error in listErrors ? f.Outcome.Errors : [])
        {
            Line($"      {error}");
        }
    }
}

Line($"managed heap growth so far (prediction cache after the whole corpus): {HeapGrowthMb():F0} MB");
Line();

// ---------------------------------------------------------------------------------------------------------
// 3. Error recovery on broken input
// ---------------------------------------------------------------------------------------------------------
Line("## 3. Error recovery");
Line("survival = share of constructs the intact parse found that the broken parse still finds at the same place.");
Line("path match = the last token before the caret gets the same chain of grammar rules as in the intact parse.");
Line("clause / tables = for carets inside a query: is the clause (select list, FROM, WHERE, ...) and the set of tables");
Line("             in scope the same as in the intact parse? Compared for the broken parse tree and for the token-based fallback.");

var package1K = SyntheticPackage.Generate(1_000);
var packageSubject = PlSqlParsing.Parse(package1K).SyntaxErrors == 0 ? [("synthetic-1000.pkb", package1K)] : new List<(string, string)>();
if (packageSubject.Count == 0)
{
    Line("(synthetic package does not parse cleanly; package experiments skipped)");
}

if (!skipRecovery)
{
    RecoveryExperiment.Run("synthetic 1,000-line package body", packageSubject, cutsPerFile: 150, Line);
    RecoveryExperiment.Run("corpus files that parse cleanly (>= 40 tokens)", clean, cutsPerFile: 3, Line);
}

Line();
Line("## 4. Memory");
Line($"managed heap growth after all runs (nothing but the parser's static prediction caches is still referenced): {HeapGrowthMb():F0} MB; peak working set: {Process.GetCurrentProcess().PeakWorkingSet64 / 1024.0 / 1024.0:F0} MB");

if (reportPath is not null)
{
    File.WriteAllText(reportPath, report.ToString());
}

return 0;
