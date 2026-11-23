using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Jobs;
using JetQueries.Core.Settings;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.Services;

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

    /// <summary>The values within the ranges the settings screen offers (a hand-edited file may hold anything).</summary>
    public AppearanceSettings Normalized() => this with
    {
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
}

/// <summary>A choice in a list of the settings screen.</summary>
public sealed record SettingChoice<T>(T Value, string Text);

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
    private readonly AppearanceSettings _original;
    private readonly bool _originalDark;
    private bool _loading;

    /// <param name="store">Where settings are saved.</param>
    /// <param name="jobs">Where saving runs.</param>
    /// <param name="appearance">Applies the preview.</param>
    /// <param name="theme">The theme.</param>
    /// <param name="fonts">The font families installed, for the font lists.</param>
    public SettingsViewModel(ISettingsStore store, IJobQueue jobs, IAppearanceService appearance, IThemeService theme, IEnumerable<string> fonts)
    {
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
        UiFontFamily = string.Empty;
        EditorFontFamily = string.Empty;
        Show(_original);
        SelectedTheme = Themes.First(t => t.Value == _originalDark);
    }

    /// <summary>Raised when the screen should close; true when the settings were saved.</summary>
    public event EventHandler<bool>? CloseRequested;

    public IReadOnlyList<string> Fonts { get; }

    public IReadOnlyList<double> UiSizes { get; } = [11, 12, 13, 14, 15, 16];

    public IReadOnlyList<double> EditorSizes { get; } = [9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24];

    public IReadOnlyList<double> GridSizes { get; } = [11, 12, 13, 14, 15, 16, 18];

    public ObservableCollection<SettingChoice<bool>> Themes { get; }

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

    [ObservableProperty]
    public partial SettingChoice<bool>? QueryButtonPlace { get; set; }

    public IReadOnlyList<int> FetchSizes { get; } = [50, 100, 200, 500, 1000, 5000, 10000];

    [ObservableProperty]
    public partial int FetchSize { get; set; }

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
        FetchSize = FetchSize,
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
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var settings = Chosen;
        _appearance.Apply(settings);
        await _jobs.Enqueue("Save settings", context => _store.SaveAsync(AppearanceSettings.Section, settings, context.CancellationToken), JobPriority.High).Completion;
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel()
    {
        _appearance.Apply(_original);
        if (_theme.IsDark != _originalDark)
        {
            _theme.SetDark(_originalDark);
        }

        CloseRequested?.Invoke(this, false);
    }

    [RelayCommand]
    private void ResetDefaults() => Show(new AppearanceSettings());

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
            FetchSize = settings.FetchSize;
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
            _appearance.Apply(Chosen);
        }
    }
}
