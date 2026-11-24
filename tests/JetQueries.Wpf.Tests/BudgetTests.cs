using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace JetQueries.Wpf.Tests;

/// <summary>
/// The performance budgets of CLAUDE.md, measured on the published application: cold start (window shown) under
/// 1.5 s and idle memory under 200 MB. Set <c>JETQUERIES_BUDGET_EXE</c> to the published JetQueries.exe
/// (<c>scripts/pack.ps1</c> writes artifacts/publish/win-x64) to run them; they are skipped otherwise. The run keeps
/// its data in a folder of its own (<c>JETQUERIES_DATA_DIR</c>), never the user's.
/// </summary>
public sealed partial class BudgetTests
{
    public const string ExeVariable = "JETQUERIES_BUDGET_EXE";

    [GeneratedRegex(@"window shown after (?<ms>-?\d+) ms")]
    private static partial Regex StartupLine();

    [GeneratedRegex(@"before GC: working set (?<ws>[\d.]+) MB, private (?<private>[\d.]+) MB")]
    private static partial Regex MemoryLine();

    [Theory]
    [InlineData("software", 200)]
    [InlineData("hardware", 400)]
    public async Task The_published_application_starts_fast_and_idles_within_its_memory(string rendering, double privateBudgetMb)
    {
        var exe = Environment.GetEnvironmentVariable(ExeVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(exe) || !File.Exists(exe), $"Set {ExeVariable} to the published JetQueries.exe to measure the budgets.");

        var folder = Path.Combine(Path.GetTempPath(), "jetqueries-budget", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var report = Path.Combine(folder, "report.txt");
        var start = new ProcessStartInfo(exe!) { UseShellExecute = false };
        start.Environment["JETQUERIES_DATA_DIR"] = folder;
        start.Environment["JETQUERIES_MEMORY_REPORT"] = report;
        start.Environment["JETQUERIES_MEMORY_REPORT_DELAY"] = "10";
        start.Environment["JETQUERIES_RENDER"] = rendering == "software" ? "software" : string.Empty;
        using var process = Process.Start(start)!;
        try
        {
            for (var i = 0; i < 120 && !File.Exists(report); i++)
            {
                await Task.Delay(500, TestContext.Current.CancellationToken);
            }

            Assert.True(File.Exists(report), "The application wrote no memory report.");
            await Task.Delay(300, TestContext.Current.CancellationToken);
            var text = await File.ReadAllTextAsync(report, TestContext.Current.CancellationToken);
            TestContext.Current.TestOutputHelper?.WriteLine(text);

            var startup = int.Parse(StartupLine().Match(text).Groups["ms"].Value, CultureInfo.InvariantCulture);
            var memory = MemoryLine().Match(text);
            var privateMb = double.Parse(memory.Groups["private"].Value, CultureInfo.InvariantCulture);
            Assert.InRange(startup, 0, 1500);
            Assert.True(privateMb < privateBudgetMb, $"Idle private memory {privateMb} MB with {rendering} rendering (budget {privateBudgetMb} MB).");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(10_000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
    }
}
