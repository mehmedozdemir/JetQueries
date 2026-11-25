using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace JetQueries.Wpf.Views;

public partial class DocumentsView : UserControl
{
    public DocumentsView() => InitializeComponent();

    /// <summary>The tab menu's "Copy path" (a command: a Click handler there was mistyped by the markup compiler).</summary>
    public static readonly System.Windows.Input.RoutedUICommand CopyPathCommand = new("Copy path", nameof(CopyPathCommand), typeof(DocumentsView));

    private void OnCopyPath(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is QueryDocumentViewModel { FilePath: { } path })
        {
            Clipboard.SetText(path);
        }
    }

    private void OnCanCopyPath(object sender, System.Windows.Input.CanExecuteRoutedEventArgs e) =>
        e.CanExecute = e.Parameter is QueryDocumentViewModel { FilePath: not null };

    private void OnTabMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Middle && sender is TabItem { DataContext: { } document } &&
            DataContext is DocumentsViewModel documents)
        {
            documents.CloseTabCommand.Execute(document);
            e.Handled = true;
        }
    }
}

/// <summary>False → Visible, true → Collapsed.</summary>
public sealed class FalseToVisibleConverter : IValueConverter
{
    public static FalseToVisibleConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is false ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when the value is not null.</summary>
public sealed class NotNullConverter : IValueConverter
{
    public static NotNullConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0 → Visible (an empty state), anything else → Collapsed.</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public static ZeroToVisibleConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0 → Collapsed, anything else → Visible.</summary>
public sealed class ZeroToCollapsedConverter : IValueConverter
{
    public static ZeroToCollapsedConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The view of a document tab: the query editor or an object's details.</summary>
public sealed class DocumentTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Query { get; set; }

    public DataTemplate? Details { get; set; }

    public DataTemplate? Key { get; set; }

    public DataTemplate? Activity { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        ObjectDetailsViewModel => Details,
        KeyValueViewModel => Key,
        ActivityMonitorViewModel => Activity,
        _ => Query,
    };
}

/// <summary>The icon of a document tab.</summary>
public sealed class DocumentIconConverter : IValueConverter
{
    public static DocumentIconConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ObjectDetailsViewModel { Target.Kind: Sdk.DatabaseObjectKinds.Table or Sdk.DatabaseObjectKinds.View } => UiKit.Controls.Icons.Table,
        ObjectDetailsViewModel => UiKit.Controls.Icons.Info,
        KeyValueViewModel => UiKit.Controls.Icons.Key,
        ActivityMonitorViewModel => "",
        _ => UiKit.Controls.Icons.Code,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
