using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using JetQueries.Presentation.ViewModels;
using JetQueries.Sdk;

namespace JetQueries.Wpf.Views;

/// <summary>A connection's health check in a tab: counts by severity, the findings, and the chosen one explained.</summary>
public partial class HealthCheckView : UserControl
{
    public HealthCheckView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is HealthCheckViewModel health)
            {
                ScriptView.LanguageService = health.LanguageService;
            }
        };
    }

    /// <summary>The findings list, for tests.</summary>
    internal ListBox List => FindingsList;
}

/// <summary>The icon of a severity (it always goes with its word).</summary>
public sealed class HealthIconConverter : IValueConverter
{
    public static HealthIconConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        HealthSeverity.Critical => "",
        HealthSeverity.Warning => "",
        HealthSeverity.Info => "",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The status brush of a severity: danger, warning, accent, success.</summary>
public sealed class HealthBrushConverter : IValueConverter
{
    public static HealthBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Application.Current.TryFindResource(value switch
    {
        HealthSeverity.Critical => "Brush.Danger",
        HealthSeverity.Warning => "Brush.Warning",
        HealthSeverity.Info => "Brush.Accent",
        _ => "Brush.Success",
    });

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Collapsed for an empty text.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public static EmptyToCollapsedConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
