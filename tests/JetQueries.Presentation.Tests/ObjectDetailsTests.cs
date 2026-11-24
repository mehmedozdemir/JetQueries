using System.Globalization;
using JetQueries.Presentation.ViewModels;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>The object details view (Ctrl+Click, F4, explorer "Details") and the explorer's ALTER / DROP scripts.</summary>
public sealed class ObjectDetailsTests
{
    private static readonly DatabaseObject Cards = new(new DatabaseObjectName("Cards", "dbo", "Cards"), DatabaseObjectKinds.Table);

    public ObjectDetailsTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
    }

    private static async Task<ObjectDetailsViewModel> DetailsAsync(TestEnvironment env, DatabaseObject target)
    {
        var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar"));
        var session = env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
        var details = new ObjectDetailsViewModel(session, target, env.Metadata, env.Jobs);
        await details.LoadAsync();
        return details;
    }

    [Fact]
    public async Task The_provider_details_become_translated_properties_and_tabs()
    {
        await using var env = new TestEnvironment();
        var orders = new DatabaseObject(new DatabaseObjectName("Cards", "dbo", "Orders"), DatabaseObjectKinds.Table);
        env.Provider.Details = target => new ObjectDetails(
            target,
            [new(ObjectDetailKeys.ObjectType, "Type", "USER TABLE"), new(ObjectDetailKeys.Rows, "Rows", "1,024"), new("custom", "Custom", "x"), new(ObjectDetailKeys.Comment, "Comment", null)],
            [
                new(ObjectDetailKeys.Columns, "Columns", [new(ObjectDetailKeys.Name, "Name"), new(ObjectDetailKeys.DataType, "Data type")], [new(["Id", "bigint"])]),
                new(ObjectDetailKeys.References, "References", [new(ObjectDetailKeys.Name, "Name"), new("other", "Other")], [new(["FK_Orders_Cards", "x"]) { Target = orders }]),
            ]);

        var details = await DetailsAsync(env, Cards);

        Assert.Null(details.Error);
        Assert.Equal("dbo.Cards", details.Title);
        Assert.StartsWith("Tablo · Kartlar", details.Subtitle, StringComparison.Ordinal);
        Assert.Equal([("Tür", "USER TABLE"), ("Satır (tahmini)", "1,024"), ("Custom", "x")], details.Properties.Select(static p => (p.Label, p.Value)));
        Assert.Equal(["Kolonlar (1)", "Referanslar (1)"], details.Sections.Select(static s => s.Header));
        Assert.Equal(["Ad", "Other"], details.Sections[1].Headers);
        Assert.True(details.Sections[1].HasTargets);
        Assert.False(details.Sections[0].HasTargets);

        // A row's object opens its own details.
        ObjectDetailsRequest? opened = null;
        details.DetailsRequested += (_, request) => opened = request;
        details.OpenRow(details.Sections[1].Rows[0]);
        Assert.Equal(orders, opened?.Target);
    }

    [Fact]
    public async Task A_progressive_provider_shows_the_columns_before_the_slow_tabs()
    {
        await using var env = new TestEnvironment();
        var columns = new ObjectDetailSection(ObjectDetailKeys.Columns, "Columns", [new(ObjectDetailKeys.Name, "Name")], [new(["Id"])]);
        var indexes = new ObjectDetailSection(ObjectDetailKeys.Indexes, "Indexes", [new(ObjectDetailKeys.Name, "Name")], [new(["IX_Cards"])]);
        var references = new ObjectDetailSection(ObjectDetailKeys.References, "References", [new(ObjectDetailKeys.Name, "Name")], [new(["FK_Orders_Cards"])]);
        var slow = new TaskCompletionSource();
        env.Provider.ProgressiveDetails = async (target, progress, token) =>
        {
            progress.Report(new ObjectDetails(target, [new(ObjectDetailKeys.ObjectType, "Type", "TABLE")], [columns]));
            await slow.Task.WaitAsync(token);
            return new ObjectDetails(target, [new(ObjectDetailKeys.ObjectType, "Type", "TABLE"), new(ObjectDetailKeys.Rows, "Rows", "5")], [columns, references, indexes]);
        };

        var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar"));
        var session = env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
        var details = new ObjectDetailsViewModel(session, Cards, env.Metadata, env.Jobs);
        var loading = details.LoadAsync();

        // The columns are there while the rest is still being read.
        for (var i = 0; i < 200 && details.Sections.Count == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(details.IsLoading);
        Assert.Equal([ObjectDetailKeys.Columns], details.Sections.Select(static s => s.Key));
        var columnsView = details.Sections[0];

        slow.SetResult();
        await loading;
        Assert.False(details.IsLoading);
        Assert.Equal([ObjectDetailKeys.Columns, ObjectDetailKeys.References, ObjectDetailKeys.Indexes], details.Sections.Select(static s => s.Key));
        Assert.Same(columnsView, details.Sections[0]);
        Assert.Equal(2, details.Properties.Count);
    }

    [Fact]
    public async Task The_ddl_opens_in_an_editor_tab_and_its_absence_is_said()
    {
        await using var env = new TestEnvironment();
        var view = new DatabaseObject(new DatabaseObjectName("Cards", "dbo", "CardSummary"), DatabaseObjectKinds.View);
        env.Provider.Details = target => new ObjectDetails(target, [], []);

        var details = await DetailsAsync(env, view);
        Assert.StartsWith("CREATE VIEW", details.Ddl, StringComparison.Ordinal);
        Assert.Null(details.DdlMessage);
        NewQueryRequest? query = null;
        details.NewQueryRequested += (_, request) => query = request;
        details.OpenDdlInEditorCommand.Execute(null);
        Assert.Equal(details.Ddl, query?.Text);
        Assert.False(query!.Run);

        var table = await DetailsAsync(env, Cards);
        Assert.Null(table.Ddl);
        Assert.False(table.OpenDdlInEditorCommand.CanExecute(null));
        Assert.Contains("Cards", table.DdlMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_provider_details_a_table_still_shows_its_columns()
    {
        await using var env = new TestEnvironment();
        var details = await DetailsAsync(env, Cards);

        var columns = Assert.Single(details.Sections);
        Assert.Equal(ObjectDetailKeys.Columns, columns.Key);
        Assert.Equal(["#", "Ad", "Veri tipi", "Null olabilir", "Anahtar"], columns.Headers);
        Assert.Equal(["1", "Id", "bigint", null, "PK"], columns.Rows[0].Cells);
    }

    [Fact]
    public async Task A_missing_object_says_so()
    {
        await using var env = new TestEnvironment();
        var details = await DetailsAsync(env, new DatabaseObject(new DatabaseObjectName("Cards", "dbo", "Nope"), DatabaseObjectKinds.Procedure));
        Assert.Equal("dbo.Nope bulunamadı ya da görme izniniz yok.", details.Error);
        Assert.Empty(details.Sections);
    }

    [Fact]
    public async Task Open_data_runs_the_first_rows_in_a_tab()
    {
        await using var env = new TestEnvironment();
        env.Provider.Details = target => new ObjectDetails(target, [], []);
        var details = await DetailsAsync(env, Cards);
        NewQueryRequest? query = null;
        details.NewQueryRequested += (_, request) => query = request;

        Assert.True(details.HasData);
        await details.SelectRowsCommand.ExecuteAsync(null);

        Assert.True(query?.Run);
        Assert.Contains("Cards", query!.Text, StringComparison.Ordinal);
        Assert.Equal("Cards", query.Database);
    }

    [Theory]
    [InlineData(DatabaseObjectKinds.View, true)]
    [InlineData(DatabaseObjectKinds.Procedure, true)]
    [InlineData(DatabaseObjectKinds.Trigger, true)]
    [InlineData(DatabaseObjectKinds.Table, false)]
    [InlineData(DatabaseObjectKinds.Sequence, false)]
    public void Alter_is_offered_for_programmable_objects(DatabaseObjectKinds kind, bool offered)
    {
        Assert.Equal(offered, ObjectScripts.CanAlter(kind));
    }
}
