using System.Globalization;
using System.Text;
using System.Windows;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.ViewModels;
using UiKit.Theming;

namespace JetQueries.Wpf.Views;

/// <summary>Saving a query to the library: its name, folder, description and whether it opens on its connection.</summary>
public partial class SaveQueryWindow : Window
{
    private static readonly CompositeFormat KeepConnection = CompositeFormat.Parse(Strings.SaveQueryKeepConnection);

    public SaveQueryWindow(SavedQueryEditViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        KeepConnectionBox.Content = string.Format(CultureInfo.CurrentCulture, KeepConnection, viewModel.ConnectionName ?? "—");
        KeepConnectionBox.Visibility = viewModel.CanKeepConnection ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;
}
