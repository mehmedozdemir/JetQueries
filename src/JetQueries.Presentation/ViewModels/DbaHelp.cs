using JetQueries.Presentation.Resources;

namespace JetQueries.Presentation.ViewModels;

/// <summary>
/// An explanation of one thing on a DBA screen, for a developer who has never been a DBA: what it shows, what is
/// good, what to keep an eye on and what is a problem, and what to do. Opened from the small "i" beside it.
/// </summary>
/// <param name="Title">What it is.</param>
/// <param name="What">What it shows, in plain words.</param>
/// <param name="Good">Normal values; null when there is no rule.</param>
/// <param name="Watch">Values worth a look; null when there is no rule.</param>
/// <param name="Bad">Values that mean a problem; null when there is no rule.</param>
/// <param name="Action">What to do about it.</param>
public sealed record DbaHelpTopic(string Title, string What, string? Good, string? Watch, string? Bad, string Action)
{
    /// <summary>The topic has good / watch / problem levels.</summary>
    public bool HasLevels => Good is not null || Watch is not null || Bad is not null;
}

/// <summary>
/// The explanations of the DBA screens. Each topic's texts are resources named <c>Help_&lt;topic&gt;_Title</c>,
/// <c>_What</c>, <c>_Good</c>, <c>_Watch</c>, <c>_Bad</c> and <c>_Action</c> (Turkish and English); every new DBA
/// screen adds its topics here (ADR 0012).
/// </summary>
public static class DbaHelp
{
    /// <summary>Dashboard: CPU tile.</summary>
    public const string DashCpu = "DashCpu";

    /// <summary>Dashboard: memory tile.</summary>
    public const string DashMemory = "DashMemory";

    /// <summary>Dashboard: batches a second.</summary>
    public const string DashBatches = "DashBatches";

    /// <summary>Dashboard: running requests.</summary>
    public const string DashRequests = "DashRequests";

    /// <summary>Dashboard: blocked requests.</summary>
    public const string DashBlocked = "DashBlocked";

    /// <summary>Dashboard: page life expectancy.</summary>
    public const string DashPle = "DashPle";

    /// <summary>Dashboard: I/O latency.</summary>
    public const string DashLatency = "DashLatency";

    /// <summary>Dashboard: CPU chart.</summary>
    public const string DashCpuChart = "DashCpuChart";

    /// <summary>Dashboard: batches chart.</summary>
    public const string DashBatchChart = "DashBatchChart";

    /// <summary>Dashboard: file I/O chart.</summary>
    public const string DashIoChart = "DashIoChart";

    /// <summary>Dashboard: top waits.</summary>
    public const string DashWaits = "DashWaits";

    /// <summary>Dashboard: databases and backups.</summary>
    public const string DashDatabases = "DashDatabases";

    /// <summary>Activity monitor: the sessions and counters.</summary>
    public const string ActSessions = "ActSessions";

    /// <summary>Activity monitor: blocking chains.</summary>
    public const string ActBlocking = "ActBlocking";

    /// <summary>Activity monitor: ending a session.</summary>
    public const string ActKill = "ActKill";

    /// <summary>Activity monitor: wait types.</summary>
    public const string ActWaits = "ActWaits";

    /// <summary>Performance analysis: heavy queries.</summary>
    public const string PerfHeavy = "PerfHeavy";

    /// <summary>Performance analysis: missing indexes.</summary>
    public const string PerfMissing = "PerfMissing";

    /// <summary>Performance analysis: index use.</summary>
    public const string PerfUsage = "PerfUsage";

    /// <summary>Performance analysis: fragmentation.</summary>
    public const string PerfFragmentation = "PerfFragmentation";

    /// <summary>Health check: the screen and its severities.</summary>
    public const string Health = "Health";

    /// <summary>Every topic, for tests: each must have its texts in both languages.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        DashCpu, DashMemory, DashBatches, DashRequests, DashBlocked, DashPle, DashLatency, DashCpuChart, DashBatchChart, DashIoChart,
        DashWaits, DashDatabases, ActSessions, ActBlocking, ActKill, ActWaits, PerfHeavy, PerfMissing, PerfUsage, PerfFragmentation, Health,
    ];

    /// <summary>A topic's explanation in the user's language; null for an unknown topic.</summary>
    public static DbaHelpTopic? Get(string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        var title = Strings.Find("Help_" + topic + "_Title");
        var what = Strings.Find("Help_" + topic + "_What");
        var action = Strings.Find("Help_" + topic + "_Action");
        return title is null || what is null || action is null
            ? null
            : new DbaHelpTopic(title, what, Strings.Find("Help_" + topic + "_Good"), Strings.Find("Help_" + topic + "_Watch"), Strings.Find("Help_" + topic + "_Bad"), action);
    }
}
