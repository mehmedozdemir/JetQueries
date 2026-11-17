using Avalonia.Controls;
using Avalonia.Interactivity;

namespace JetQueries.Desktop.Views;

/// <summary>
/// A small modal question: a confirmation, or a password prompt. Closes with the entered text (empty for a
/// confirmation) on OK and with null on Cancel.
/// </summary>
public partial class PromptDialog : Window
{
    /// <summary>Used by the XAML previewer only.</summary>
    public PromptDialog() => InitializeComponent();

    public PromptDialog(string title, string message, bool askForSecret)
        : this()
    {
        Title = title;
        Message.Text = message;
        Secret.IsVisible = askForSecret;
        if (askForSecret)
        {
            Opened += (_, _) => Secret.Focus();
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(Secret.Text ?? string.Empty);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
