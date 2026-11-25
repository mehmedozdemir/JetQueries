using System.Globalization;
using JetQueries.Core.Connections;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>The performance analysis: which indexes are unnecessary, and what each part shows and opens.</summary>
public sealed class PerformanceTests
{
    public PerformanceTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
    }

    [Fact]
    public void Unused_duplicate_and_covered_indexes_are_found_never_keys_or_the_clustered_one()
    {
        var indexes = FakePerformance.Usage().Indexes;
        var findings = IndexAnalysis.Analyze(indexes).Select(static f => (f.Kind, f.Other)).ToList();

        Assert.Equal(
        [
            (IndexFindingKind.None, null),                                       // PK_Orders: clustered key
            (IndexFindingKind.Covered, "IX_Orders_Customer_Date"),             // (CustomerId) starts (CustomerId, OrderDate)
            (IndexFindingKind.None, null),                                       // IX_Orders_Customer_Date: used
            (IndexFindingKind.Unused, null),                                     // IX_Orders_Status: only written
            (IndexFindingKind.None, null),                                       // IX_Orders_Date: the first of two equal ones
            (IndexFindingKind.Duplicate, "IX_Orders_Date"),                    // IX_Orders_Date2: same keys
        ], findings);
    }

    [Theory]
    [InlineData(true, false, IndexFindingKind.None)]       // a unique index is not a duplicate of a plain one
    [InlineData(false, true, IndexFindingKind.Duplicate)]  // a plain one is, of a unique one
    public void Uniqueness_matters_for_duplicates(bool firstUnique, bool secondUnique, IndexFindingKind expected)
    {
        IndexUsage[] indexes =
        [
            new("dbo.T", "B") { KeyColumns = ["A"], IsUnique = secondUnique, Seeks = 1 },
            new("dbo.T", "A") { KeyColumns = ["A"], IsUnique = firstUnique, Seeks = 1 },
        ];
        Assert.Equal(expected, IndexAnalysis.Analyze(indexes)[1].Kind);
    }

    [Fact]
    public void Different_filters_or_missing_columns_are_not_redundant()
    {
        IndexUsage[] indexes =
        [
            new("dbo.T", "Wide") { KeyColumns = ["A", "B"], Seeks = 1 },
            new("dbo.T", "Filtered") { KeyColumns = ["A"], Filter = "([B] IS NOT NULL)", Seeks = 1 },
            new("dbo.T", "Includes") { KeyColumns = ["A"], IncludedColumns = ["C"], Seeks = 1 },
            new("dbo.Other", "SameKeysOtherTable") { KeyColumns = ["A"], Seeks = 1 },
        ];
        Assert.All(IndexAnalysis.Analyze(indexes), static f => Assert.Equal(IndexFindingKind.None, f.Kind));
    }

    [Fact]
    public async Task Each_part_is_read_when_shown_and_scripts_go_to_a_query_tab()
    {
        await using var env = new TestEnvironment();
        var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar"));
        var session = env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
        await using var analysis = new PerformanceViewModel(session, env.Jobs);
        await analysis.StartAsync();

        // Heavy queries first, over every database.
        Assert.Equal(["heavy:TotalCpu:50:"], env.Provider.PerformanceLog);
        Assert.Equal(PerformanceViewModel.AllDatabases, analysis.Databases[0]);
        Assert.Equal(2, analysis.Table.Rows.Count);
        Assert.Equal("1,200", analysis.Table.Rows[0].Cells[3]);
        Assert.Equal("35 ms", analysis.Table.Rows[0].Cells[5]);
        Assert.Contains("11/20/2026", analysis.Note, StringComparison.Ordinal);
        Assert.False(analysis.HasScripts);

        NewQueryRequest? opened = null;
        analysis.QueryRequested += (_, request) => opened = request;
        analysis.OpenCommand.Execute(null);
        Assert.Equal("SELECT * FROM dbo.Orders WHERE CustomerId = @1", opened?.Text);
        Assert.Equal("Cards", opened?.Database);

        analysis.Order = PerformanceViewModel.Orders.Single(static o => o.Value == HeavyQueryOrder.AverageReads);
        await Until(() => env.Provider.PerformanceLog.Count == 2);
        Assert.Equal("heavy:AverageReads:50:", env.Provider.PerformanceLog[^1]);

        analysis.Section = PerformanceSection.MissingIndexes;
        await Until(() => analysis.Table.Rows.Count == 1 && !analysis.IsLoading);
        Assert.Equal("missing:", env.Provider.PerformanceLog[^1]);
        Assert.Equal("88 %", analysis.Table.Rows[0].Cells[5]);
        analysis.ScriptAllCommand.Execute(null);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_Orders_Status]", opened?.Text, StringComparison.Ordinal);
        Assert.StartsWith("-- Proposed by JetQueries", opened?.Text, StringComparison.Ordinal);

        // Index use needs a database.
        analysis.Section = PerformanceSection.IndexUsage;
        Assert.True(analysis.NeedsDatabase);
        Assert.Empty(analysis.Table.Rows);
        analysis.Databases.Add("Cards");
        analysis.Database = "Cards";
        await Until(() => analysis.Table.Rows.Count > 0 && !analysis.IsLoading);
        Assert.Equal("usage:Cards", env.Provider.PerformanceLog[^1]);
        Assert.Equal(["IX_Orders_Customer", "IX_Orders_Status", "IX_Orders_Date2"], analysis.Table.Rows.Select(static r => r.Cells[1]));
        Assert.Equal("covered by IX_Orders_Customer_Date", analysis.Table.Rows[0].Cells[2]);
        analysis.OnlyFindings = false;
        Assert.Equal(6, analysis.Table.Rows.Count);
        Assert.Null(analysis.Table.Rows[0].Script);
        analysis.OnlyFindings = true;

        // Fragmentation only when asked.
        analysis.Section = PerformanceSection.Fragmentation;
        Assert.DoesNotContain(env.Provider.PerformanceLog, static l => l.StartsWith("fragmentation", StringComparison.Ordinal));
        Assert.Empty(analysis.Table.Rows);
        await analysis.LoadCommand.ExecuteAsync(null);
        Assert.Equal("fragmentation:Cards:1000", env.Provider.PerformanceLog[^1]);
        Assert.Equal("REBUILD", analysis.Table.Rows[0].Cells[5]);
        Assert.Equal("62.5 %", analysis.Table.Rows[0].Cells[2]);
    }

    [Fact]
    public async Task The_explorer_opens_one_analysis_per_connection_and_a_database_node_chooses_its_database()
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
        Assert.True(node.CanAnalyze);

        var documents = env.Documents();
        connections.PerformanceRequested += (_, analysis) => documents.OpenPerformance(analysis);
        connections.OpenPerformanceCommand.Execute(node);
        var analysis = Assert.Single(documents.Tabs.OfType<PerformanceViewModel>());
        Assert.Equal("Performance · Kartlar", analysis.HeaderText);

        await TestEnvironment.LoadedAsync(node);
        var database = node.Children.OfType<DatabaseObjectNodeViewModel>().First(static d => d.Target.Kind == DatabaseObjectKinds.Database);
        Assert.True(database.CanAnalyze);
        connections.OpenPerformanceCommand.Execute(database);
        Assert.Same(analysis, Assert.Single(documents.Tabs.OfType<PerformanceViewModel>()));
        Assert.Equal(database.Target.Name.Name, analysis.Database);

        await documents.CloseTabCommand.ExecuteAsync(analysis);
        Assert.Empty(documents.Tabs.OfType<PerformanceViewModel>());
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }
}
