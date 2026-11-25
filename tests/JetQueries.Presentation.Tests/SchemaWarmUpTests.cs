using System.Globalization;
using JetQueries.Provider.SqlServer;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>
/// What completion needs is read ahead after connecting (one read for all the columns of the default schema),
/// the same list asked for at once is read once, and the status bar says what is being read.
/// </summary>
public sealed class SchemaWarmUpTests
{
    private static readonly ISqlLanguageService TSql = new SqlServerProvider().LanguageService!;

    public SchemaWarmUpTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_query_tab_reads_the_schema_ahead_and_completion_then_reads_nothing()
    {
        await using var env = new TestEnvironment();
        env.Provider.LanguageService = TSql;
        env.Provider.MetadataGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var query = env.Query();

        // While the database is slow, the status bar says what is being read.
        await Until(() => query.SelectedDatabase == "Cards" && query.SchemaStatus is not null);
        Assert.Equal("Reading the structure of Cards for IntelliSense…", query.SchemaStatus);
        env.Provider.MetadataGate.SetResult();
        await Until(() => query.SchemaStatus is null);

        string[] calls;
        lock (env.Provider.MetadataCalls)
        {
            calls = [.. env.Provider.MetadataCalls];
        }

        // The table list and every column of dbo, in one read each; no table on its own.
        Assert.Contains("objects:Cards/dbo:Table, View", calls);
        Assert.Single(calls, static c => c == "allcolumns:Cards/dbo");
        Assert.DoesNotContain(calls, static c => c.StartsWith("columns:", StringComparison.Ordinal));

        var tables = await query.CompleteAsync("SELECT * FROM Ca", 16, Token);
        Assert.Contains(tables, static t => t.Label == "Cards");
        var columns = await query.CompleteAsync("SELECT c. FROM Cards c", 9, Token);
        Assert.Equal(["Id", "CardNo", "Balance"], columns.Where(static c => c.Kind == CompletionItemKind.Column).Select(static c => c.Label));
        lock (env.Provider.MetadataCalls)
        {
            Assert.Equal(calls.Length, env.Provider.MetadataCalls.Count);
        }

        // Choosing the same database again does not read it again.
        query.SelectedDatabase = "Cards";
        await Task.Delay(50, Token);
        Assert.Null(query.SchemaStatus);
    }

    [Fact]
    public async Task The_same_list_asked_for_at_once_is_read_once()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var session = Assert.Single(env.Manager.Sessions);
        var metadata = env.Metadata;
        var dbo = new DatabaseObject(new DatabaseObjectName("Cards", null, "dbo"), DatabaseObjectKinds.Schema);
        env.Provider.MetadataGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = metadata.GetObjectsAsync(session, dbo, DatabaseObjectKinds.Table, refresh: false, Token);
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var abandoned = metadata.GetObjectsAsync(session, dbo, DatabaseObjectKinds.Table, refresh: false, giveUp.Token);
        var second = metadata.GetObjectsAsync(session, dbo, DatabaseObjectKinds.Table, refresh: false, Token);
        await giveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        env.Provider.MetadataGate.SetResult();

        Assert.Equal(3, (await first).Count);
        Assert.Equal(3, (await second).Count);
        lock (env.Provider.MetadataCalls)
        {
            Assert.Single(env.Provider.MetadataCalls, static c => c == "objects:Cards/dbo:Table");
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Token);
        }

        Assert.True(condition());
    }
}
