using System.Globalization;
using JetQueries.Core.Library;
using JetQueries.Presentation.ViewModels;
using JetQueries.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Presentation.Tests;

/// <summary>Parameterized queries and the library of saved queries.</summary>
public sealed class SavedQueriesTests
{
    public SavedQueriesTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Placeholders_are_found_outside_strings_comments_and_casts()
    {
        const string Sql = """
            -- :not_this
            SELECT * FROM ${table} t /* :nor_this */
            WHERE t.id = :id AND t.name <> ':text' AND t.created::date > :since
              AND "col:x" = :id AND [a:b] IS NOT NULL AND $$ :body $$ = '' AND x := 1 AND :new.a = :old.b
            """;

        var found = QueryPlaceholders.Find(Sql);

        Assert.Equal(["table", "id", "since", "id"], found.Select(static p => p.Name));
        Assert.True(found[0].IsText);
        Assert.Equal(":id", Sql.Substring(found[1].Start, found[1].Length));
        Assert.Equal(
            "SELECT * FROM cards WHERE id = @id",
            QueryPlaceholders.Apply("SELECT * FROM ${table} WHERE id = :id", QueryPlaceholders.Find("SELECT * FROM ${table} WHERE id = :id"), n => "@" + n,
                new Dictionary<string, string> { ["table"] = "cards" }));
    }

    [Theory]
    [InlineData("42", QueryParameterKind.Auto, 42L)]
    [InlineData("12.5", QueryParameterKind.Auto, "12.5")]
    [InlineData("0532", QueryParameterKind.Auto, "0532")]
    [InlineData("İstanbul", QueryParameterKind.Auto, "İstanbul")]
    [InlineData("42", QueryParameterKind.Text, "42")]
    [InlineData("1.250,50", QueryParameterKind.Number, "1250.50")]
    [InlineData("x", QueryParameterKind.Null, null)]
    public void Values_are_sent_as_their_kind(string text, QueryParameterKind kind, object? expected)
    {
        var (value, ok) = QueryParametersViewModel.Convert(text, kind);

        Assert.True(ok);
        if (expected is "12.5" or "1250.50")
        {
            Assert.Equal(decimal.Parse((string)expected, CultureInfo.InvariantCulture), value);
        }
        else
        {
            Assert.Equal(expected, value);
        }

        Assert.False(QueryParametersViewModel.Convert("on iki", QueryParameterKind.Number).Ok);
        Assert.Equal(new DateTime(2026, 1, 15), QueryParametersViewModel.Convert("15.01.2026", QueryParameterKind.Date).Value);
    }

    [Fact]
    public async Task A_parameterized_query_asks_for_values_binds_them_and_refresh_keeps_them()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        env.Provider.ReturnRows(5);
        var asked = 0;
        env.Dialogs.Parameters = dialog =>
        {
            asked++;
            Assert.Equal(["${table}", ":id"], dialog.Items.Select(static i => i.Label));
            dialog.Items[1].Value = asked == 1 ? "42" : dialog.Items[1].Value;
            dialog.Items[0].Value = "dbo.Cards";
            return true;
        };
        var query = env.Query();
        query.Text = "SELECT * FROM ${table} WHERE Id = :id OR ParentId = :ID";

        await query.RunCommand.ExecuteAsync(null);

        var request = env.Provider.Executed[^1];
        Assert.Equal("SELECT * FROM dbo.Cards WHERE Id = @id OR ParentId = @id", request.Sql);
        Assert.Equal([new QueryParameter("id", 42L)], request.Parameters);

        // Refresh runs it again with the same values, without asking.
        await query.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(1, asked);
        Assert.Equal([new QueryParameter("id", 42L)], env.Provider.Executed[^1].Parameters);

        // Running again asks, with the last values filled in.
        await query.RunCommand.ExecuteAsync(null);
        Assert.Equal(2, asked);
        Assert.Equal(42L, env.Provider.Executed[^1].Parameters[0].Value);

        // Canceling sends nothing.
        var executed = env.Provider.Executed.Count;
        env.Dialogs.Parameters = null;
        await query.RunCommand.ExecuteAsync(null);
        Assert.Equal(executed, env.Provider.Executed.Count);
    }

    [Fact]
    public async Task Queries_are_saved_found_opened_and_updated()
    {
        await using var env = new TestEnvironment();
        var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar"));
        await env.Store.SaveAsync(definition, Token);
        var path = Path.Combine(AppContext.BaseDirectory, "saved-queries-tests", Guid.NewGuid().ToString("N"), "saved-queries.json");
        using var store = new JsonSavedQueryStore(new SavedQueryStoreOptions { FilePath = path }, NullLogger<JsonSavedQueryStore>.Instance);
        var documents = env.Documents();
        var library = new SavedQueriesViewModel(store, env.Store, env.Jobs, env.Dialogs, documents);
        await library.LoadAsync();
        Assert.True(library.IsEmpty);

        var document = documents.Open("Query 1", "SELECT * FROM Cards WHERE City = :city", definition.Id);
        env.Dialogs.SaveQuery = edit =>
        {
            Assert.Equal("Kartlar", edit.ConnectionName);
            edit.Name = "İstanbul kartları";
            edit.Folder = " Raporlar / Aylık ";
            edit.Description = "Şehre göre kartlar";
            return true;
        };
        var saved = await library.SaveDocumentAsync(document, asNew: false);

        Assert.NotNull(saved);
        Assert.Equal("Raporlar/Aylık", saved.Folder);
        Assert.Equal(definition.Id, saved.ConnectionId);
        Assert.Equal(saved.Id, document.SavedQueryId);
        Assert.Equal("İstanbul kartları", document.Title);
        Assert.True(File.Exists(path));

        // Saving the same tab again updates it without asking.
        env.Dialogs.SaveQuery = null;
        document.Text = "SELECT * FROM Cards WHERE City = :city ORDER BY Id";
        await library.SaveDocumentAsync(document, asNew: false);
        var all = await store.GetAllAsync(Token);
        Assert.Equal("SELECT * FROM Cards WHERE City = :city ORDER BY Id", Assert.Single(all).Sql);

        // Found by any word, the Turkish way: "istanbul" finds "İstanbul", "aylık" the folder.
        var reopened = new SavedQueriesViewModel(new JsonSavedQueryStore(new SavedQueryStoreOptions { FilePath = path }, NullLogger<JsonSavedQueryStore>.Instance), env.Store, env.Jobs, env.Dialogs, documents);
        await reopened.LoadAsync();
        reopened.SearchText = "istanbul aylık";
        var item = Assert.Single(reopened.Items);
        Assert.True(item.HasParameters);
        Assert.Contains("Kartlar", item.Detail, StringComparison.Ordinal);
        reopened.SearchText = "orders";
        Assert.True(reopened.IsFilteredEmpty);
        reopened.SearchText = string.Empty;

        // Opened in a new tab on its connection, linked to the library.
        reopened.OpenSelectedCommand.Execute(null);
        Assert.Equal(item.Query.Sql, documents.Selected!.Text);
        Assert.Equal(definition.Id, documents.Selected.SelectedSession?.Session.Definition.Id);
        Assert.Equal(item.Query.Id, documents.Selected.SavedQueryId);

        // Deleted after asking.
        env.Dialogs.Confirm = true;
        await reopened.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.True(reopened.IsEmpty);
        using var fresh = new JsonSavedQueryStore(new SavedQueryStoreOptions { FilePath = path }, NullLogger<JsonSavedQueryStore>.Instance);
        Assert.Empty(await fresh.GetAllAsync(Token));
    }
}
