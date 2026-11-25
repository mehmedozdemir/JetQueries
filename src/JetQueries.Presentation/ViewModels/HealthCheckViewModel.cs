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

/// <summary>A finding as the health check shows it, in the user's language (<c>Rule_&lt;id&gt;_…</c> resources).</summary>
public sealed class HealthRow(HealthFinding finding)
{
    public HealthFinding Finding { get; } = finding;

    public HealthSeverity Severity => Finding.Severity;

    public bool IsProblem => Finding.Severity is HealthSeverity.Warning or HealthSeverity.Critical;

    /// <summary>The rule's translated title, else the provider's.</summary>
    public string Title { get; } = Strings.Find("Rule_" + finding.RuleId + "_Title") ?? finding.Title;

    /// <summary>Why the rule matters; empty when the host has no text for it.</summary>
    public string Why { get; } = Strings.Find("Rule_" + finding.RuleId + "_Why") ?? string.Empty;

    /// <summary>What to do about it; empty when the host has no text for it.</summary>
    public string Fix { get; } = Strings.Find("Rule_" + finding.RuleId + "_Fix") ?? string.Empty;

    /// <summary>"Kritik", "Uyarı", "Bilgi", "Sorunsuz".</summary>
    public string SeverityText { get; } = SeverityName(finding.Severity);

    public string CategoryText { get; } = CategoryName(finding.Category);

    /// <summary>One line under the title: where it was found, or the value against the recommendation.</summary>
    public string Summary { get; } = SummaryOf(finding);

    public string? Current => Finding.Current;

    public string? Recommended => Finding.Recommended;

    public IReadOnlyList<string> Objects => Finding.Objects;

    public bool HasValues => Finding.Current is not null || Finding.Recommended is not null;

    public string? FixScript => Finding.FixScript;

    public static string SeverityName(HealthSeverity severity) => severity switch
    {
        HealthSeverity.Critical => Strings.HealthCritical,
        HealthSeverity.Warning => Strings.HealthWarning,
        HealthSeverity.Info => Strings.HealthInfo,
        _ => Strings.HealthOk,
    };

    public static string CategoryName(HealthCategory category) => category switch
    {
        HealthCategory.Configuration => Strings.HealthCatConfiguration,
        HealthCategory.Databases => Strings.HealthCatDatabases,
        HealthCategory.Backups => Strings.HealthCatBackups,
        HealthCategory.Security => Strings.HealthCatSecurity,
        _ => Strings.HealthCatPerformance,
    };

    private static string SummaryOf(HealthFinding finding)
    {
        if (finding.Objects.Count > 0)
        {
            var shown = string.Join(", ", finding.Objects.Take(3));
            return finding.Objects.Count > 3 ? shown + " … (+" + (finding.Objects.Count - 3).ToString(CultureInfo.CurrentCulture) + ")" : shown;
        }

        return finding.Severity == HealthSeverity.Ok || (finding.Current is null && finding.Recommended is null)
            ? string.Empty
            : string.Join(" · ", new[]
            {
                finding.Current is { } current ? Strings.HealthCurrent + ": " + current : null,
                finding.Recommended is { } recommended ? Strings.HealthRecommended + ": " + recommended : null,
            }.OfType<string>());
    }
}

/// <summary>
/// The health check of a connection: the provider's rules, run on a connection of its own when the tab opens and on
/// request; findings by severity, filtered by category; fix scripts go to a query tab, never run (ADR 0013).
/// </summary>
public sealed partial class HealthCheckViewModel : ObservableObject, IAsyncDisposable, IDocumentTab
{
    /// <inheritdoc />
    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    private readonly ConnectionSession _session;
    private readonly IJobQueue _jobs;
    private IReadOnlyList<HealthRow> _all = [];
    private QueryChannel? _channel;
    private bool _disposed;

    public HealthCheckViewModel(ConnectionSession session, IJobQueue jobs)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _jobs = jobs;
    }

    public Guid ConnectionId => _session.Definition.Id;

    Guid? IDocumentTab.ConnectionId => ConnectionId;

    public string ConnectionName => _session.Definition.Name;

    public ConnectionEnvironment Environment => _session.Definition.Environment;

    public string HeaderText => Strings.HealthTitle + " · " + ConnectionName;

    public string ToolTip => Strings.HealthTitle + "\n" + ConnectionName;

    public ISqlLanguageService? LanguageService => _session.Provider.LanguageService;

    /// <summary>"All" and the categories.</summary>
    public static IReadOnlyList<SettingChoice<HealthCategory?>> Categories { get; } =
    [
        new(null, Strings.HealthAll),
        .. Enum.GetValues<HealthCategory>().Select(static c => new SettingChoice<HealthCategory?>(c, HealthRow.CategoryName(c))),
    ];

    public ObservableCollection<HealthRow> Rows { get; } = [];

    public event EventHandler<NewQueryRequest>? QueryRequested;

    [ObservableProperty]
    public partial SettingChoice<HealthCategory?> Category { get; set; } = Categories[0];

    [ObservableProperty]
    public partial bool OnlyProblems { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(OpenScriptCommand))]
    public partial HealthRow? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    public partial int CriticalCount { get; set; }

    [ObservableProperty]
    public partial int WarningCount { get; set; }

    [ObservableProperty]
    public partial int InfoCount { get; set; }

    [ObservableProperty]
    public partial int OkCount { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelLoadCommand))]
    public partial bool IsLoading { get; set; }

    // The read now running, so that it can be stopped.
    private JetQueries.Core.Jobs.JobHandle? _load;

    private Task<T> Track<T>(JetQueries.Core.Jobs.JobHandle<T> job)
    {
        _load = job;
        return job.Completion;
    }

    /// <summary>Stops the read now running (the query on the server is cancelled).</summary>
    [RelayCommand(CanExecute = nameof(IsLoading))]
    private void CancelLoad() => _load?.Cancel();

    [ObservableProperty]
    public partial string CheckedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; set; }

    partial void OnCategoryChanged(SettingChoice<HealthCategory?> value) => Show();

    partial void OnOnlyProblemsChanged(bool value) => Show();

    /// <summary>Runs every rule again.</summary>
    [RelayCommand]
    public async Task RunAsync()
    {
        if (_disposed || IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var findings = await Track(_jobs.Enqueue("Health check", async context =>
            {
                var channel = await ChannelAsync(context.CancellationToken).ConfigureAwait(false);
                return await channel.Health!.CheckAsync(context.CancellationToken).ConfigureAwait(false);
            }));
            if (_disposed)
            {
                return;
            }

            _all = [.. findings.Select(static f => new HealthRow(f))];
            CriticalCount = _all.Count(static r => r.Severity == HealthSeverity.Critical);
            WarningCount = _all.Count(static r => r.Severity == HealthSeverity.Warning);
            InfoCount = _all.Count(static r => r.Severity == HealthSeverity.Info);
            OkCount = _all.Count(static r => r.Severity == HealthSeverity.Ok);
            CheckedText = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.HealthChecked), DateTime.Now.ToString("T", CultureInfo.CurrentCulture));
            Error = null;
            Show();
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // A missing permission is shown where the findings would be.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanOpenScript(HealthRow? row) => (row ?? Selected)?.FixScript is not null;

    /// <summary>Puts the chosen finding's fix into a new query tab (not run).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenScript))]
    private void OpenScript(HealthRow? row)
    {
        if ((row ?? Selected) is { FixScript: { } script } chosen)
        {
            QueryRequested?.Invoke(this, new NewQueryRequest(ConnectionId, null) { Text = "-- " + chosen.Title + "\n" + script });
        }
    }

    private bool CanScriptAll() => Rows.Any(static r => r.IsProblem && r.FixScript is not null);

    /// <summary>The fixes of every shown problem, one after the other, in a new query tab (not run).</summary>
    [RelayCommand(CanExecute = nameof(CanScriptAll))]
    private void ScriptAll()
    {
        var text = new StringBuilder("-- ").Append(Strings.HealthScriptHeader).Append('\n');
        foreach (var row in Rows.Where(static r => r.IsProblem && r.FixScript is not null))
        {
            text.Append("\n-- ").Append(row.SeverityText).Append(": ").Append(row.Title).Append('\n').Append(row.FixScript).Append('\n');
        }

        QueryRequested?.Invoke(this, new NewQueryRequest(ConnectionId, null) { Text = text.ToString() });
    }

    private async Task<QueryChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { } open)
        {
            return open;
        }

        var channel = await _session.OpenChannelAsync(cancellationToken).ConfigureAwait(false);
        if (channel.Health is null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw new NotSupportedException(Strings.HealthUnsupported);
        }

        _channel = channel;
        return channel;
    }

    // Critical first, then warnings, info and passing rules; within each, by category.
    private void Show()
    {
        var chosen = Selected?.Finding.RuleId;
        Rows.Clear();
        foreach (var row in _all
                     .Where(r => Category.Value is not { } category || r.Finding.Category == category)
                     .Where(r => !OnlyProblems || r.Severity != HealthSeverity.Ok)
                     .OrderByDescending(static r => r.Severity)
                     .ThenBy(static r => r.Finding.Category))
        {
            Rows.Add(row);
        }

        Selected = Rows.FirstOrDefault(r => r.Finding.RuleId == chosen) ?? (Rows.Count > 0 ? Rows[0] : null);
        ScriptAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Gives the health check's connection back.</summary>
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
