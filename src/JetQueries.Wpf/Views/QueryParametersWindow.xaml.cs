using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using JetQueries.Presentation.ViewModels;
using UiKit.Theming;

namespace JetQueries.Wpf.Views;

/// <summary>
/// Asks for the values of a parameterized query before it runs: a line per placeholder (<c>:name</c> with how its
/// value is sent, <c>${name}</c> with the text put into the SQL). Run checks the values and closes.
/// </summary>
public partial class QueryParametersWindow : Window
{
    private readonly QueryParametersViewModel _viewModel;

    public QueryParametersWindow(QueryParametersViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        // The first value is ready to be typed.
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (FindFirst<TextBox>(Rows) is { } first)
            {
                first.Focus();
                first.SelectAll();
            }
        });
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TryBuild(out _, out _))
        {
            DialogResult = true;
        }
    }

    private static T? FindFirst<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                return found;
            }

            if (FindFirst<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
