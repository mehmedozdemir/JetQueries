using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JetQueries.Wpf.Editor;

/// <summary>
/// The editor as a one-line SQL box (the result grid's filter): no line numbers or scroll bars, Enter and Esc
/// raise <see cref="Submitted"/> and <see cref="Canceled"/> (unless the completion list or signature help has
/// them), pasted line breaks become spaces, and a placeholder shows while it is empty.
/// </summary>
public partial class SqlEditor
{
    public static readonly DependencyProperty IsSingleLineProperty = DependencyProperty.Register(
        nameof(IsSingleLine), typeof(bool), typeof(SqlEditor), new PropertyMetadata(false, static (d, e) => ((SqlEditor)d).ApplySingleLine((bool)e.NewValue)));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(SqlEditor), new PropertyMetadata(null, static (d, _) => ((SqlEditor)d).UpdatePlaceholder()));

    private TextBlock? _placeholder;

    /// <summary>A one-line box: see the class remarks.</summary>
    public bool IsSingleLine
    {
        get => (bool)GetValue(IsSingleLineProperty);
        set => SetValue(IsSingleLineProperty, value);
    }

    /// <summary>Shown, dimmed, while the text is empty.</summary>
    public string? Placeholder
    {
        get => (string?)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>Enter in a one-line box.</summary>
    public event EventHandler? Submitted;

    /// <summary>Esc in a one-line box.</summary>
    public event EventHandler? Canceled;

    /// <summary>The placeholder's text while it shows, for tests.</summary>
    internal string? PlaceholderShown => _placeholder is { Visibility: Visibility.Visible } shown ? shown.Text : null;

    private void ApplySingleLine(bool singleLine)
    {
        _editor.ShowLineNumbers = !singleLine;
        _editor.VerticalScrollBarVisibility = singleLine ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        _editor.HorizontalScrollBarVisibility = singleLine ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        _editor.Padding = singleLine ? new Thickness(6, 4, 6, 0) : new Thickness(6, 4, 0, 0);
        _editor.TextArea.TextView.Margin = singleLine ? new Thickness(0) : new Thickness(12, 0, 0, 0);
        if (singleLine)
        {
            DataObject.AddPastingHandler(_editor.TextArea, OnSingleLinePasting);
        }
        else
        {
            DataObject.RemovePastingHandler(_editor.TextArea, OnSingleLinePasting);
        }

        UpdatePlaceholder();
    }

    // Enter and Esc belong to the completion list and signature help while they are open.
    private bool HandleSingleLineKey(KeyEventArgs e)
    {
        if (!IsSingleLine || _completionWindow is not null || ShownSignature is not null)
        {
            return false;
        }

        if (e.Key is Key.Enter or Key.Return)
        {
            if (Keyboard.Modifiers == ModifierKeys.None)
            {
                Submitted?.Invoke(this, EventArgs.Empty);
            }

            return true;
        }

        if (e.Key == Key.Escape)
        {
            Canceled?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    private void OnSingleLinePasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || text.IndexOfAny(['\r', '\n']) < 0)
        {
            return;
        }

        var oneLine = string.Join(' ', text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).Select(static l => l.Trim()).Where(static l => l.Length > 0));
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, oneLine);
        e.DataObject = data;
    }

    private void UpdatePlaceholder()
    {
        if (Content is not Grid grid)
        {
            return;
        }

        if (_placeholder is null && Placeholder is not null)
        {
            _placeholder = new TextBlock { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
            _placeholder.SetResourceReference(TextBlock.FontFamilyProperty, "Font.UI");
            grid.Children.Add(_placeholder);
        }

        if (_placeholder is not null)
        {
            _placeholder.Text = Placeholder ?? string.Empty;
            _placeholder.Visibility = Placeholder is { Length: > 0 } && _editor.Document.TextLength == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
