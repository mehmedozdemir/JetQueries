using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;

namespace JetQueries.Wpf.Services;

/// <summary>
/// A diagnostic for the idle-memory budget (CLAUDE.md: under 200 MB): with <c>JETQUERIES_MEMORY_REPORT</c> set to a
/// file path, the application writes, once it has been idle for a while, the process's memory, the managed heap and
/// the loaded assemblies with their sizes. Off unless asked for; nothing leaves the machine.
/// </summary>
internal static class MemoryReport
{
    public const string Variable = "JETQUERIES_MEMORY_REPORT";

    private static TimeSpan? _startup;

    /// <summary>Notes how long the process took to show its window (cold start budget: 1.5 s).</summary>
    public static void WindowShown()
    {
        using var process = Process.GetCurrentProcess();
        _startup = DateTime.Now - process.StartTime;
    }

    /// <summary>Starts the report when the variable asks for it.</summary>
    public static void ScheduleIfAsked(Dispatcher dispatcher)
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } path)
        {
            return;
        }

        var seconds = int.TryParse(Environment.GetEnvironmentVariable(Variable + "_DELAY"), out var delay) ? delay : 20;
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher) { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            File.WriteAllText(path, Build());
        };
        timer.Start();
    }

    public static string Build()
    {
        var before = Snapshot();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var after = Snapshot();
        var info = GC.GetGCMemoryInfo();
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"window shown after {(_startup?.TotalMilliseconds ?? -1):0} ms");
        text.AppendLine(CultureInfo.InvariantCulture, $"rendering {(System.Windows.Media.RenderOptions.ProcessRenderMode == System.Windows.Interop.RenderMode.SoftwareOnly ? "software" : "hardware")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"before GC: {before}");
        text.AppendLine(CultureInfo.InvariantCulture, $"after GC:  {after}");
        text.AppendLine(CultureInfo.InvariantCulture, $"managed heap {Mb(GC.GetTotalMemory(false))} MB, committed {Mb(info.TotalCommittedBytes)} MB, fragmented {Mb(info.FragmentedBytes)} MB, gen2 count {GC.CollectionCount(2)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"server GC {System.Runtime.GCSettings.IsServerGC}, latency {System.Runtime.GCSettings.LatencyMode}");
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Select(static a => (Name: a.GetName().Name ?? "?", Size: a.IsDynamic || string.IsNullOrEmpty(a.Location) ? 0 : new FileInfo(a.Location).Length))
            .OrderByDescending(static a => a.Size)
            .ToList();
        text.AppendLine(CultureInfo.InvariantCulture, $"assemblies: {assemblies.Count}, {Mb(assemblies.Sum(static a => a.Size))} MB on disk");
        foreach (var (name, size) in assemblies.Take(40))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {Mb(size),6} MB  {name}");
        }

        return text.ToString();
    }

    private static string Snapshot()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return string.Create(CultureInfo.InvariantCulture, $"working set {Mb(process.WorkingSet64)} MB, private {Mb(process.PrivateMemorySize64)} MB, managed {Mb(GC.GetTotalMemory(false))} MB");
    }

    private static string Mb(long bytes) => (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture);
}
