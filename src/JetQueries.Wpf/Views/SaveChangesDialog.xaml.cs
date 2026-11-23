using System.Globalization;
using System.Text;
using System.Windows;
using UiKit.Theming;

namespace JetQueries.Wpf.Views;

/// <summary>"Save the changes to …?" with Save, Don't save and Cancel (Esc).</summary>
public partial class SaveChangesDialog : Window
{
    public SaveChangesDialog(string documentName)
    {
        InitializeComponent();
        Message.Text = string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.SaveChangesMessage), documentName);
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    /// <summary>The answer; Cancel unless a button said otherwise.</summary>
    public SaveChangesChoice Choice { get; private set; } = SaveChangesChoice.Cancel;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Choice = SaveChangesChoice.Save;
        DialogResult = true;
    }

    private void OnDiscard(object sender, RoutedEventArgs e)
    {
        Choice = SaveChangesChoice.Discard;
        DialogResult = true;
    }
}
