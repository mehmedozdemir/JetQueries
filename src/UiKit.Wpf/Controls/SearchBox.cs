using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace UiKit.Controls;

/// <summary>Search input with icon, placeholder (<see cref="Ui.PlaceholderProperty"/>) and a clear button. Esc clears.</summary>
public class SearchBox : TextBox
{
    private ButtonBase? _clear;

    public override void OnApplyTemplate()
    {
        if (_clear is not null)
            _clear.Click -= OnClear;
        base.OnApplyTemplate();
        _clear = GetTemplateChild("PART_Clear") as ButtonBase;
        if (_clear is not null)
            _clear.Click += OnClear;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape && Text.Length > 0)
        {
            Clear();
            e.Handled = true;
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Clear();
        Focus();
    }
}
