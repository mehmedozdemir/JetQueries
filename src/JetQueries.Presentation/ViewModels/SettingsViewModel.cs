using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Jobs;
using JetQueries.Core.Settings;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.Services;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>How the application looks: fonts and text sizes. Saved as the <c>appearance</c> settings section.</summary>
public sealed record AppearanceSettings
{
    /// <summary>The settings section (file) name.</summary>
    public const string Section = "appearance";

    /// <summary>Font of the user interface; a comma-separated fallback list is allowed.</summary>
    public string UiFontFamily { get; init; } = "Segoe UI Variable Text, Segoe UI";

    /// <summary>Body text size of the user interface, in device-independent pixels.</summary>
    public double UiFontSize { get; init; } = 13;

    /// <summary>Font of the SQL editor.</summary>
    public string EditorFontFamily { get; init; } = "Cascadia Mono, Consolas";

    /// <summary>Text size of the SQL editor at 100 % zoom.</summary>
    public double EditorFontSize { get; init; } = 13;

    /// <summary>Text size of result grids at 100 % zoom.</summary>
    public double GridFontSize { get; init; } = 13;

    /// <summary>Run, Cancel and Save as icons in each query tab (true) or with text in the main toolbar (false).</summary>
    public bool QueryButtonsInTab { get; init; } = true;

    /// <summary>Rows fetched per page in a new query tab (DBeaver's fetch size).</summary>
    public int FetchSize { get; init; } = QueryDocumentViewModel.DefaultFetchSize;

    /// <summary>Result headers show each column's data type on a second line.</summary>
    public bool ShowColumnTypes { get; init; } = true;

    /// <summary>A text or date column chosen after WHERE, AND, SET… gets <c> = ''</c> with the caret in the quotes.</summary>
    public bool ValueSnippets { get; init; } = true;

    /// <summary>Resting the mouse on a table or column in the editor shows its details.</summary>
    public bool HoverInfo { get; init; } = true;

    /// <summary>
    /// The compact interface density (JetBrains' compact mode): lower toolbars, rows, tabs and inputs with tighter
    /// padding; text keeps its size.
    /// </summary>
    public bool CompactMode { get; init; }

    /// <summary>
    /// Draw with the GPU (smoother scrolling). Off draws in software: about 160 MB less memory, more CPU while
    /// drawing. Takes effect at the next start.
    /// </summary>
    public bool HardwareRendering { get; init; } = true;

    /// <summary>The language of the interface: "" follows Windows, "tr" or "en". Takes effect at the next start.</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>How Format SQL writes keywords.</summary>
    public SqlKeywordCase FormatKeywordCase { get; init; } = SqlKeywordCase.Upper;

    /// <summary>Spaces per indentation level of Format SQL; 0 for a tab.</summary>
    public int FormatIndent { get; init; } = 4;

    /// <summary>Format SQL puts a clause's content on the lines below it.</summary>
    public bool FormatContentOnNewLine { get; init; } = true;

    /// <summary>Format SQL starts a new line at each AND / OR of a condition.</summary>
    public bool FormatConditionsOnNewLine { get; init; } = true;

    /// <summary>The Format SQL options these settings describe.</summary>
    public SqlFormatOptions FormatOptions() => new(FormatKeywordCase, FormatIndent > 0 ? FormatIndent : 4)
    {
        UseTabs = FormatIndent == 0,
        ContentOnNewLine = FormatContentOnNewLine,
        ConditionsOnNewLine = FormatConditionsOnNewLine,
    };

    /// <summary>The values within the ranges the settings screen offers (a hand-edited file may hold anything).</summary>
    public AppearanceSettings Normalized() => this with
    {
        FormatKeywordCase = Enum.IsDefined(FormatKeywordCase) ? FormatKeywordCase : SqlKeywordCase.Upper,
        FormatIndent = Math.Clamp(FormatIndent, 0, 8),
        UiFontFamily = string.IsNullOrWhiteSpace(UiFontFamily) ? new AppearanceSettings().UiFontFamily : UiFontFamily,
        EditorFontFamily = string.IsNullOrWhiteSpace(EditorFontFamily) ? new AppearanceSettings().EditorFontFamily : EditorFontFamily,
        UiFontSize = Math.Clamp(UiFontSize, 10, 18),
        EditorFontSize = Math.Clamp(EditorFontSize, 8, 28),
        GridFontSize = Math.Clamp(GridFontSize, 10, 20),
        FetchSize = Math.Clamp(FetchSize, 1, QueryDocumentViewModel.MaxRows),
    };
}

/// <summary>Applies appearance settings to the running user interface.</summary>
public interface IAppearanceService
{
    /// <summary>The settings in effect.</summary>
    AppearanceSettings Current { get; }

    /// <summary>Puts <paramref name="settings"/> into effect at once (also used for the settings screen's preview).</summary>
    void Apply(AppearanceSettings settings);
}

/// <summary>Light or dark theme.</summary>
public interface IThemeService
{
    /// <summary>Whether the dark theme is showing.</summary>
    bool IsDark { get; }

    /// <summary>Raised after the theme changed.</summary>
    event EventHandler? Changed;

    /// <summary>Switches between light and dark.</summary>
    void Toggle();

    /// <summary>Shows the dark or the light theme and remembers the choice.</summary>
    void SetDark(bool dark);

    /// <summary>The colour scheme of the dark or the light theme (see <see cref="ColorScheme.All"/>).</summary>
    string SchemeOf(bool dark) => dark ? "Midnight" : "Paper";

    /// <summary>Chooses the colour scheme of a theme (shown at once when that theme is showing) and remembers it.</summary>
    void SetScheme(bool dark, string scheme)
    {
    }
}

/// <summary>A colour scheme of the light or the dark theme, with the colours its card shows.</summary>
/// <param name="Key">The scheme's name in UiKit (Midnight, Graphite, Paper…).</param>
/// <param name="IsDark">Whether it belongs to the dark theme.</param>
/// <param name="Window">The window colour.</param>
/// <param name="Surface">The panels' colour.</param>
/// <param name="Text">The text colour.</param>
/// <param name="Accent">The accent colour.</param>
/// <param name="Keyword">The editor's keyword colour.</param>
/// <param name="Literal">The editor's string colour.</param>
public sealed record ColorScheme(string Key, bool IsDark, string Window, string Surface, string Text, string Accent, string Keyword, string Literal)
{
    /// <summary>The scheme's name in the user's language.</summary>
    public string Name => Strings.Find("Scheme_" + (IsDark ? "Dark" : "Light") + "_" + Key) ?? Key;

    /// <summary>Every scheme; the first of each theme is its default.</summary>
    public static IReadOnlyList<ColorScheme> All { get; } =
    [
        new("Midnight", true, "#0D0F13", "#181B22", "#F3F5F9", "#4C8DFF", "#6FA8FF", "#7EE2B0"),
        new("Graphite", true, "#15171C", "#20242C", "#E7E9EE", "#5B8CFF", "#7AA2FF", "#7FD1A8"),
        new("Ocean", true, "#0B141D", "#13212D", "#E8F1F8", "#22B8F0", "#4CC8F5", "#8BE0B8"),
        new("Contrast", true, "#000000", "#121212", "#FFFFFF", "#6AA8FF", "#7DB4FF", "#8CF0BE"),
        new("Paper", false, "#F3F4F7", "#FFFFFF", "#1C2128", "#2F6FEB", "#2F5FD0", "#1F8A5B"),
        new("Sky", false, "#EAF1F8", "#FFFFFF", "#0F1B2A", "#0A63D1", "#0A5BC4", "#1F8A5B"),
        new("Sand", false, "#F3EFE8", "#FFFDF8", "#1F1A14", "#B4531B", "#A9451A", "#1F8A5B"),
        new("Contrast", false, "#FFFFFF", "#F5F5F5", "#000000", "#0047B3", "#0041A8", "#1F8A5B"),
    ];
}

/// <summary>A choice in a list of the settings screen.</summary>
public sealed record SettingChoice<T>(T Value, string Text);

/// <summary>A snippet being edited on the settings screen.</summary>
public sealed partial class SnippetItem : ObservableObject
{
    [ObservableProperty]
    public partial string Shortcut { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    public SqlSnippet ToSnippet() => new(Shortcut.Trim(), Description.Trim(), Text);

    public static SnippetItem From(SqlSnippet snippet) => new() { Shortcut = snippet.Shortcut, Description = snippet.Description, Text = snippet.Text };
}

/// <summary>A page of the settings screen.</summary>
/// <param name="Key">appearance, editor, results, query or format.</param>
/// <param name="Title">The page's name in the list.</param>
/// <param name="Description">One line under the page's title.</param>
public sealed record SettingsSection(string Key, string Title, string Description);

/// <summary>
/// The settings screen. Every change is previewed at once; Save keeps it (and writes it to the settings file),
/// Cancel goes back to what was in effect when the screen opened.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IJobQueue _jobs;
    private readonly IAppearanceService _appearance;
    private readonly IThemeService _theme;
    private readonly (string Dark, string Light) _originalSchemes;
    private readonly AppearanceSettings _original;
    private readonly bool _originalDark;
    private bool _loading;
    private readonly ISqlFormatter? _formatter;
    private readonly ISnippetService? _snippets;
    private int _previewVersion;

    /// <summary>The text of the editor preview: keywords, names, a string, a number and a comment.</summary>
    public const string EditorSample = "SELECT k.id, k.kart_no, SUM(h.tutar) AS toplam\nFROM kart k\nJOIN hareket h ON h.kart_id = k.id\n" +
        "WHERE h.tarih >= '2026-01-01' AND k.limit > 1000 -- İıŞşĞğ 0Oo1lI\nGROUP BY k.id, k.kart_no;";

    /// <summary>The query the formatting page lays out to show the options at work.</summary>
    public const string FormatSample =
        "-- Son 30 günün kart hareketleri\nwith son as (select kart_id, sum(tutar) toplam from hareket where tarih >= '2026-01-01' group by kart_id) " +
        "select k.id, k.kart_no, case when s.toplam > 1000 then 'yüksek' else 'normal' end as seviye from kart k inner join son s on s.kart_id = k.id " +
        "where k.durum = 1 and k.limit between 100 and 5000 order by s.toplam desc;";

    /// <param name="store">Where settings are saved.</param>
    /// <param name="jobs">Where saving runs.</param>
    /// <param name="appearance">Applies the preview.</param>
    /// <param name="theme">The theme.</param>
    /// <param name="fonts">The font families installed, for the font lists.</param>
    /// <param name="previewLanguage">
    /// A SQL language for the editor and formatting previews (colouring, and its formatter if it has one); null
    /// shows them plain.
    /// </param>
    /// <param name="snippets">The snippets to edit; the page is empty without.</param>
    public SettingsViewModel(
        ISettingsStore store, IJobQueue jobs, IAppearanceService appearance, IThemeService theme, IEnumerable<string> fonts, ISqlLanguageService? previewLanguage = null,
        ISnippetService? snippets = null, UpdatesViewModel? updates = null, SyncViewModel? sync = null)
    {
        _snippets = snippets;
        Updates = updates;
        Sync = sync;
        SnippetItems = [.. (snippets?.Current ?? SqlSnippets.Defaults).Select(SnippetItem.From)];
        SelectedSnippet = SnippetItems.FirstOrDefault();
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(fonts);
        _store = store;
        _jobs = jobs;
        _appearance = appearance;
        _theme = theme;
        _original = appearance.Current;
        _originalDark = theme.IsDark;
        Fonts = [.. fonts.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static f => f, StringComparer.CurrentCultureIgnoreCase)];
        Themes = [new(true, Strings.ThemeDark), new(false, Strings.ThemeLight)];
        PreviewLanguage = previewLanguage;
        _formatter = previewLanguage as ISqlFormatter;
        Sections =
        [
            new("appearance", Strings.SettingsPageAppearance, Strings.SettingsPageAppearanceInfo),
            new("editor", Strings.SettingsPageEditor, Strings.SettingsPageEditorInfo),
            new("results", Strings.SettingsPageResults, Strings.SettingsPageResultsInfo),
            new("query", Strings.SettingsPageQuery, Strings.SettingsPageQueryInfo),
            new("format", Strings.SettingsPageFormat, Strings.SettingsPageFormatInfo),
            new("snippets", Strings.SettingsPageSnippets, Strings.SettingsPageSnippetsInfo),
            .. sync is null ? Array.Empty<SettingsSection>() : [new SettingsSection("sync", Strings.SettingsPageSync, Strings.SettingsPageSyncInfo)],
            .. updates is null ? Array.Empty<SettingsSection>() : [new SettingsSection("updates", Strings.SettingsPageUpdates, Strings.SettingsPageUpdatesInfo)],
        ];
        SelectedSection = Sections[0];
        FormatPreview = FormatSample;
        UiFontFamily = string.Empty;
        EditorFontFamily = string.Empty;
        Show(_original);
        _originalSchemes = (theme.SchemeOf(true), theme.SchemeOf(false));
        SelectedTheme = Themes.First(t => t.Value == _originalDark);
    }

    /// <summary>The sync page (shared folder, on or off, sync now); null when the host has no sync.</summary>
    public SyncViewModel? Sync { get; }

    /// <summary>The updates page (channel, source, start-up check); null when the host has no updater.</summary>
    public UpdatesViewModel? Updates { get; }

    /// <summary>Raised when the screen should close; true when the settings were saved.</summary>
    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<string> Fonts { get; }

    public IReadOnlyList<double> UiSizes { get; } = [11, 12, 13, 14, 15, 16];

    public IReadOnlyList<double> EditorSizes { get; } = [9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24];

    public IReadOnlyList<double> GridSizes { get; } = [11, 12, 13, 14, 15, 16, 18];

    public ObservableCollection<SettingChoice<bool>> Themes { get; }

    /// <summary>The colour schemes of the theme chosen above.</summary>
    public ObservableCollection<ColorScheme> Schemes { get; } = [];

    /// <summary>The scheme of the chosen theme; applied at once, as the theme is.</summary>
    [ObservableProperty]
    public partial ColorScheme? SelectedScheme { get; set; }

    private bool _fillingSchemes;

    partial void OnSelectedSchemeChanged(ColorScheme? value)
    {
        if (value is not null && !_fillingSchemes)
        {
            _theme.SetScheme(value.IsDark, value.Key);
        }
    }

    private void FillSchemes(bool dark)
    {
        _fillingSchemes = true;
        Schemes.Clear();
        foreach (var scheme in ColorScheme.All.Where(s => s.IsDark == dark))
        {
            Schemes.Add(scheme);
        }

        SelectedScheme = Schemes.FirstOrDefault(s => s.Key == _theme.SchemeOf(dark)) ?? Schemes.FirstOrDefault();
        _fillingSchemes = false;
    }

    [ObservableProperty]
    public partial SettingChoice<bool>? SelectedTheme { get; set; }

    [ObservableProperty]
    public partial string UiFontFamily { get; set; }

    [ObservableProperty]
    public partial double UiFontSize { get; set; }

    [ObservableProperty]
    public partial string EditorFontFamily { get; set; }

    [ObservableProperty]
    public partial double EditorFontSize { get; set; }

    [ObservableProperty]
    public partial double GridFontSize { get; set; }

    public IReadOnlyList<SettingChoice<bool>> QueryButtonPlaces { get; } =
        [new(true, Strings.QueryButtonsInTab), new(false, Strings.QueryButtonsInToolbar)];

    /// <summary>Normal or compact interface density.</summary>
    public IReadOnlyList<SettingChoice<bool>> Densities { get; } =
        [new(false, Strings.DensityNormal), new(true, Strings.DensityCompact)];

    [ObservableProperty]
    public partial SettingChoice<bool>? Density { get; set; }

    partial void OnDensityChanged(SettingChoice<bool>? value) => Preview();

    /// <summary>Draw with the GPU; applies at the next start.</summary>
    [ObservableProperty]
    public partial bool HardwareRendering { get; set; } = true;

    /// <summary>The interface languages: Windows' own, Turkish, English (each named in itself).</summary>
    public IReadOnlyList<SettingChoice<string>> Languages { get; } =
        [new(string.Empty, Strings.LanguageSystem), new("tr", "Türkçe"), new("en", "English")];

    /// <summary>The interface language; applies at the next start.</summary>
    [ObservableProperty]
    public partial SettingChoice<string>? Language { get; set; }

    [ObservableProperty]
    public partial SettingChoice<bool>? QueryButtonPlace { get; set; }

    public IReadOnlyList<int> FetchSizes { get; } = [50, 100, 200, 500, 1000, 5000, 10000];

    /// <summary>The pages of the screen, in the order listed.</summary>
    public IReadOnlyList<SettingsSection> Sections { get; }

    /// <summary>The page shown.</summary>
    [ObservableProperty]
    public partial SettingsSection SelectedSection { get; set; }

    /// <summary>The snippets, as edited.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SnippetItem> SnippetItems { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSnippetCommand))]
    public partial SnippetItem? SelectedSnippet { get; set; }

    /// <summary>Why the snippets cannot be saved; null when they can.</summary>
    [ObservableProperty]
    public partial string? SnippetError { get; set; }

    [RelayCommand]
    private void AddSnippet()
    {
        var number = 1;
        while (SnippetItems.Any(s => s.Shortcut.Equals("snippet" + number.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)))
        {
            number++;
        }

        var item = new SnippetItem { Shortcut = "snippet" + number.ToString(System.Globalization.CultureInfo.InvariantCulture), Text = "$0" };
        SnippetItems.Add(item);
        SelectedSnippet = item;
    }

    private bool HasSnippet() => SelectedSnippet is not null;

    [RelayCommand(CanExecute = nameof(HasSnippet))]
    private void RemoveSnippet()
    {
        if (SelectedSnippet is { } item)
        {
            var index = SnippetItems.IndexOf(item);
            SnippetItems.Remove(item);
            SelectedSnippet = SnippetItems.Count == 0 ? null : SnippetItems[Math.Min(index, SnippetItems.Count - 1)];
        }
    }

    /// <summary>The snippets as edited when every shortcut is a word used once; null otherwise.</summary>
    private List<SqlSnippet>? ValidSnippets()
    {
        var snippets = SnippetItems.Select(static s => s.ToSnippet()).ToList();
        var valid = snippets.All(static s => SqlSnippets.IsValidShortcut(s.Shortcut)) &&
            snippets.Select(static s => s.Shortcut).Distinct(StringComparer.OrdinalIgnoreCase).Count() == snippets.Count;
        return valid ? snippets : null;
    }

    /// <summary>Colours the previews; null shows them plain.</summary>
    public ISqlLanguageService? PreviewLanguage { get; }

    /// <summary>Whether the formatting preview shows the options at work (a formatter is at hand).</summary>
    public bool CanPreviewFormat => _formatter is not null;

    public IReadOnlyList<SettingChoice<SqlKeywordCase>> KeywordCases { get; } =
        [new(SqlKeywordCase.Upper, "SELECT"), new(SqlKeywordCase.Lower, "select"), new(SqlKeywordCase.AsWritten, Strings.FormatCaseAsWritten)];

    [ObservableProperty]
    public partial SettingChoice<SqlKeywordCase>? FormatKeywordCase { get; set; }

    public IReadOnlyList<SettingChoice<int>> Indents { get; } = [new(2, "2"), new(4, "4"), new(8, "8"), new(0, Strings.FormatIndentTab)];

    [ObservableProperty]
    public partial SettingChoice<int>? FormatIndent { get; set; }

    [ObservableProperty]
    public partial bool FormatContentOnNewLine { get; set; }

    [ObservableProperty]
    public partial bool FormatConditionsOnNewLine { get; set; }

    /// <summary><see cref="FormatSample"/> laid out with the options chosen.</summary>
    [ObservableProperty]
    public partial string FormatPreview { get; set; }

    partial void OnFormatKeywordCaseChanged(SettingChoice<SqlKeywordCase>? value) => Preview();

    partial void OnFormatIndentChanged(SettingChoice<int>? value) => Preview();

    partial void OnFormatContentOnNewLineChanged(bool value) => Preview();

    partial void OnFormatConditionsOnNewLineChanged(bool value) => Preview();

    /// <summary>Lays the sample out again in the background; only the newest result is shown.</summary>
    private async Task RefreshFormatPreviewAsync(SqlFormatOptions options)
    {
        if (_formatter is not { } formatter)
        {
            return;
        }

        var version = ++_previewVersion;
        var text = await _jobs.Enqueue("Format preview", _ => Task.FromResult(formatter.Format(FormatSample, options)), JobPriority.High).Completion;
        if (version == _previewVersion)
        {
            FormatPreview = text;
        }
    }

    [ObservableProperty]
    public partial int FetchSize { get; set; }

    [ObservableProperty]
    public partial bool ShowColumnTypes { get; set; }

    partial void OnShowColumnTypesChanged(bool value) => Preview();

    [ObservableProperty]
    public partial bool ValueSnippets { get; set; }

    partial void OnValueSnippetsChanged(bool value) => Preview();

    [ObservableProperty]
    public partial bool HoverInfo { get; set; }

    partial void OnHoverInfoChanged(bool value) => Preview();

    partial void OnQueryButtonPlaceChanged(SettingChoice<bool>? value) => Preview();

    partial void OnFetchSizeChanged(int value) => Preview();

    /// <summary>The settings as currently chosen on the screen.</summary>
    public AppearanceSettings Chosen => new AppearanceSettings
    {
        UiFontFamily = UiFontFamily,
        UiFontSize = UiFontSize,
        EditorFontFamily = EditorFontFamily,
        EditorFontSize = EditorFontSize,
        GridFontSize = GridFontSize,
        QueryButtonsInTab = QueryButtonPlace?.Value ?? true,
        CompactMode = Density?.Value ?? false,
        HardwareRendering = HardwareRendering,
        Language = Language?.Value ?? string.Empty,
        FetchSize = FetchSize,
        ShowColumnTypes = ShowColumnTypes,
        ValueSnippets = ValueSnippets,
        HoverInfo = HoverInfo,
        FormatKeywordCase = FormatKeywordCase?.Value ?? SqlKeywordCase.Upper,
        FormatIndent = FormatIndent?.Value ?? 4,
        FormatContentOnNewLine = FormatContentOnNewLine,
        FormatConditionsOnNewLine = FormatConditionsOnNewLine,
    }.Normalized();

    partial void OnUiFontFamilyChanged(string value) => Preview();

    partial void OnUiFontSizeChanged(double value) => Preview();

    partial void OnEditorFontFamilyChanged(string value) => Preview();

    partial void OnEditorFontSizeChanged(double value) => Preview();

    partial void OnGridFontSizeChanged(double value) => Preview();

    partial void OnSelectedThemeChanged(SettingChoice<bool>? value)
    {
        if (value is not null && value.Value != _theme.IsDark)
        {
            _theme.SetDark(value.Value);
        }

        if (value is not null)
        {
            FillSchemes(value.Value);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        // Snippets with a missing or repeated shortcut are shown, not saved.
        if (ValidSnippets() is not { } snippets)
        {
            SnippetError = Strings.SnippetInvalid;
            SelectedSection = Sections.Single(static s => s.Key == "snippets");
            return;
        }

        SnippetError = null;
        var settings = Chosen;
        _appearance.Apply(settings);
        _snippets?.Apply(snippets);
        await _jobs.Enqueue("Save settings", context => _store.SaveAsync(AppearanceSettings.Section, settings, context.CancellationToken), JobPriority.High).Completion;
        if (_snippets is not null)
        {
            var section = new SnippetSettings { Snippets = snippets };
            await _jobs.Enqueue("Save snippets", context => _store.SaveAsync(SnippetSettings.Section, section, context.CancellationToken), JobPriority.High).Completion;
        }

        if (Updates is not null)
        {
            await Updates.SaveAsync();
        }

        if (Sync is not null)
        {
            await Sync.SaveAsync();
            _ = Sync.SyncIfOnAsync();
        }

        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel()
    {
        _appearance.Apply(_original);
        if (_theme.SchemeOf(true) != _originalSchemes.Dark)
        {
            _theme.SetScheme(true, _originalSchemes.Dark);
        }

        if (_theme.SchemeOf(false) != _originalSchemes.Light)
        {
            _theme.SetScheme(false, _originalSchemes.Light);
        }

        if (_theme.IsDark != _originalDark)
        {
            _theme.SetDark(_originalDark);
        }

        // The update settings go back to what is saved.
        _ = Updates?.LoadAsync();
        _ = Sync?.LoadAsync();
        CloseRequested?.Invoke(this, false);
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        Show(new AppearanceSettings());
        SnippetItems.Clear();
        foreach (var snippet in SqlSnippets.Defaults)
        {
            SnippetItems.Add(SnippetItem.From(snippet));
        }

        SelectedSnippet = SnippetItems.FirstOrDefault();
    }

    private void Show(AppearanceSettings settings)
    {
        _loading = true;
        try
        {
            UiFontFamily = settings.UiFontFamily;
            UiFontSize = settings.UiFontSize;
            EditorFontFamily = settings.EditorFontFamily;
            EditorFontSize = settings.EditorFontSize;
            GridFontSize = settings.GridFontSize;
            QueryButtonPlace = QueryButtonPlaces.First(p => p.Value == settings.QueryButtonsInTab);
            Density = Densities.First(d => d.Value == settings.CompactMode);
            HardwareRendering = settings.HardwareRendering;
            Language = Languages.FirstOrDefault(l => string.Equals(l.Value, settings.Language, StringComparison.OrdinalIgnoreCase)) ?? Languages[0];
            FetchSize = settings.FetchSize;
            ShowColumnTypes = settings.ShowColumnTypes;
            ValueSnippets = settings.ValueSnippets;
            HoverInfo = settings.HoverInfo;
            FormatKeywordCase = KeywordCases.FirstOrDefault(c => c.Value == settings.FormatKeywordCase) ?? KeywordCases[0];
            FormatIndent = Indents.FirstOrDefault(c => c.Value == settings.FormatIndent) ?? Indents[1];
            FormatContentOnNewLine = settings.FormatContentOnNewLine;
            FormatConditionsOnNewLine = settings.FormatConditionsOnNewLine;
        }
        finally
        {
            _loading = false;
        }

        Preview();
    }

    private void Preview()
    {
        if (!_loading)
        {
            var chosen = Chosen;
            _appearance.Apply(chosen);
            _ = RefreshFormatPreviewAsync(chosen.FormatOptions());
        }
    }
}
