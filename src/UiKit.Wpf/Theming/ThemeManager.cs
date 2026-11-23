using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace UiKit.Theming;

public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>
/// Swaps the UiKit color dictionary at runtime, persists the choice and keeps the native
/// Windows title bar (dark mode + caption color) in sync with the theme.
/// </summary>
public static class ThemeManager
{
    private static string? _settingsPath;

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static event Action? ThemeChanged;

    /// <summary>Call once at startup, after the application resources are loaded.</summary>
    /// <param name="appName">Settings folder name under %AppData%.</param>
    /// <param name="defaultTheme">Theme when nothing is saved; <c>null</c> follows the Windows setting.</param>
    public static void Initialize(string appName, AppTheme? defaultTheme = null)
    {
        _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appName, "theme.json");
        var theme = LoadSavedTheme() ?? defaultTheme ?? (SystemUsesLightTheme() ? AppTheme.Light : AppTheme.Dark);
        Apply(theme, save: false);
    }

    public static void Toggle() => Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);

    public static void Apply(AppTheme theme, bool save = true)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var colors = new ResourceDictionary { Source = new Uri($"pack://application:,,,/UiKit.Wpf;component/Themes/{theme}.xaml") };

        int index = -1;
        for (int i = 0; i < dictionaries.Count; i++)
        {
            string? source = dictionaries[i].Source?.OriginalString;
            if (source is not null && (source.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                                       source.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                index = i;
                break;
            }
        }
        if (index >= 0) dictionaries[index] = colors;
        else dictionaries.Insert(0, colors);

        Current = theme;
        foreach (Window window in Application.Current.Windows)
            ApplyTitleBar(window);
        if (save) SaveTheme(theme);
        ThemeChanged?.Invoke();
    }

    /// <summary>Applies dark mode and the caption color to the native title bar. Call from Window.SourceInitialized.</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int dark = Current == AppTheme.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        if (Application.Current.TryFindResource("Color.Window") is Color c)
        {
            int colorRef = c.R | (c.G << 8) | (c.B << 16);
            _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
        }
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static AppTheme? LoadSavedTheme()
    {
        try
        {
            if (_settingsPath is null || !File.Exists(_settingsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
            return doc.RootElement.TryGetProperty("theme", out var t) && Enum.TryParse<AppTheme>(t.GetString(), out var theme) ? theme : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void SaveTheme(AppTheme theme)
    {
        if (_settingsPath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new { theme = theme.ToString() }));
        }
        catch (Exception)
        {
            // Persisting the theme is optional.
        }
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
