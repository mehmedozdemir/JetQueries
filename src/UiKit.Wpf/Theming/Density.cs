using System.Windows;

namespace UiKit.Theming;

/// <summary>How much room controls take: the usual sizes, or one step smaller (JetBrains' compact mode).</summary>
public enum UiDensity
{
    /// <summary>The design's sizes.</summary>
    Normal,

    /// <summary>Lower controls, rows and tabs with tighter padding; text keeps its size.</summary>
    Compact,
}

/// <summary>
/// Switches UiKit's size tokens (<c>Size.*</c>, <c>Pad.*</c>, <c>Space.*</c>) between the normal and the compact
/// density at runtime. Styles read them as DynamicResource, so open windows follow at once. Text sizes are not
/// touched: compact means less padding, not smaller type.
/// </summary>
public static class Density
{
    /// <summary>The density in effect.</summary>
    public static UiDensity Current { get; private set; } = UiDensity.Normal;

    /// <summary>Raised after the density changed.</summary>
    public static event Action? Changed;

    /// <summary>Puts a density into effect for the whole application.</summary>
    public static void Apply(UiDensity density)
    {
        var compact = density == UiDensity.Compact;
        var resources = Application.Current.Resources;
        resources["Size.Control"] = compact ? 28.0 : 34.0;
        resources["Size.Input"] = compact ? 26.0 : 32.0;
        resources["Size.Toolbar.Button"] = compact ? 28.0 : 34.0;
        resources["Size.StatusBar"] = compact ? 26.0 : 32.0;
        resources["Pad.Button"] = compact ? new Thickness(8, 3, 8, 3) : new Thickness(10, 6, 10, 6);
        resources["Pad.Button.Primary"] = compact ? new Thickness(12, 4, 12, 4) : new Thickness(16, 7, 16, 7);
        resources["Pad.Button.Secondary"] = compact ? new Thickness(10, 4, 10, 4) : new Thickness(14, 7, 14, 7);
        resources["Pad.Input"] = compact ? new Thickness(6, 3, 6, 3) : new Thickness(8, 6, 8, 6);
        resources["Pad.Item"] = compact ? new Thickness(10, 3, 10, 3) : new Thickness(10, 6, 10, 6);
        resources["Pad.ListItem"] = compact ? new Thickness(6, 2, 6, 2) : new Thickness(6, 4, 6, 4);
        resources["Pad.MenuItem"] = compact ? new Thickness(8, 3, 8, 3) : new Thickness(8, 6, 8, 6);
        resources["Pad.Tab"] = compact ? new Thickness(10, 5, 10, 5) : new Thickness(12, 9, 12, 9);
        resources["Pad.TreeItem"] = compact ? new Thickness(2, 2, 8, 2) : new Thickness(2, 4, 8, 4);
        resources["Space.Toolbar"] = compact ? new Thickness(10, 5, 10, 5) : new Thickness(12, 9, 12, 9);
        resources["Space.Card"] = compact ? new Thickness(10) : new Thickness(14);
        Current = density;
        Changed?.Invoke();
    }
}
