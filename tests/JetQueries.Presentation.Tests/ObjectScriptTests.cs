using System.Globalization;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>Scripts written from the explorer: what they say, and that the dangerous ones change nothing as written.</summary>
public sealed class ObjectScriptTests
{
    public ObjectScriptTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    private static readonly DatabaseObject Cards = new(new DatabaseObjectName("Cards", "dbo", "Cards"), DatabaseObjectKinds.Table);

    [Fact]
    public async Task Templates_name_every_column_and_go_by_the_primary_key()
    {
        await using var env = new TestEnvironment();
        var session = await Session(env);

        Assert.Equal("SELECT\n    Id,\n    CardNo,\n    Balance\nFROM dbo.Cards;", await Build(env, session, ObjectScriptKind.Select));
        Assert.Equal(
            "INSERT INTO dbo.Cards (Id, CardNo, Balance)\nVALUES (\n    NULL, -- Id bigint NOT NULL · PK\n    NULL, -- CardNo varchar(19) NOT NULL\n    NULL  -- Balance decimal(12,2)\n);",
            await Build(env, session, ObjectScriptKind.Insert));
        Assert.Equal(
            "UPDATE dbo.Cards\nSET CardNo = NULL, -- varchar(19) NOT NULL\n    Balance = NULL -- decimal(12,2)\nWHERE Id = NULL; -- Id bigint",
            await Build(env, session, ObjectScriptKind.Update));
        Assert.Equal("DELETE FROM dbo.Cards\nWHERE Id = NULL; -- Id bigint", await Build(env, session, ObjectScriptKind.Delete));

        // The fake provider has no row-limit syntax: a plain SELECT.
        Assert.Equal("SELECT Id, CardNo, Balance\nFROM dbo.Cards;", await Build(env, session, ObjectScriptKind.SelectRows));
    }

    [Fact]
    public async Task The_explorer_opens_scripts_in_new_tabs_and_runs_the_first_rows()
    {
        await using var env = new TestEnvironment();
        var (connections, node) = await TestEnvironment.ConnectAsync(env);
        await TestEnvironment.LoadedAsync(node);
        var database = (DatabaseObjectNodeViewModel)node.Children[0];
        await TestEnvironment.ExpandAsync(database);
        var schema = (DatabaseObjectNodeViewModel)database.Children[0];
        await TestEnvironment.ExpandAsync(schema);
        var tables = (CategoryNodeViewModel)schema.Children[0];
        await TestEnvironment.ExpandAsync(tables);
        var table = (DatabaseObjectNodeViewModel)tables.Children[0];
        Assert.True(table.HasData);
        Assert.True(table.HasDefinition);
        Assert.False(database.HasData);

        var requests = new List<NewQueryRequest>();
        connections.NewQueryRequested += (_, r) => requests.Add(r);
        await connections.ScriptDeleteCommand.ExecuteAsync(table);
        var delete = Assert.Single(requests);
        Assert.Equal(("Cards", "Cards", false), (delete.Title, delete.Database, delete.Run));
        Assert.StartsWith("DELETE FROM dbo.Cards", delete.Text, StringComparison.Ordinal);

        // No definition from the fake for a table: said under the explorer, no tab.
        await connections.ScriptCreateCommand.ExecuteAsync(table);
        Assert.Single(requests);
        Assert.Equal("The definition of Cards.dbo.Cards is not available (encrypted, or no permission to view it).", connections.ScriptError);

        // Select first rows: a tab that runs at once.
        await connections.SelectRowsCommand.ExecuteAsync(table);
        var documents = env.Documents();
        await documents.OpenAsync(requests[^1]);
        Assert.Equal("Cards", documents.Selected!.Title);
        Assert.Equal("SELECT Id, CardNo, Balance\nFROM dbo.Cards;", env.Provider.Executed[^1].Sql);
    }

    private static async Task<Core.Connections.ConnectionSession> Session(TestEnvironment env)
    {
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        return env.Manager.Sessions.Single();
    }

    private static Task<string?> Build(TestEnvironment env, Core.Connections.ConnectionSession session, ObjectScriptKind kind) =>
        ObjectScripts.BuildAsync(session, env.Metadata, Cards, kind, TestContext.Current.CancellationToken);
}
