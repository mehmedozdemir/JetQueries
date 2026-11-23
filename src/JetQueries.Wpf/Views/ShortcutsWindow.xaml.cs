using System.Windows;
using System.Windows.Input;
using UiKit.Theming;

namespace JetQueries.Wpf.Views;

/// <summary>The keyboard shortcuts overview (Tools, Keyboard shortcuts, F1).</summary>
public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow(KeyboardShortcutsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += (_, _) => Search.Focus();
    }

    private void OnClose(object sender, ExecutedRoutedEventArgs e) => Close();
}
