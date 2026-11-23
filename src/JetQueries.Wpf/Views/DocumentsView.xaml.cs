using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace JetQueries.Wpf.Views;

public partial class DocumentsView : UserControl
{
    public DocumentsView() => InitializeComponent();

    private void OnTabMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Middle && sender is TabItem { DataContext: QueryDocumentViewModel document } &&
            DataContext is DocumentsViewModel documents)
        {
            documents.CloseCommand.Execute(document);
            e.Handled = true;
        }
    }
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
