using System.Globalization;
using System.Text;
using JetQueries.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Presentation.Tests;

public sealed class FilesAndWorkspaceTests
{
    public FilesAndWorkspaceTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("utf8-bom")]
    [InlineData("utf8")]
    [InlineData("windows-1254")]
    [InlineData("utf16")]
    public async Task A_file_opens_in_its_encoding_and_is_saved_back_in_it(string kind)
    {
        const string Text = "-- Müşteri İşlemleri: ğüşıöç\r\nSELECT * FROM kart.İşlem;\r\n";
        var encoding = kind switch
        {
            "utf8-bom" => new UTF8Encoding(true),
            "utf8" => new UTF8Encoding(false),
            "windows-1254" => Encoding.GetEncoding(1254),
            _ => Encoding.Unicode,
        };
        var path = Path.Combine(NewDirectory(), "query.sql");
        var original = encoding.GetPreamble().Concat(encoding.GetBytes(Text)).ToArray();
        await File.WriteAllBytesAsync(path, original, Token);

        await using var env = new TestEnvironment();
        var documents = env.Documents();
        var document = await documents.OpenFileAsync(path);

        Assert.NotNull(document);
        Assert.Equal(Text, document.Text);
        Assert.Equal("query.sql", document.Title);
        Assert.Equal(path, document.FilePath);
        Assert.False(document.IsDirty);

        // The untouched empty tab of the start made way for the file.
        Assert.Same(document, Assert.Single(documents.Documents));

        Assert.True(await documents.SaveAsync(document, saveAs: false));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, Token));
    }

    [Fact]
    public async Task A_new_query_is_saved_where_the_user_says_and_then_tracks_changes()
    {
        await using var env = new TestEnvironment();
        var documents = env.Documents();
        var query = documents.Selected!;
        Assert.Equal("Query 1", query.HeaderText);

        query.Text = "SELECT 1";
        Assert.True(query.IsDirty);
        Assert.Equal("Query 1 *", query.HeaderText);

        // Canceled save dialog: nothing happens.
        Assert.False(await documents.SaveAsync(query, saveAs: false));
        Assert.Contains("save:Query 1.sql", env.Dialogs.Asked);

        var path = Path.Combine(NewDirectory(), "kartlar.sql");
        env.Dialogs.SavePath = path;
        await documents.SaveCommand.ExecuteAsync(null);
        Assert.Equal("SELECT 1", await File.ReadAllTextAsync(path, Token));
        Assert.Equal(new byte[] { (byte)'S' }, (await File.ReadAllBytesAsync(path, Token))[..1]);
        Assert.Equal(("kartlar.sql", "kartlar.sql", false), (query.Title, query.HeaderText, query.IsDirty));
        Assert.Equal(path, query.ToolTip);

        // Saving again does not ask; Save as does.
        env.Dialogs.Asked.Clear();
        query.Text = "SELECT 2";
        await documents.SaveCommand.ExecuteAsync(null);
        Assert.Empty(env.Dialogs.Asked);
        Assert.Equal("SELECT 2", await File.ReadAllTextAsync(path, Token));
        env.Dialogs.SavePath = Path.Combine(Path.GetDirectoryName(path)!, "copy.sql");
        await documents.SaveAsCommand.ExecuteAsync(null);
        Assert.Equal("copy.sql", query.Title);

        // Opening a file that is open already selects its tab.
        documents.NewQueryCommand.Execute(null);
        Assert.Same(query, await documents.OpenFileAsync(env.Dialogs.SavePath));
        Assert.Same(query, documents.Selected);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_or_written_says_why()
    {
        await using var env = new TestEnvironment();
        var documents = env.Documents();

        Assert.Null(await documents.OpenFileAsync(Path.Combine(NewDirectory(), "missing.sql")));
        Assert.Contains("missing.sql", documents.Selected!.Status, StringComparison.Ordinal);

        var query = documents.Selected!;
        query.Text = "SELECT 1";
        env.Dialogs.SavePath = Path.Combine(NewDirectory(), "no-such-folder", "x.sql");
        Assert.False(await documents.SaveAsync(query, saveAs: false));
        Assert.Contains("x.sql", query.Status, StringComparison.Ordinal);
        Assert.True(query.IsDirty);
    }

    [Fact]
    public async Task Closing_a_changed_tab_asks_first()
    {
        await using var env = new TestEnvironment();
        var documents = env.Documents();
        var query = documents.Selected!;

        // An empty new query has nothing to lose.
        documents.NewQueryCommand.Execute(null);
        await documents.CloseCommand.ExecuteAsync(documents.Selected);
        Assert.Empty(env.Dialogs.Asked);

        query.Text = "SELECT 1";
        env.Dialogs.SaveChoice = SaveChangesChoice.Cancel;
        await documents.CloseCommand.ExecuteAsync(query);
        Assert.Contains(query, documents.Documents);
        Assert.Contains("save-changes:Query 1", env.Dialogs.Asked);

        // Save, but the save dialog is canceled: the tab stays.
        env.Dialogs.SaveChoice = SaveChangesChoice.Save;
        await documents.CloseCommand.ExecuteAsync(query);
        Assert.Contains(query, documents.Documents);

        env.Dialogs.SaveChoice = SaveChangesChoice.Discard;
        await documents.CloseCommand.ExecuteAsync(query);
        Assert.DoesNotContain(query, documents.Documents);
    }

    [Fact]
    public async Task The_workspace_brings_back_tabs_texts_files_and_connections()
    {
        var store = new JsonSettingsStore(new SettingsStoreOptions { Directory = NewDirectory() }, NullLogger<JsonSettingsStore>.Instance);
        var path = Path.Combine(NewDirectory(), "report.sql");
        await File.WriteAllTextAsync(path, "SELECT 'from file'", Token);
        var cards = TestEnvironment.Definition("Cards");

        await using (var env = new TestEnvironment())
        {
            await env.OpenAsync(cards);
            var documents = new DocumentsViewModel(env.Manager, env.Metadata, env.History, env.Jobs, env.Dialogs, env.Ui, store);
            var first = documents.Selected!;
            first.Text = "SELECT 'unsaved'";
            await WaitAsync(() => first.SelectedDatabase == "Cards");
            first.SelectedDatabase = "master";
            await WaitAsync(() => first.SelectedDatabase == "master");
            await documents.OpenFileAsync(path);
            documents.NewQueryCommand.Execute(null);
            documents.Selected = first;
            await documents.SaveWorkspaceAsync();
        }

        // The file changed while the application was closed: the clean tab shows the new content.
        await File.WriteAllTextAsync(path, "SELECT 'edited outside'", Token);

        await using var next = new TestEnvironment();
        var restored = new DocumentsViewModel(next.Manager, next.Metadata, next.History, next.Jobs, next.Dialogs, next.Ui, store);
        await restored.RestoreAsync();

        Assert.Equal(["Query 1 *", "report.sql", "Query 2"], restored.Documents.Select(static d => d.HeaderText));
        var unsaved = restored.Documents[0];
        Assert.Same(unsaved, restored.Selected);
        Assert.Equal("SELECT 'unsaved'", unsaved.Text);
        Assert.Equal("SELECT 'edited outside'", restored.Documents[1].Text);
        Assert.False(restored.Documents[1].IsDirty);

        // Not on any connection until Cards is opened, then back on it and in master.
        Assert.Null(unsaved.SelectedSession);
        await next.OpenAsync(cards);
        await WaitAsync(() => unsaved.SelectedSession is not null);
        Assert.Equal("Cards", unsaved.SelectedSession!.Name);
        await WaitAsync(() => unsaved.SelectedDatabase == "master");

        // New tabs continue the numbering.
        restored.NewQueryCommand.Execute(null);
        Assert.Equal("Query 3", restored.Selected!.Title);
    }

    [Fact]
    public async Task Changes_are_written_to_the_workspace_shortly_after_typing()
    {
        var store = new JsonSettingsStore(new SettingsStoreOptions(), NullLogger<JsonSettingsStore>.Instance);
        await using var env = new TestEnvironment();
        var documents = new DocumentsViewModel(env.Manager, env.Metadata, env.History, env.Jobs, env.Dialogs, env.Ui, store);

        documents.Selected!.Text = "SELECT 'typed'";
        await WaitAsync(
            async () => (await store.LoadAsync(WorkspaceState.Section, new WorkspaceState(), Token)).Documents is [{ Text: "SELECT 'typed'", IsDirty: true }],
            TimeSpan.FromSeconds(10));
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Token);
        }

        Assert.True(condition());
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < until, "condition not met in time");
            await Task.Delay(50, Token);
        }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "file-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
