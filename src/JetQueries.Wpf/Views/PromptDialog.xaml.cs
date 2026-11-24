using System.Windows;
using UiKit.Theming;

namespace JetQueries.Wpf.Views;

/// <summary>A small modal question: a confirmation, or a password prompt. OK closes it with a true result.</summary>
public partial class PromptDialog : Window
{
    public PromptDialog(string title, string message, bool askForSecret)
    {
        InitializeComponent();
        Title = title;
        Message.Text = message;
        SecretInput.Visibility = askForSecret ? Visibility.Visible : Visibility.Collapsed;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        if (askForSecret)
        {
            Loaded += (_, _) => SecretInput.Focus();
        }
    }

    /// <summary>A question that takes a line of text (a folder name), starting with <paramref name="initial"/>.</summary>
    public PromptDialog(string title, string message, string initial)
        : this(title, message, askForSecret: false)
    {
        TextAnswer.Visibility = Visibility.Visible;
        TextAnswer.Text = initial;
        Loaded += (_, _) =>
        {
            TextAnswer.Focus();
            TextAnswer.SelectAll();
        };
    }

    /// <summary>The entered text after OK.</summary>
    public string Text => TextAnswer.Text;

    /// <summary>The entered password after OK; never stored by the dialog itself.</summary>
    public string Secret { get; private set; } = string.Empty;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Secret = SecretInput.Password;
        DialogResult = true;
    }
}
