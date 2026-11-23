using System.Globalization;
using System.Text;
using System.Windows;
using UiKit.Theming;

namespace JetQueries.Wpf.Views;

/// <summary>"… has changes that are not committed" with Commit, Rollback and Cancel (Esc and Enter: nothing is ended by accident).</summary>
public partial class TransactionDialog : Window
{
    public TransactionDialog(string documentNames)
    {
        InitializeComponent();
        Message.Text = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.TransactionAskMessage), documentNames);
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    /// <summary>The answer; Cancel unless a button said otherwise.</summary>
    public TransactionChoice Choice { get; private set; } = TransactionChoice.Cancel;

    private void OnCommit(object sender, RoutedEventArgs e)
    {
        Choice = TransactionChoice.Commit;
        DialogResult = true;
    }

    private void OnRollback(object sender, RoutedEventArgs e)
    {
        Choice = TransactionChoice.Rollback;
        DialogResult = true;
    }
}
