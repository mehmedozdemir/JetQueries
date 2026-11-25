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
/// Sets UiKit's size tokens (<c>Size.*</c>, <c>Pad.*</c>, <c>Space.*</c>) at runtime, on a scale of five levels:
/// -2 very compact, -1 compact, 0 normal, 1 comfortable, 2 spacious. Styles read them as DynamicResource, so open
/// windows follow at once. Text sizes are not touched: density is padding and height, not type.
/// </summary>
public static class Density
{
    /// <summary>The lowest level (very compact).</summary>
    public const int MinLevel = -2;

    /// <summary>The highest level (spacious).</summary>
    public const int MaxLevel = 2;

    /// <summary>The density in effect: compact for any level below normal.</summary>
    public static UiDensity Current => Level < 0 ? UiDensity.Compact : UiDensity.Normal;

    /// <summary>The level in effect, -2 to 2.</summary>
    public static int Level { get; private set; }

    /// <summary>Raised after the density changed.</summary>
    public static event Action? Changed;

    /// <summary>
    /// How far a level moves a size from normal, in steps of "normal minus compact": compact is exactly one step
    /// down; the steps up are a little smaller, so spacious stays reasonable.
    /// </summary>
    public static double Step(int level) => Math.Clamp(level, MinLevel, MaxLevel) switch
    {
        -2 => -1.6,
        -1 => -1,
        1 => 0.7,
        2 => 1.4,
        _ => 0,
    };

    /// <summary>A size at a level, from its normal and its compact value.</summary>
    public static double Size(double normal, double compact, int level) => Math.Max(0, Math.Round((normal + Step(level) * (normal - compact)) * 2) / 2);

    /// <summary>A padding at a level, from its normal and its compact value.</summary>
    public static Thickness Pad(Thickness normal, Thickness compact, int level) => new(
        Size(normal.Left, compact.Left, level), Size(normal.Top, compact.Top, level), Size(normal.Right, compact.Right, level), Size(normal.Bottom, compact.Bottom, level));

    /// <summary>Puts a density into effect for the whole application (normal is level 0, compact level -1).</summary>
    public static void Apply(UiDensity density) => Apply(density == UiDensity.Compact ? -1 : 0);

    /// <summary>Puts a density level (-2 to 2) into effect for the whole application.</summary>
    public static void Apply(int level)
    {
        level = Math.Clamp(level, MinLevel, MaxLevel);
        var resources = Application.Current.Resources;
        resources["Size.Control"] = Size(34, 28, level);
        resources["Size.Input"] = Size(32, 26, level);
        resources["Size.Toolbar.Button"] = Size(34, 28, level);
        resources["Size.StatusBar"] = Size(32, 26, level);
        resources["Pad.Button"] = Pad(new Thickness(10, 6, 10, 6), new Thickness(8, 3, 8, 3), level);
        resources["Pad.Button.Primary"] = Pad(new Thickness(16, 7, 16, 7), new Thickness(12, 4, 12, 4), level);
        resources["Pad.Button.Secondary"] = Pad(new Thickness(14, 7, 14, 7), new Thickness(10, 4, 10, 4), level);
        resources["Pad.Input"] = Pad(new Thickness(8, 6, 8, 6), new Thickness(6, 3, 6, 3), level);
        resources["Pad.Item"] = Pad(new Thickness(10, 6, 10, 6), new Thickness(10, 3, 10, 3), level);
        resources["Pad.ListItem"] = Pad(new Thickness(6, 4, 6, 4), new Thickness(6, 2, 6, 2), level);
        resources["Pad.MenuItem"] = Pad(new Thickness(8, 6, 8, 6), new Thickness(8, 3, 8, 3), level);
        resources["Pad.Tab"] = Pad(new Thickness(12, 9, 12, 9), new Thickness(10, 5, 10, 5), level);
        resources["Pad.TreeItem"] = Pad(new Thickness(2, 4, 8, 4), new Thickness(2, 2, 8, 2), level);
        resources["Space.Toolbar"] = Pad(new Thickness(12, 9, 12, 9), new Thickness(10, 5, 10, 5), level);
        resources["Space.Card"] = Pad(new Thickness(14), new Thickness(10), level);
        Level = level;
        Changed?.Invoke();
    }
}
