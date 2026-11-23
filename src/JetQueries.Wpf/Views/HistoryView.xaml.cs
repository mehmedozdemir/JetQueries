using System.Windows.Controls;
using System.Windows.Input;

namespace JetQueries.Wpf.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    // Double-click opens the run's text in a new query tab.
    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is HistoryViewModel viewModel && viewModel.OpenSelectedCommand.CanExecute(null))
        {
            viewModel.OpenSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }
}
