using System.Globalization;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>Formatting through the connection's language, and closing several query tabs at once.</summary>
public sealed class EditorCommandTests
{
    public EditorCommandTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    [Fact]
    public async Task Sql_is_formatted_by_the_connections_language_and_says_when_it_cannot_be()
    {
        await using var env = new TestEnvironment();
        var query = env.Query();
        Assert.Null(await query.FormatAsync("select 1"));
        Assert.StartsWith("Connect the query to a database", query.Status, StringComparison.Ordinal);

        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        query.RefreshSessions();
        Assert.Null(await query.FormatAsync("select 1"));
        Assert.Equal("This connection's language has no SQL formatter.", query.Status);

        env.Provider.LanguageService = new Shouting();
        query.FormatOptions = new SqlFormatOptions(IndentSize: 2);
        Assert.Equal("SELECT 1 /2", await query.FormatAsync("select 1"));
        Assert.Equal("SQL formatted (Ctrl+Z undoes it).", query.Status);
        Assert.Equal("SELECT 1 /2", await query.FormatAsync("SELECT 1 /2"));
        Assert.Equal("Already formatted.", query.Status);
    }

    [Fact]
    public async Task Other_tabs_or_all_tabs_close_and_a_canceled_question_stops_it()
    {
        await using var env = new TestEnvironment();
        var documents = env.Documents();
        var first = documents.Selected!;
        documents.NewQueryCommand.Execute(null);
        var second = documents.Selected!;
        documents.NewQueryCommand.Execute(null);
        var third = documents.Selected!;
        third.Text = "SELECT 3";

        // The changed tab asks; Cancel keeps it and stops.
        env.Dialogs.SaveChoice = SaveChangesChoice.Cancel;
        await documents.CloseOthersCommand.ExecuteAsync(second);
        Assert.Equal([second, third], documents.Documents);

        env.Dialogs.SaveChoice = SaveChangesChoice.Discard;
        await documents.CloseOthersCommand.ExecuteAsync(second);
        Assert.Equal([second], documents.Documents);
        Assert.Same(second, documents.Selected);

        await documents.CloseAllCommand.ExecuteAsync(null);
        Assert.Empty(documents.Documents);
        _ = first;
    }

    /// <summary>A language whose formatter upper-cases and notes the indent size.</summary>
    private sealed class Shouting : ISqlLanguageService, ISqlFormatter
    {
        public SqlLineTokens TokenizeLine(ReadOnlySpan<char> line, int startState) => new([], 0);

        public Task<IReadOnlyList<SqlDiagnostic>> GetDiagnosticsAsync(SqlDocument document, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SqlDiagnostic>>([]);

        public Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(SqlDocument document, int offset, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CompletionItem>>([]);

        public Task<SqlDefinition?> GetDefinitionAsync(SqlDocument document, int offset, CancellationToken cancellationToken) =>
            Task.FromResult<SqlDefinition?>(null);

        public string Format(string sql, SqlFormatOptions options) =>
            sql.EndsWith(" /2", StringComparison.Ordinal) ? sql : sql.ToUpperInvariant() + " /" + options.IndentSize.ToString(CultureInfo.InvariantCulture);
    }
}
