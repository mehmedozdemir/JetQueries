using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UiKit.Controls;

/// <summary>
/// Editing a cell of a <see cref="VirtualGrid"/> whose source is an <see cref="IEditableGridDataSource"/>: F2, a
/// double click or typing opens a text box over the cell; Enter (down) and Tab (right) keep the text, Esc drops it,
/// leaving the box keeps it. The host gets <see cref="CellEdited"/> and updates its source.
/// </summary>
public sealed partial class VirtualGrid
{
    private TextBox? _editor;
    private int _editRow = -1;
    private int _editCol = -1;

    /// <summary>A cell was edited: the row, the source column and the text typed.</summary>
    public event Action<int, int, string>? CellEdited;

    /// <summary>Whether a cell editor is open.</summary>
    public bool IsEditing => _editor is not null;

    /// <summary>The text box of the open editor; null when none is open.</summary>
    public TextBox? ActiveEditor => _editor;

    private IEditableGridDataSource? Editable => _source as IEditableGridDataSource;

    /// <summary>Opens the editor on the current cell; false when it cannot be edited.</summary>
    /// <param name="initialText">Text to start with (a typed character); null starts with the cell's value.</param>
    public bool BeginEdit(string? initialText = null)
    {
        if (_editor is not null || Editable is not { } editable || _curRow < 0 || _curCol < 0 || _curCol >= _display.Length ||
            _curRow >= _source.RowCount || !editable.CanEdit(_curRow, _display[_curCol]))
        {
            return false;
        }

        EnsureVisible(_curRow, _curCol);
        _editRow = _curRow;
        _editCol = _curCol;
        var editor = new TextBox
        {
            Text = initialText ?? editable.GetEditText(_editRow, _display[_editCol]) ?? string.Empty,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(CellPadding - 3, 0, CellPadding - 3, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 0,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
        };
        editor.SetResourceReference(TextBox.BackgroundProperty, "Brush.Input");
        editor.SetResourceReference(TextBox.ForegroundProperty, "Brush.Text");
        editor.SetResourceReference(TextBox.CaretBrushProperty, "Brush.Text");
        editor.PreviewKeyDown += OnEditorKeyDown;
        editor.LostKeyboardFocus += (_, e) =>
        {
            if (ReferenceEquals(_editor, editor) && e.NewFocus is not System.Windows.Controls.ContextMenu and not MenuItem)
            {
                CommitEdit(move: null);
            }
        };
        _editor = editor;
        _children.Add(editor);
        InvalidateArrange();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (ReferenceEquals(_editor, editor))
            {
                editor.Focus();
                if (initialText is null)
                {
                    editor.SelectAll();
                }
                else
                {
                    editor.CaretIndex = editor.Text.Length;
                }
            }
        });
        return true;
    }

    /// <summary>Makes a cell current (and scrolls to it): a row and a source column.</summary>
    public void MoveTo(int row, int column)
    {
        var col = Array.IndexOf(_display, column);
        SetCurrent(row, col < 0 ? 0 : col, keepAnchor: false);
    }

    private bool StartEditAt(int row, int col)
    {
        SetCurrent(row, col, keepAnchor: false);
        return BeginEdit();
    }

    /// <summary>Keeps the text typed, closes the editor and moves to the next cell when asked.</summary>
    /// <param name="move">Down (Enter), right (Tab), left (Shift+Tab) or stay (null).</param>
    public void CommitEdit(Key? move = Key.Down)
    {
        if (_editor is not { } editor)
        {
            return;
        }

        var (row, col, text) = (_editRow, _editCol, editor.Text);
        bool hadFocus = editor.IsKeyboardFocusWithin;
        CloseEditor();
        if (col < _display.Length)
        {
            CellEdited?.Invoke(row, _display[col], text);
        }

        InvalidateVisual();
        switch (move)
        {
            case Key.Down:
                SetCurrent(Math.Min(row + 1, _source.RowCount - 1), col, keepAnchor: false);
                break;
            case Key.Right:
                SetCurrent(row, Math.Min(col + 1, _display.Length - 1), keepAnchor: false);
                break;
            case Key.Left:
                SetCurrent(row, Math.Max(col - 1, 0), keepAnchor: false);
                break;
        }

        if (hadFocus || move is not null)
        {
            Focus();
        }
    }

    /// <summary>Drops the text typed and closes the editor.</summary>
    public void CancelEdit()
    {
        if (_editor is null)
        {
            return;
        }

        CloseEditor();
        InvalidateVisual();
        Focus();
    }

    private void CloseEditor()
    {
        if (_editor is { } editor)
        {
            _editor = null;
            _children.Remove(editor);
            _editRow = _editCol = -1;
            InvalidateArrange();
        }
    }

    private bool IsInEditor(object? source) =>
        _editor is { } editor && source is DependencyObject element && (ReferenceEquals(element, editor) || IsDescendant(editor, element));

    private static bool IsDescendant(DependencyObject ancestor, DependencyObject element)
    {
        for (var current = element; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CommitEdit(Key.Down);
                e.Handled = true;
                break;
            case Key.Tab:
                CommitEdit(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Key.Left : Key.Right);
                e.Handled = true;
                break;
            case Key.Escape:
                CancelEdit();
                e.Handled = true;
                break;
        }
    }

    // The editor sits over its cell and follows scrolling; out of view it is hidden.
    private void ArrangeEditor()
    {
        if (_editor is not { } editor)
        {
            return;
        }

        if (_editCol < 0 || _editCol >= _display.Length)
        {
            editor.Arrange(new Rect(0, 0, 0, 0));
            return;
        }

        double x = _gutterWidth + _lefts[_editCol] - _hOffset;
        double y = HeaderHeight + (_editRow - _firstRow) * RowHeight;
        double width = _widths[_display[_editCol]];
        bool visible = y >= HeaderHeight - 0.5 && y + RowHeight <= DataBottom + 0.5 && x + width > _gutterWidth && x < DataRight;
        editor.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        double left = Math.Max(x, _gutterWidth) + 2;
        double right = Math.Min(x + width, DataRight) - 2;
        editor.Measure(new Size(Math.Max(0, right - left), Math.Max(0, RowHeight - 4)));
        editor.Arrange(new Rect(left, y + 2, Math.Max(0, right - left), Math.Max(0, RowHeight - 4)));
    }

    /// <summary>Typing on a cell starts editing it with what was typed.</summary>
    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (!e.Handled && _editor is null && e.Text is { Length: > 0 } text && !char.IsControl(text[0]) &&
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 && BeginEdit(text))
        {
            e.Handled = true;
        }
    }
}
