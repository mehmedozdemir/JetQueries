using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Connections;
using JetQueries.Core.Jobs;
using JetQueries.Presentation.Resources;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>The parts of the performance analysis.</summary>
public enum PerformanceSection
{
    /// <summary>The statements that cost most.</summary>
    HeavyQueries,

    /// <summary>Indexes the optimizer asked for.</summary>
    MissingIndexes,

    /// <summary>Indexes and their use: unused, duplicate, covered.</summary>
    IndexUsage,

    /// <summary>Fragmented indexes (measured on request).</summary>
    Fragmentation,
}

/// <summary>A row of a performance table: its cells, the text shown beside the grid, and a script to open.</summary>
/// <param name="Cells">One text per column; null for empty.</param>
/// <param name="Detail">The SQL or script shown for the chosen row.</param>
/// <param name="Database">Where a query tab for it opens.</param>
public sealed record PerformanceRow(IReadOnlyList<string?> Cells, string Detail, string? Database)
{
    /// <summary>A script that can be opened (CREATE, DROP, ALTER INDEX); null for a heavy query.</summary>
    public string? Script { get; init; }

    /// <summary>A finding to stand out (unused, duplicate, covered index).</summary>
    public bool IsFinding { get; init; }
}

/// <summary>A table of the analysis: headers, which columns are numbers, rows.</summary>
public sealed record PerformanceTable(IReadOnlyList<string> Headers, IReadOnlyList<bool> Numeric, IReadOnlyList<PerformanceRow> Rows)
{
    /// <summary>No table.</summary>
    public static PerformanceTable Empty { get; } = new([], [], []);
}

/// <summary>
/// The performance analysis of a connection: heavy queries, missing indexes, index use (unused, duplicate, covered)
/// and fragmentation. Each part is read when first shown or asked for, on a connection of its own, through the job
/// queue; fragmentation reads index pages, so only on the user's request. Scripts go to a new query tab, never run
/// (ADR 0010).
/// </summary>
public sealed partial class PerformanceViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>The choice that means every database.</summary>
    public static string AllDatabases => Strings.PerfAllDatabases;

    private readonly ConnectionSession _session;
    private readonly IJobQueue _jobs;
    private readonly HashSet<PerformanceSection> _loaded = [];
    private QueryChannel? _channel;
    private bool _disposed;
    private bool _started;

    // Asked again while a read was running (another part, database or order): read once more when it ends.
    private bool _again;

    public PerformanceViewModel(ConnectionSession session, IJobQueue jobs, string? database = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _jobs = jobs;
        Database = database ?? AllDatabases;
    }

    public Guid ConnectionId => _session.Definition.Id;

    public string ConnectionName => _session.Definition.Name;

    public ConnectionEnvironment Environment => _session.Definition.Environment;

    public string HeaderText => Strings.PerfTitle + " · " + ConnectionName;

    public string ToolTip => Strings.PerfTitle + "\n" + ConnectionName;

    /// <summary>Colours the SQL and scripts.</summary>
    public ISqlLanguageService? LanguageService => _session.Provider.LanguageService;

    /// <summary>"All databases" and the server's databases.</summary>
    public ObservableCollection<string> Databases { get; } = [];

    /// <summary>What heavy queries are ranked by, with their names.</summary>
    public static IReadOnlyList<SettingChoice<HeavyQueryOrder>> Orders { get; } =
    [
        new(HeavyQueryOrder.TotalCpu, Strings.PerfOrderTotalCpu), new(HeavyQueryOrder.AverageCpu, Strings.PerfOrderAverageCpu),
        new(HeavyQueryOrder.TotalDuration, Strings.PerfOrderTotalDuration), new(HeavyQueryOrder.AverageDuration, Strings.PerfOrderAverageDuration),
        new(HeavyQueryOrder.TotalReads, Strings.PerfOrderTotalReads), new(HeavyQueryOrder.AverageReads, Strings.PerfOrderAverageReads),
        new(HeavyQueryOrder.Executions, Strings.PerfOrderExecutions),
    ];

    public static IReadOnlyList<int> Tops { get; } = [25, 50, 100, 200];

    public event EventHandler<NewQueryRequest>? QueryRequested;

    [ObservableProperty]
    public partial PerformanceSection Section { get; set; }

    /// <summary>The chosen database, or <see cref="AllDatabases"/>.</summary>
    [ObservableProperty]
    public partial string Database { get; set; }

    [ObservableProperty]
    public partial SettingChoice<HeavyQueryOrder> Order { get; set; } = Orders[0];

    [ObservableProperty]
    public partial int Top { get; set; } = 50;

    /// <summary>Index use: only the indexes with a finding.</summary>
    [ObservableProperty]
    public partial bool OnlyFindings { get; set; } = true;

    /// <summary>Fragmentation: smaller indexes are left out.</summary>
    [ObservableProperty]
    public partial long MinimumPages { get; set; } = 1000;

    /// <summary>The shown part's table.</summary>
    [ObservableProperty]
    public partial PerformanceTable Table { get; set; } = PerformanceTable.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial PerformanceRow? Selected { get; set; }

    /// <summary>The chosen row's SQL or script.</summary>
    public string Detail => Selected?.Detail ?? string.Empty;

    /// <summary>What the shown part is about and since when the server counts.</summary>
    [ObservableProperty]
    public partial string Note { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>The part needs one database (index use, fragmentation) and "all" is chosen.</summary>
    public bool NeedsDatabase => Section is PerformanceSection.IndexUsage or PerformanceSection.Fragmentation && Database == AllDatabases;

    /// <summary>The shown rows have scripts (missing, unused, fragmented indexes).</summary>
    public bool HasScripts => Section != PerformanceSection.HeavyQueries;

    private HeavyQueryReport? _heavy;
    private IReadOnlyList<MissingIndex>? _missing;
    private IndexUsageReport? _usage;
    private IReadOnlyList<IndexFragmentation>? _fragmentation;

    partial void OnSectionChanged(PerformanceSection value)
    {
        OnPropertyChanged(nameof(NeedsDatabase));
        OnPropertyChanged(nameof(HasScripts));
        ScriptAllCommand.NotifyCanExecuteChanged();
        Show();
        if (_started && value != PerformanceSection.Fragmentation && !_loaded.Contains(value))
        {
            _ = LoadAsync();
        }
    }

    partial void OnDatabaseChanged(string value)
    {
        OnPropertyChanged(nameof(NeedsDatabase));
        if (!_started)
        {
            return;
        }

        _loaded.Clear();
        _heavy = null;
        _missing = null;
        _usage = null;
        _fragmentation = null;
        Show();
        if (Section != PerformanceSection.Fragmentation)
        {
            _ = LoadAsync();
        }
    }

    partial void OnOrderChanged(SettingChoice<HeavyQueryOrder> value) => Reload(PerformanceSection.HeavyQueries);

    partial void OnTopChanged(int value) => Reload(PerformanceSection.HeavyQueries);

    partial void OnOnlyFindingsChanged(bool value) => Show();

    private void Reload(PerformanceSection section)
    {
        if (Section == section && _loaded.Contains(section))
        {
            _ = LoadAsync();
        }
    }

    /// <summary>Reads the databases and the first part; called when the tab opens.</summary>
    public async Task StartAsync()
    {
        _started = true;
        try
        {
            var names = await _jobs.Enqueue("Read databases", async context =>
            {
                var channel = await ChannelAsync(context.CancellationToken).ConfigureAwait(false);
                return await channel.GetDatabasesAsync(context.CancellationToken).ConfigureAwait(false);
            }).Completion;
            Databases.Clear();
            Databases.Add(AllDatabases);
            foreach (var name in names)
            {
                Databases.Add(name);
            }
        }
#pragma warning disable CA1031 // The list stays with "all databases"; the error shows.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ex.Message;
            if (Databases.Count == 0)
            {
                Databases.Add(AllDatabases);
            }
        }

        if (!Databases.Contains(Database))
        {
            Databases.Add(Database);
        }

        await LoadAsync();
    }

    /// <summary>Reads the shown part again (fragmentation: measures).</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (IsLoading)
        {
            _again = true;
            return;
        }

        var section = Section;
        if (NeedsDatabase)
        {
            Show();
            return;
        }

        var database = Database == AllDatabases ? null : Database;
        IsLoading = true;
        Error = null;
        try
        {
            switch (section)
            {
                case PerformanceSection.HeavyQueries:
                    var request = new HeavyQueryRequest(Order.Value, Top, database);
                    _heavy = await Run("Read heavy queries", (advisor, token) => advisor.GetHeavyQueriesAsync(request, token));
                    break;
                case PerformanceSection.MissingIndexes:
                    _missing = await Run("Read missing indexes", (advisor, token) => advisor.GetMissingIndexesAsync(database, token));
                    break;
                case PerformanceSection.IndexUsage:
                    _usage = await Run("Read index use", (advisor, token) => advisor.GetIndexUsageAsync(database!, token));
                    break;
                case PerformanceSection.Fragmentation:
                    var pages = MinimumPages;
                    _fragmentation = await Run("Measure fragmentation", (advisor, token) => advisor.GetFragmentationAsync(database!, pages, token));
                    break;
            }

            _loaded.Add(section);
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // A missing permission or a timeout is shown where the table would be.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }

        if (section == Section)
        {
            Show();
        }

        // What was asked meanwhile: the part now shown, if it is not read yet (fragmentation only when asked).
        if (_again)
        {
            _again = false;
            if (Section != section ? Section != PerformanceSection.Fragmentation && !_loaded.Contains(Section) : true)
            {
                await LoadAsync();
            }
        }
    }

    private Task<T> Run<T>(string name, Func<IPerformanceAdvisor, CancellationToken, Task<T>> work) =>
        _jobs.Enqueue(name, async context =>
        {
            var channel = await ChannelAsync(context.CancellationToken).ConfigureAwait(false);
            return await work(channel.Performance!, context.CancellationToken).ConfigureAwait(false);
        }).Completion;

    private bool CanOpen() => Selected is not null;

    /// <summary>Puts the chosen row's SQL or script into a new query tab (not run).</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open()
    {
        if (Selected is { } row)
        {
            QueryRequested?.Invoke(this, new NewQueryRequest(ConnectionId, row.Database) { Text = row.Script ?? row.Detail });
        }
    }

    private bool CanScriptAll() => HasScripts && Table.Rows.Any(static r => r.Script is not null);

    /// <summary>The scripts of every shown row, one after the other, in a new query tab (not run).</summary>
    [RelayCommand(CanExecute = nameof(CanScriptAll))]
    private void ScriptAll()
    {
        var rows = Table.Rows.Where(static r => r.Script is not null).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var text = new StringBuilder("-- ").Append(Strings.PerfScriptHeader).Append('\n');
        foreach (var row in rows)
        {
            text.Append('\n').Append(row.Script).Append('\n');
        }

        QueryRequested?.Invoke(this, new NewQueryRequest(ConnectionId, rows[0].Database) { Text = text.ToString() });
    }

    private async Task<QueryChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { } open)
        {
            return open;
        }

        var channel = await _session.OpenChannelAsync(cancellationToken).ConfigureAwait(false);
        if (channel.Performance is null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw new NotSupportedException(Strings.PerfUnsupported);
        }

        _channel = channel;
        return channel;
    }

    // The shown part's table from what was read.
    private void Show()
    {
        var chosen = Selected?.Detail;
        Table = Section switch
        {
            _ when NeedsDatabase => PerformanceTable.Empty,
            PerformanceSection.HeavyQueries => Heavy(),
            PerformanceSection.MissingIndexes => Missing(),
            PerformanceSection.IndexUsage => Usage(),
            _ => Fragmentation(),
        };
        Note = Section switch
        {
            _ when NeedsDatabase => Strings.PerfChooseDatabase,
            PerformanceSection.HeavyQueries => Since(Strings.PerfHeavyNote, _heavy?.CountingSince),
            PerformanceSection.MissingIndexes => Strings.PerfMissingNote,
            PerformanceSection.IndexUsage => Since(Strings.PerfUsageNote, _usage?.CountingSince),
            _ => _fragmentation is null ? Strings.PerfFragmentationNote : Strings.PerfFragmentationDone,
        };
        Selected = Table.Rows.FirstOrDefault(r => r.Detail == chosen) ?? (Table.Rows.Count > 0 ? Table.Rows[0] : null);
        ScriptAllCommand.NotifyCanExecuteChanged();
    }

    private static string Since(string note, DateTimeOffset? since) => since is { } start
        ? note + " " + string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.PerfSince), start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
        : note;

    private PerformanceTable Heavy()
    {
        var rows = (_heavy?.Queries ?? []).Select(q => new PerformanceRow(
        [
            OneLine(q.Sql), q.Database, q.ObjectName, N(q.Executions), ActivityRow.Duration(q.TotalCpu), ActivityRow.Duration(q.AverageCpu),
            ActivityRow.Duration(q.TotalDuration), ActivityRow.Duration(q.AverageDuration), N(q.TotalReads), N(q.AverageReads), N(q.TotalWrites),
            q.LastExecution?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
        ], q.Sql, q.Database)).ToList();
        return new PerformanceTable(
            [Strings.PerfColSql, Strings.ActivityColDatabase, Strings.PerfColObject, Strings.PerfColExecutions, Strings.PerfColTotalCpu, Strings.PerfColAverageCpu,
             Strings.PerfColTotalDuration, Strings.PerfColAverageDuration, Strings.PerfColTotalReads, Strings.PerfColAverageReads, Strings.ActivityColWrites, Strings.PerfColLast],
            [false, false, false, true, true, true, true, true, true, true, true, false],
            rows);
    }

    private PerformanceTable Missing()
    {
        var rows = (_missing ?? []).Select(m => new PerformanceRow(
        [
            m.Table, m.Database, string.Join(", ", m.EqualityColumns), string.Join(", ", m.InequalityColumns), string.Join(", ", m.IncludedColumns),
            m.ImpactPercent.ToString("0", CultureInfo.CurrentCulture) + " %", N(m.Uses), m.Score.ToString("N0", CultureInfo.CurrentCulture),
            m.LastWanted?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
        ], m.CreateScript, m.Database) { Script = m.CreateScript }).ToList();
        return new PerformanceTable(
            [Strings.PerfColTable, Strings.ActivityColDatabase, Strings.PerfColEquality, Strings.PerfColInequality, Strings.PerfColInclude, Strings.PerfColImpact,
             Strings.PerfColUses, Strings.PerfColScore, Strings.PerfColLast],
            [false, false, false, false, false, true, true, true, false],
            rows);
    }

    private PerformanceTable Usage()
    {
        var indexes = _usage?.Indexes ?? [];
        var findings = IndexAnalysis.Analyze(indexes);
        var rows = new List<PerformanceRow>();
        for (var i = 0; i < indexes.Count; i++)
        {
            var index = indexes[i];
            var finding = findings[i];
            if (OnlyFindings && finding.Kind == IndexFindingKind.None)
            {
                continue;
            }

            var findingText = finding.Kind switch
            {
                IndexFindingKind.Unused => Strings.PerfFindingUnused,
                IndexFindingKind.Duplicate => string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.PerfFindingDuplicate), finding.Other),
                IndexFindingKind.Covered => string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.PerfFindingCovered), finding.Other),
                _ => null,
            };
            var definition = index.Name + " (" + string.Join(", ", index.KeyColumns) + ")" +
                             (index.IncludedColumns.Count > 0 ? " INCLUDE (" + string.Join(", ", index.IncludedColumns) + ")" : string.Empty) +
                             (index.Filter is { } filter ? " WHERE " + filter : string.Empty);
            var script = finding.Kind == IndexFindingKind.None ? null : index.DropScript;
            rows.Add(new PerformanceRow(
            [
                index.Table, index.Name, findingText, string.Join(", ", index.KeyColumns), string.Join(", ", index.IncludedColumns),
                N(index.Seeks), N(index.Scans), N(index.Lookups), N(index.Updates), Size(index.SizeKb),
                index.LastRead?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                index.IsClustered ? Strings.PerfKindClustered : index.IsConstraint ? Strings.PerfKindConstraint : index.IsUnique ? Strings.PerfKindUnique : null,
            ], script is null ? "-- " + definition : "-- " + definition + "\n-- " + findingText + "\n" + script, Database) { Script = script, IsFinding = finding.Kind != IndexFindingKind.None });
        }

        return new PerformanceTable(
            [Strings.PerfColTable, Strings.PerfColIndex, Strings.PerfColFinding, Strings.PerfColKeys, Strings.PerfColInclude, Strings.PerfColSeeks,
             Strings.PerfColScans, Strings.PerfColLookups, Strings.PerfColUpdates, Strings.PerfColSize, Strings.PerfColLastRead, Strings.PerfColKind],
            [false, false, false, false, false, true, true, true, true, true, false, false],
            rows);
    }

    private PerformanceTable Fragmentation()
    {
        var rows = (_fragmentation ?? []).Select(f => new PerformanceRow(
        [
            f.Table, f.Name, f.FragmentationPercent.ToString("0.0", CultureInfo.CurrentCulture) + " %", N(f.Pages),
            f.Partition?.ToString(CultureInfo.CurrentCulture), f.Rebuild ? "REBUILD" : "REORGANIZE",
        ], f.MaintenanceScript ?? string.Empty, Database) { Script = f.MaintenanceScript }).ToList();
        return new PerformanceTable(
            [Strings.PerfColTable, Strings.PerfColIndex, Strings.PerfColFragmentation, Strings.PerfColPages, Strings.PerfColPartition, Strings.PerfColAction],
            [false, false, true, true, true, false],
            rows);
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string Size(long kb) => kb >= 1024 * 1024
        ? (kb / 1024d / 1024d).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
        : kb >= 1024 ? (kb / 1024d).ToString("0.0", CultureInfo.CurrentCulture) + " MB" : kb.ToString("N0", CultureInfo.CurrentCulture) + " KB";

    private static string OneLine(string sql)
    {
        var text = string.Join(' ', sql.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    /// <summary>Gives the analysis' connection back.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_channel is { } channel)
        {
            _channel = null;
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
