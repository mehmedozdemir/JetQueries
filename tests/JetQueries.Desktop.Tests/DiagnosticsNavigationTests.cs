using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using JetQueries.Editor;
using JetQueries.Sdk;

namespace JetQueries.Desktop.Tests;

[Collection(HeadlessUi.Collection)]
public sealed class DiagnosticsNavigationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public DiagnosticsNavigationTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    [Fact]
    public async Task Diagnostics_come_from_the_connections_language_service()
    {
        await using var env = new TestEnvironment();
        env.Provider.LanguageService = new ScriptedLanguage();
        var query = env.Query();

        Assert.Empty(await query.DiagnoseAsync("SELECT oops", Token));

        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        query.RefreshSessions();

        var diagnostic = Assert.Single(await query.DiagnoseAsync("SELECT oops", Token));
        Assert.Equal(new TextSpan(7, 4), diagnostic.Span);
        Assert.Empty(await query.DiagnoseAsync("SELECT 1", Token));
    }

    [Fact]
    public async Task Go_to_definition_opens_the_source_in_a_new_tab_on_the_same_connection()
    {
        await using var env = new TestEnvironment();
        env.Provider.LanguageService = new ScriptedLanguage();
        var cards = await env.OpenAsync(TestEnvironment.Definition("Cards"));
        await env.OpenAsync(TestEnvironment.Definition("Logs"));
        var documents = env.Documents();
        var query = documents.Selected!;
        query.SelectSession(cards.Id);
        query.Text = "SELECT * FROM CardSummary";

        await query.GoToDefinitionAsync(query.Text, 16);

        Assert.Equal(2, documents.Documents.Count);
        var opened = documents.Selected!;
        Assert.NotSame(query, opened);
        Assert.Equal("CardSummary", opened.Title);
        Assert.Equal("CREATE VIEW dbo.CardSummary AS SELECT 1 AS n;", opened.Text);
        Assert.Equal("Cards", opened.SelectedSession!.Name);

        // From the new tab it works again: definitions chain.
        await opened.GoToDefinitionAsync("SELECT * FROM CardSummary", 16);
        Assert.Equal(3, documents.Documents.Count);
    }

    [Fact]
    public async Task When_there_is_nothing_to_show_the_status_says_so()
    {
        await using var env = new TestEnvironment();
        env.Provider.LanguageService = new ScriptedLanguage();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var documents = env.Documents();
        var query = documents.Selected!;

        await query.GoToDefinitionAsync("SELECT 1", 3);
        Assert.Equal("Nothing to go to here.", query.Status);

        await query.GoToDefinitionAsync("EXEC Encrypted", 8);
        Assert.Contains("dbo.Encrypted", query.Status, StringComparison.Ordinal);
        Assert.Contains("not available", query.Status, StringComparison.Ordinal);
        Assert.Single(documents.Documents);
    }

    [Fact]
    public async Task The_editor_underlines_errors_and_f12_asks_to_navigate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "screenshots", "editor-diagnostics.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var session = HeadlessUnitTestSession.StartNew(typeof(EditorApp));
        var (count, afterFix, navigated) = await session.Dispatch(
            async () =>
            {
                var requested = new List<int>();
                var editor = new SqlEditor
                {
                    LanguageService = Core.Sql.BasicSqlLanguageService.Instance,
                    DiagnosticSource = (text, _) => new ScriptedLanguage().GetDiagnosticsAsync(new SqlDocument(text), default),
                };
                editor.NavigationRequested += (_, offset) => requested.Add(offset);
                var window = new Window { Width = 520, Height = 140, Content = editor };
                window.Show();

                editor.Text = "SELECT c.Id, oops\nFROM dbo.Cards c\nWHERE c.Balance > oops;";
                await editor.RefreshDiagnosticsAsync();
                Dispatcher.UIThread.RunJobs();
                var found = editor.Diagnostics.Count;
                using (var frame = window.CaptureRenderedFrame()!)
                {
                    frame.Save(path, new PngBitmapEncoderOptions());
                }

                editor.FocusText();
                Dispatcher.UIThread.RunJobs();
                window.KeyPressQwerty(PhysicalKey.F12, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.F12, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();

                editor.Text = "SELECT 1";
                await editor.RefreshDiagnosticsAsync();
                var remaining = editor.Diagnostics.Count;
                window.Close();
                return (found, remaining, requested.Count);
            },
            Token);
        await Task.Run(session.Dispose, Token);

        Assert.Equal(2, count);
        Assert.Equal(0, afterFix);
        Assert.Equal(1, navigated);
        Assert.True(new FileInfo(path).Length > 1_000);
    }

    private static class EditorApp
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure(static () => new App())
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont();
    }
}

/// <summary>
/// A language service for tests: every "oops" is an error; "CardSummary" is a view of dbo and "Encrypted" a
/// procedure whose source cannot be read.
/// </summary>
internal sealed class ScriptedLanguage : ISqlLanguageService
{
    public SqlLineTokens TokenizeLine(ReadOnlySpan<char> line, int startState) => new([], 0);

    public Task<IReadOnlyList<SqlDiagnostic>> GetDiagnosticsAsync(SqlDocument document, CancellationToken cancellationToken)
    {
        var diagnostics = new List<SqlDiagnostic>();
        for (var i = document.Text.IndexOf("oops", StringComparison.Ordinal); i >= 0; i = document.Text.IndexOf("oops", i + 1, StringComparison.Ordinal))
        {
            diagnostics.Add(new SqlDiagnostic(new TextSpan(i, 4), "Incorrect syntax near 'oops'.", SqlDiagnosticSeverity.Error));
        }

        return Task.FromResult<IReadOnlyList<SqlDiagnostic>>(diagnostics);
    }

    public Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(SqlDocument document, int offset, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CompletionItem>>([]);

    public Task<SqlDefinition?> GetDefinitionAsync(SqlDocument document, int offset, CancellationToken cancellationToken)
    {
        SqlDefinition? definition = document.Text switch
        {
            var text when text.Contains("CardSummary", StringComparison.Ordinal) =>
                new SqlDefinition(new DatabaseObject(new DatabaseObjectName(null, "dbo", "CardSummary"), DatabaseObjectKinds.View)),
            var text when text.Contains("Encrypted", StringComparison.Ordinal) =>
                new SqlDefinition(new DatabaseObject(new DatabaseObjectName(null, "dbo", "Encrypted"), DatabaseObjectKinds.Procedure)),
            _ => null,
        };
        return Task.FromResult(definition);
    }
}
