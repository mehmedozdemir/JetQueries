using System.Globalization;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>Snippets: how they expand, and how the settings keep them.</summary>
public sealed class SnippetTests
{
    public SnippetTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    [Fact]
    public void A_snippet_takes_the_lines_indentation_and_puts_the_caret_at_its_marker()
    {
        Assert.Equal(("SELECT *\r\n    FROM ", 19), SqlSnippets.Expand("SELECT *\nFROM $0", "    ", "\r\n"));
        Assert.Equal(("GROUP BY ", 9), SqlSnippets.Expand("GROUP BY ", string.Empty, "\n"));

        var service = new SnippetService();
        Assert.Equal("SELECT *\nFROM $0", service.Find("SEL")?.Text);
        Assert.Null(service.Find("nope"));

        // Shortcuts that are not words are dropped.
        service.Apply([new("ok_1", "", "x"), new("1bad", "", "y"), new("has space", "", "z")]);
        Assert.Equal(["ok_1"], service.Current.Select(static s => s.Shortcut));
    }

    [Fact]
    public async Task The_settings_edit_validate_and_save_the_snippets()
    {
        await using var env = new TestEnvironment();
        var store = new Store();
        var snippets = new SnippetService();
        var settings = new SettingsViewModel(store, env.Jobs, new Appearance(), new Theme(), ["Consolas"], null, snippets);
        Assert.Equal(SqlSnippets.Defaults.Count, settings.SnippetItems.Count);
        Assert.Contains(settings.Sections, static s => s.Key == "snippets");

        settings.AddSnippetCommand.Execute(null);
        Assert.Equal("snippet1", settings.SelectedSnippet!.Shortcut);
        settings.SelectedSnippet.Text = "SELECT TOP (10) * FROM $0";

        // A repeated shortcut: shown, not saved, the page comes up.
        settings.SelectedSnippet.Shortcut = "SEL";
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("Each snippet needs a shortcut", settings.SnippetError, StringComparison.Ordinal);
        Assert.Equal("snippets", settings.SelectedSection.Key);
        Assert.Empty(store.Saved);

        settings.SelectedSnippet.Shortcut = "top10";
        settings.SelectedSnippet = settings.SnippetItems[0];
        settings.RemoveSnippetCommand.Execute(null);
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Null(settings.SnippetError);
        Assert.Equal("SELECT TOP (10) * FROM $0", snippets.Find("top10")?.Text);
        Assert.Null(snippets.Find("sel"));
        var saved = Assert.IsType<SnippetSettings>(store.Saved[SnippetSettings.Section]);
        Assert.Equal(SqlSnippets.Defaults.Count, saved.Snippets.Count);

        settings.ResetDefaultsCommand.Execute(null);
        Assert.Equal(SqlSnippets.Defaults.Select(static s => s.Shortcut), settings.SnippetItems.Select(static s => s.Shortcut));
    }

    private sealed class Store : Core.Settings.ISettingsStore
    {
        public Dictionary<string, object?> Saved { get; } = [];

        public Task<T> LoadAsync<T>(string section, T defaults, CancellationToken cancellationToken) => Task.FromResult(defaults);

        public Task SaveAsync<T>(string section, T value, CancellationToken cancellationToken)
        {
            Saved[section] = value;
            return Task.CompletedTask;
        }
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

        public bool IsDark => true;

        public void Toggle()
        {
        }

        public void SetDark(bool dark)
        {
        }
    }
}
