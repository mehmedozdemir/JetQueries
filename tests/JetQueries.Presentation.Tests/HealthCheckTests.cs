using System.Globalization;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>The health check: order, counts, filters, scripts, and the rule texts in both languages.</summary>
public sealed class HealthCheckTests
{
    // The rules the SQL Server provider reports (SqlServerHealth).
    private static readonly string[] SqlServerRules =
    [
        "MaxServerMemory", "MaxDop", "CostThreshold", "OptimizeAdhoc", "PriorityBoost", "AutoShrink", "AutoClose", "PageVerify", "AutoStats",
        "Trustworthy", "CompatibilityLevel", "FileGrowth", "LogReuse", "TempdbFiles", "Backups", "FullBackup", "LogBackup", "CheckDb",
        "SuspectPages", "SaEnabled", "Sysadmins", "Uptime",
    ];

    public HealthCheckTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    public void Every_rule_has_a_title_a_reason_and_a_fix(string language)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        foreach (var rule in SqlServerRules)
        {
            foreach (var part in new[] { "Title", "Why", "Fix" })
            {
                Assert.True(Resources.Strings.Find($"Rule_{rule}_{part}") is { Length: > 10 } || part == "Title", $"Rule_{rule}_{part} is missing in {language}");
            }

            Assert.NotNull(Resources.Strings.Find($"Rule_{rule}_Title"));
        }
    }

    [Fact]
    public async Task Findings_come_most_severe_first_with_counts_filters_and_scripts()
    {
        await using var env = new TestEnvironment();
        var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar"));
        var session = env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
        await using var health = new HealthCheckViewModel(session, env.Jobs);
        await health.RunAsync();

        Assert.Null(health.Error);
        Assert.Equal((1, 3, 1, 2), (health.CriticalCount, health.WarningCount, health.InfoCount, health.OkCount));
        Assert.Equal(["FullBackup", "NewRule", "AutoShrink", "CostThreshold", "Uptime"], health.Rows.Select(static r => r.Finding.RuleId));
        Assert.Same(health.Rows[0], health.Selected);

        var backup = health.Rows[0];
        Assert.Equal("A database has no recent full backup", backup.Title);
        Assert.Equal("Critical", backup.SeverityText);
        Assert.Equal("Backups", backup.CategoryText);
        Assert.Equal("Cards (—), Sales (2026-11-10 22:00)", backup.Summary);
        Assert.StartsWith("Without a full backup", backup.Why, StringComparison.Ordinal);
        Assert.False(health.OpenScriptCommand.CanExecute(backup));

        // A rule the host does not know keeps the provider's title, without explanations.
        var unknown = health.Rows[1];
        Assert.Equal("A rule the host has no text for", unknown.Title);
        Assert.Empty(unknown.Why);

        var cost = health.Rows.Single(static r => r.Finding.RuleId == "CostThreshold");
        Assert.Equal("Now: 5 · Recommended: 50", cost.Summary);
        NewQueryRequest? opened = null;
        health.QueryRequested += (_, request) => opened = request;
        health.OpenScriptCommand.Execute(cost);
        Assert.StartsWith("-- Cost threshold for parallelism is the default 5\nEXEC sys.sp_configure", opened?.Text, StringComparison.Ordinal);
        health.ScriptAllCommand.Execute(null);
        Assert.Contains("-- Warning: Auto shrink is on\nALTER DATABASE [Archive] SET AUTO_SHRINK OFF;", opened?.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Uptime", opened?.Text, StringComparison.Ordinal);

        health.OnlyProblems = false;
        Assert.Equal(7, health.Rows.Count);
        Assert.Equal(HealthSeverity.Ok, health.Rows[^1].Severity);
        health.Category = HealthCheckViewModel.Categories.Single(static c => c.Value == HealthCategory.Databases);
        Assert.Equal(["AutoShrink", "PageVerify"], health.Rows.Select(static r => r.Finding.RuleId));
    }

    [Fact]
    public async Task The_explorer_opens_one_health_check_per_connection_and_runs_it()
    {
        await using var env = new TestEnvironment();
        var definition = TestEnvironment.Definition("Kartlar");
        await env.Store.SaveAsync(definition, TestContext.Current.CancellationToken);
        await env.Credentials.SetPasswordAsync(definition.Id, "x", TestContext.Current.CancellationToken);
        var connections = env.Connections();
        await connections.LoadAsync();
        connections.Selected = connections.Roots.OfType<ConnectionNodeViewModel>().Single();
        await connections.ToggleConnectionCommand.ExecuteAsync(null);
        var node = connections.Roots.OfType<ConnectionNodeViewModel>().Single();
        Assert.True(node.CanHealthCheck);
        Assert.True(node.HasDbaTools);

        var documents = env.Documents();
        connections.HealthRequested += (_, health) => documents.OpenHealth(health);
        connections.OpenHealthCommand.Execute(node);
        connections.OpenHealthCommand.Execute(node);
        var tab = Assert.Single(documents.Tabs.OfType<HealthCheckViewModel>());
        Assert.Equal("Health check · Kartlar", tab.HeaderText);
        for (var i = 0; i < 100 && tab.Rows.Count == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.NotEmpty(tab.Rows);
        await documents.CloseTabCommand.ExecuteAsync(tab);
        Assert.Empty(documents.Tabs.OfType<HealthCheckViewModel>());
    }
}
