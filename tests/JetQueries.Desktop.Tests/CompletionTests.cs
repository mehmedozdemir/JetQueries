using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using JetQueries.Core.Sql;
using JetQueries.Editor;
using JetQueries.Sdk;

namespace JetQueries.Desktop.Tests;

[Collection(HeadlessUi.Collection)]
public sealed class CompletionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] ColumnNames = ["Id", "Card No", "Balance"];

    [Fact]
    public async Task A_query_uses_its_connections_language_and_cached_metadata()
    {
        await using var env = new TestEnvironment();
        env.Provider.LanguageService = new TablesOfDbo();
        var query = env.Query();

        // No connection: plain colouring, nothing to complete.
        Assert.Same(BasicSqlLanguageService.Instance, query.LanguageService);
        Assert.Empty(await query.CompleteAsync("SELECT * FROM ", 14, Token));

        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        query.RefreshSessions();
        Assert.Same(env.Provider.LanguageService, query.LanguageService);

        var first = await query.CompleteAsync("SELECT * FROM ", 14, Token);
        var second = await query.CompleteAsync("SELECT * FROM ", 14, Token);

        Assert.Equal(["Cards", "Müşteri İşlemleri", "Transactions"], first.Select(static i => i.Label));
        Assert.Equal(first.Select(static i => i.Label), second.Select(static i => i.Label));

        // The second request was answered from the metadata cache: the "database" was asked once.
        Assert.Equal(["objects:dbo:Table"], env.Provider.MetadataCalls);
    }

    [Fact]
    public async Task Typing_opens_the_completion_list_and_enter_inserts_the_choice()
    {
        var session = HeadlessUnitTestSession.StartNew(typeof(EditorApp));
        var (labelsAfterDot, textAfterEnter, openAfterEnter, requests) = await session.Dispatch(
            () =>
            {
                var asked = new List<(string Text, int Offset)>();
                var editor = new SqlEditor
                {
                    CompletionSource = (text, offset, _) =>
                    {
                        asked.Add((text, offset));
                        var start = text.LastIndexOf('.', Math.Max(0, offset - 1)) + 1;
                        var typed = text[start..offset];
                        IReadOnlyList<CompletionItem> items = text.Contains('.', StringComparison.Ordinal)
                            ?
                            [
                                .. ColumnNames
                                    .Where(name => name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                                    .Select(name => new CompletionItem(name, CompletionItemKind.Column, new TextSpan(start, offset - start))
                                    {
                                        InsertText = name.Contains(' ', StringComparison.Ordinal) ? $"[{name}]" : name,
                                    }),
                            ]
                            : [];
                        return Task.FromResult(items);
                    },
                };
                var window = new Window { Width = 600, Height = 300, Content = editor };
                window.Show();
                editor.FocusText();
                Dispatcher.UIThread.RunJobs();

                window.KeyTextInput("c");
                Dispatcher.UIThread.RunJobs();
                var openAfterLetter = editor.IsCompletionOpen;
                window.KeyTextInput(".");
                Dispatcher.UIThread.RunJobs();
                var labels = editor.CompletionLabels.ToList();

                window.KeyTextInput("C");
                Dispatcher.UIThread.RunJobs();
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();

                var result = (labels, editor.Text, editor.IsCompletionOpen || openAfterLetter, asked.ToList());
                window.Close();
                return result;
            },
            Token);

        // Disposing waits for the dispatcher thread, and the code above this line resumed on that thread.
        await Task.Run(session.Dispose, Token);

        Assert.Equal(["Id", "Card No", "Balance"], labelsAfterDot);
        Assert.Equal("c.[Card No]", textAfterEnter);
        Assert.False(openAfterEnter);
        Assert.Equal([("c", 1), ("c.", 2)], requests);
    }

    private static class EditorApp
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure(static () => new App())
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont();
    }

    /// <summary>A language service that offers the tables of schema dbo, read through the document's metadata.</summary>
    private sealed class TablesOfDbo : ISqlLanguageService
    {
        public SqlLineTokens TokenizeLine(ReadOnlySpan<char> line, int startState) => new([], 0);

        public Task<IReadOnlyList<SqlDiagnostic>> GetDiagnosticsAsync(SqlDocument document, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SqlDiagnostic>>([]);

        public async Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(SqlDocument document, int offset, CancellationToken cancellationToken)
        {
            var items = new List<CompletionItem>();
            var schema = new DatabaseObject(new DatabaseObjectName(null, null, "dbo"), DatabaseObjectKinds.Schema);
            await foreach (var table in document.Metadata!.GetObjectsAsync(schema, DatabaseObjectKinds.Table, cancellationToken))
            {
                items.Add(new CompletionItem(table.Name.Name, CompletionItemKind.Table, new TextSpan(offset, 0)));
            }

            return items;
        }

        public Task<SqlDefinition?> GetDefinitionAsync(SqlDocument document, int offset, CancellationToken cancellationToken) =>
            Task.FromResult<SqlDefinition?>(null);
    }
}
