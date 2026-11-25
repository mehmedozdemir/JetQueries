using System.Globalization;
using System.Windows;
using System.Windows.Data;
using JetQueries.Core.Connections;
using JetQueries.Sdk;
using UiKit.Controls;

namespace JetQueries.Wpf.Views;

/// <summary>A zoom factor as the user's percentage text: %110 in Turkish, 110% in English.</summary>
public sealed class ZoomTextConverter : IValueConverter
{
    public static ZoomTextConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double zoom ? Text(zoom) : string.Empty;

    public static string Text(double zoom) =>
        string.Format(CultureInfo.CurrentCulture, System.Text.CompositeFormat.Parse(Strings.ZoomPercent), Math.Round(zoom * 100));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public static InverseBoolToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>null → Visible, anything else → Collapsed.</summary>
public sealed class NullToVisibleConverter : IValueConverter
{
    public static NullToVisibleConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A non-null value → Visible.</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public static NotNullToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Shows a menu item for one kind of explorer node.</summary>
public sealed class NodeTypeToVisibilityConverter : IValueConverter
{
    public static NodeTypeToVisibilityConverter Instance { get; } = new();

    /// <summary>Visible when the node is of the kind named by the parameter: Key or KeySpace.</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (parameter as string, value) switch
    {
        ("Key", KeyNodeViewModel) or ("KeySpace", KeySpaceNodeViewModel) => Visibility.Visible,
        _ => Visibility.Collapsed,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The UiKit icon of an explorer node.</summary>
public sealed class ExplorerIconConverter : IValueConverter
{
    private const string Folder = "";
    private const string Schema = "";
    private const string View = "";
    private const string Group = "";
    private const string Function = "";

    public static ExplorerIconConverter Instance { get; } = new();

    /// <summary>The folder icon, which a folder's template draws itself (in the folder's colour).</summary>
    public const string FolderGlyph = Folder;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        FolderNodeViewModel => null,
        GroupNodeViewModel => null,
        // A connection shows the mark of its database type instead (in its template).
        ConnectionNodeViewModel => null,
        CategoryNodeViewModel => Group,
        SystemGroupNodeViewModel => Folder,
        FavoritesNodeViewModel => "\uE735",
        ColumnNodeViewModel { IsPrimaryKey: true } => Icons.Key,
        ColumnNodeViewModel => Icons.Column,
        DatabaseObjectNodeViewModel node => node.Target.Kind switch
        {
            DatabaseObjectKinds.Database => Icons.Database,
            DatabaseObjectKinds.Schema => Schema,
            DatabaseObjectKinds.Table => Icons.Table,
            DatabaseObjectKinds.View => View,
            DatabaseObjectKinds.Procedure or DatabaseObjectKinds.Function or DatabaseObjectKinds.Package or DatabaseObjectKinds.PackageBody
                or DatabaseObjectKinds.Trigger => Function,
            _ => Icons.Code,
        },
        MessageNodeViewModel { IsError: true } => Icons.Error,
        KeySpaceNodeViewModel => Icons.Database,
        KeyFolderNodeViewModel => Folder,
        KeyNodeViewModel => Icons.Key,
        _ => null,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The brush resource key of a connection's environment colour; null for none.</summary>
public sealed class EnvironmentBrushConverter : IValueConverter
{
    public static EnvironmentBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ConnectionEnvironment environment
            ? environment switch
            {
                ConnectionEnvironment.Development => Application.Current.TryFindResource("Brush.Success"),
                ConnectionEnvironment.Test => Application.Current.TryFindResource("Brush.Warning"),
                ConnectionEnvironment.Production => Application.Current.TryFindResource("Brush.Danger"),
                _ => Application.Current.TryFindResource("Brush.TextMuted"),
            }
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when every value is true.</summary>
public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
{
    public static readonly AllTrueToVisibilityConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.All(static v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The opposite of a boolean.</summary>
public sealed class NotConverter : IValueConverter
{
    public static readonly NotConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Visible away from 100 % zoom.</summary>
public sealed class ZoomBadgeVisibilityConverter : IValueConverter
{
    public static ZoomBadgeVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double zoom && Math.Abs(zoom - 1) > 0.001 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible for a folder node.</summary>
public sealed class IsFolderToVisibilityConverter : IValueConverter
{
    public static IsFolderToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FolderNodeViewModel ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when an enum value equals the parameter; checking sets the value to the parameter (radio buttons).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static EnumEqualsConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Equals(value, parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? parameter : Binding.DoNothing;
}

/// <summary>Visible when the value is a result set (not the Messages tab).</summary>
public sealed class IsResultSetToVisibilityConverter : IValueConverter
{
    public static IsResultSetToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is QueryResultSet ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible for a connection node.</summary>
public sealed class IsConnectionToVisibilityConverter : IValueConverter
{
    public static IsConnectionToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ConnectionNodeViewModel ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible for a folder or a connection node (where a new folder can be made).</summary>
public sealed class IsFolderOrConnectionToVisibilityConverter : IValueConverter
{
    public static IsFolderOrConnectionToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FolderNodeViewModel or ConnectionNodeViewModel ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The frame of a query in its connection's environment colour; transparent for an unmarked connection.</summary>
public sealed class EnvironmentFrameConverter : IValueConverter
{
    public static EnvironmentFrameConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ConnectionEnvironment.Development or ConnectionEnvironment.Test or ConnectionEnvironment.Production =>
            EnvironmentBrushConverter.Instance.Convert(value, targetType, parameter, culture),
        _ => System.Windows.Media.Brushes.Transparent,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A run shortcut shown in the empty results area.</summary>
public sealed record RunHint(string Keys, string Text)
{
    public static IReadOnlyList<RunHint> All =>
    [
        new("F5", Strings.EditorRun),
        new("Ctrl+Enter", Strings.EditorRunStatement),
        new("Ctrl+Shift+Enter", Strings.EditorRunInNewTab),
    ];
}

/// <summary>Visible when the value's text equals the parameter (an enum member's name, for example).</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public static EqualsToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A #RRGGBB text as a brush (folder colours); transparent when it is not one.</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public static HexBrushConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string text && FolderColors.IsColor(text))
        {
            var rgb = uint.Parse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brush.Freeze();
            return brush;
        }

        return System.Windows.Media.Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
