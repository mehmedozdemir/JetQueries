using System.Globalization;
using System.Windows;
using System.Windows.Data;
using JetQueries.Core.Connections;
using JetQueries.Sdk;
using UiKit.Controls;

namespace JetQueries.Wpf.Views;

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public static InverseBoolToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

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

/// <summary>The UiKit icon of an explorer node.</summary>
public sealed class ExplorerIconConverter : IValueConverter
{
    private const string Folder = "";
    private const string Schema = "";
    private const string View = "";
    private const string Group = "";
    private const string Function = "";

    public static ExplorerIconConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        FolderNodeViewModel => Folder,
        ConnectionNodeViewModel => Icons.Database,
        CategoryNodeViewModel => Group,
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
