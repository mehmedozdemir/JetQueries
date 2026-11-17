using Avalonia.Controls;
using JetQueries.Desktop.ViewModels;

namespace JetQueries.Desktop.Views;

public partial class ConnectionDialog : Window
{
    /// <summary>Used by the XAML previewer only.</summary>
    public ConnectionDialog() => InitializeComponent();

    public ConnectionDialog(ConnectionEditorViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.CloseRequested += (_, saved) => Close(saved);
    }
}
