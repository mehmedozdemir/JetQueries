using System.Globalization;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>The formatting page of the settings: its options, their preview, and the query tabs that use them.</summary>
public sealed class SettingsFormatTests
{
    public SettingsFormatTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Format_options_are_previewed_saved_and_used_by_query_tabs()
    {
        await using var env = new TestEnvironment();
        var store = new MemoryStore();
        var appearance = new Appearance();
        var settings = new SettingsViewModel(store, env.Jobs, appearance, new Theme(), ["Consolas"], new OptionsEcho());

        Assert.Equal(["appearance", "editor", "results", "query", "format"], settings.Sections.Select(static s => s.Key));
        Assert.Equal("appearance", settings.SelectedSection.Key);
        Assert.True(settings.CanPreviewFormat);
        Assert.Equal(SqlKeywordCase.Upper, settings.FormatKeywordCase!.Value);
        Assert.Equal(4, settings.FormatIndent!.Value);

        // Each change lays the sample out again.
        settings.FormatKeywordCase = settings.KeywordCases.Single(static c => c.Value == SqlKeywordCase.Lower);
        settings.FormatIndent = settings.Indents.Single(static c => c.Value == 0);
        settings.FormatConditionsOnNewLine = false;
        await Until(() => settings.FormatPreview == "Lower|4|tabs|content|-");

        await settings.SaveCommand.ExecuteAsync(null);
        var saved = Assert.IsType<AppearanceSettings>(store.Saved);
        Assert.Equal((SqlKeywordCase.Lower, 0, true, false), (saved.FormatKeywordCase, saved.FormatIndent, saved.FormatContentOnNewLine, saved.FormatConditionsOnNewLine));

        // A query tab formats with the options in effect when it formats, not when it opened.
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        env.Provider.LanguageService = new OptionsEcho();
        var documents = new DocumentsViewModel(env.Manager, env.Metadata, env.History, env.Jobs, env.Dialogs, env.Ui, null, appearance);
        var query = documents.Selected!;
        query.RefreshSessions();
        Assert.Equal("Lower|4|tabs|content|-", await query.FormatAsync("select 1"));
        appearance.Apply(appearance.Current with { FormatIndent = 2, FormatKeywordCase = SqlKeywordCase.AsWritten });
        Assert.Equal("AsWritten|2|spaces|content|-", await query.FormatAsync("select 1"));
    }

    [Fact]
    public void Odd_values_from_a_hand_edited_file_are_brought_back_in_range()
    {
        var odd = new AppearanceSettings { FormatIndent = 40, FormatKeywordCase = (SqlKeywordCase)9 }.Normalized();
        Assert.Equal((8, SqlKeywordCase.Upper), (odd.FormatIndent, odd.FormatKeywordCase));
        Assert.Equal(SqlFormatOptions.Default, new AppearanceSettings().FormatOptions());
    }

    [Fact]
    public void Without_a_formatter_the_preview_shows_the_sample_as_it_is()
    {
        var settings = new SettingsViewModel(new MemoryStore(), new Core.Jobs.JobQueue(), new Appearance(), new Theme(), ["Consolas"]);
        Assert.False(settings.CanPreviewFormat);
        Assert.Equal(SettingsViewModel.FormatSample, settings.FormatPreview);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Token);
        }

        Assert.True(condition());
    }

    /// <summary>A formatter that writes back the options it was given.</summary>
    private sealed class OptionsEcho : ISqlLanguageService, ISqlFormatter
    {
        public SqlLineTokens TokenizeLine(ReadOnlySpan<char> line, int startState) => new([], 0);

        public Task<IReadOnlyList<SqlDiagnostic>> GetDiagnosticsAsync(SqlDocument document, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SqlDiagnostic>>([]);

        public Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(SqlDocument document, int offset, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CompletionItem>>([]);

        public Task<SqlDefinition?> GetDefinitionAsync(SqlDocument document, int offset, CancellationToken cancellationToken) =>
            Task.FromResult<SqlDefinition?>(null);

        public string Format(string sql, SqlFormatOptions options) =>
            string.Join('|', options.KeywordCase, options.IndentSize, options.UseTabs ? "tabs" : "spaces", options.ContentOnNewLine ? "content" : "-",
                options.ConditionsOnNewLine ? "conditions" : "-");
    }

    private sealed class Appearance : IAppearanceService
    {
        public AppearanceSettings Current { get; private set; } = new();

        public void Apply(AppearanceSettings settings) => Current = settings;
    }

    private sealed class Theme : IThemeService
    {
#pragma warning disable CS0067 // Never raised in these tests.
        public event EventHandler? Changed;
#pragma warning restore CS0067

        public bool IsDark { get; private set; } = true;

        public void Toggle() => IsDark = !IsDark;

        public void SetDark(bool dark) => IsDark = dark;
    }

    private sealed class MemoryStore : Core.Settings.ISettingsStore
    {
        public object? Saved { get; private set; }

        public Task<T> LoadAsync<T>(string section, T defaults, CancellationToken cancellationToken) => Task.FromResult(defaults);

        public Task SaveAsync<T>(string section, T value, CancellationToken cancellationToken)
        {
            Saved = value;
            return Task.CompletedTask;
        }
    }
}
