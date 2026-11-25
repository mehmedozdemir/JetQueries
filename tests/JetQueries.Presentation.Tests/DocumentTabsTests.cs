using System.Globalization;
using JetQueries.Core.Settings;
using JetQueries.Presentation.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Presentation.Tests;

/// <summary>Pinned tabs stay at the front and survive bulk closing; tabs close to the left, right or by connection.</summary>
public sealed class DocumentTabsTests
{
    public DocumentTabsTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    [Fact]
    public async Task Pinned_tabs_go_first_and_stay_open_when_others_are_closed()
    {
        await using var env = new TestEnvironment();
        var cards = await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var billing = await env.OpenAsync(TestEnvironment.Definition("Billing"));
        var documents = env.Documents();
        var q1 = documents.Selected!;
        var q2 = documents.Open("Query 2", string.Empty, cards.Id);
        var q3 = documents.Open("Query 3", string.Empty, billing.Id);
        var q4 = documents.Open("Query 4", string.Empty, cards.Id);
        Assert.Equal([q1, q2, q3, q4], documents.Tabs);

        // Pinning moves a tab to the end of the pinned ones; the shown tab stays shown.
        documents.SelectedTab = q4;
        documents.TogglePinCommand.Execute(q3);
        Assert.True(q3.IsPinned);
        Assert.Equal([q3, q1, q2, q4], documents.Tabs);
        Assert.Same(q4, documents.SelectedTab);

        // A new tab never lands among the pinned ones.
        documents.SelectedTab = q3;
        documents.NewQueryCommand.Execute(null);
        var q5 = documents.Selected!;
        Assert.Equal([q3, q5, q1, q2, q4], documents.Tabs);

        // To the right / left of a tab, and the tabs of one connection; the pinned one stays.
        await documents.CloseTabsToRightCommand.ExecuteAsync(q1);
        Assert.Equal([q3, q5, q1], documents.Tabs);
        await documents.CloseTabsToLeftCommand.ExecuteAsync(q1);
        Assert.Equal([q3, q1], documents.Tabs);
        Assert.Same(q1, documents.SelectedTab);

        // The tabs of one connection (Billing): the pinned one stays, the other connection's tab too.
        await documents.CloseTabCommand.ExecuteAsync(q1);
        var q6 = documents.Open("Query 6", string.Empty, billing.Id);
        var q7 = documents.Open("Query 7", string.Empty, cards.Id);
        Assert.True(documents.HasConnectionSiblings(q6));
        Assert.False(documents.HasConnectionSiblings(q7));
        await documents.CloseConnectionTabsCommand.ExecuteAsync(q6);
        Assert.Equal([q3, q7], documents.Tabs);

        // Close others and close all leave the pinned tab; closing it on its own works.
        await documents.CloseOtherTabsCommand.ExecuteAsync(q7);
        Assert.Equal([q3, q7], documents.Tabs);
        await documents.CloseAllTabsCommand.ExecuteAsync(null);
        Assert.Equal([q3], documents.Tabs);
        documents.TogglePinCommand.Execute(q3);
        Assert.False(q3.IsPinned);
        await documents.CloseTabCommand.ExecuteAsync(q3);
        Assert.Empty(documents.Tabs);
    }

    [Fact]
    public async Task The_workspace_remembers_pinned_tabs_in_their_place()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jq-tabs-" + Guid.NewGuid().ToString("N"));
        var store = new JsonSettingsStore(new SettingsStoreOptions { Directory = directory }, NullLogger<JsonSettingsStore>.Instance);
        try
        {
            await using (var env = new TestEnvironment())
            {
                var documents = new DocumentsViewModel(env.Manager, env.Metadata, env.History, env.Jobs, env.Dialogs, env.Ui, store);
                documents.Selected!.Text = "SELECT 1";
                documents.NewQueryCommand.Execute(null);
                documents.Selected!.Text = "SELECT 2";
                documents.Pin(documents.Selected!, true);
                await documents.SaveWorkspaceAsync();
            }

            await using var next = new TestEnvironment();
            var restored = new DocumentsViewModel(next.Manager, next.Metadata, next.History, next.Jobs, next.Dialogs, next.Ui, store);
            await restored.RestoreAsync();
            Assert.Equal(["SELECT 2", "SELECT 1"], restored.Tabs.OfType<QueryDocumentViewModel>().Select(static d => d.Text));
            Assert.True(((QueryDocumentViewModel)restored.Tabs[0]).IsPinned);
            Assert.False(((QueryDocumentViewModel)restored.Tabs[1]).IsPinned);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
