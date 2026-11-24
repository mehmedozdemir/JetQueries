using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JetQueries.Wpf.Views;

/// <summary>The command palette (Ctrl+Shift+P): type, ↑↓, Enter; Esc or a click elsewhere closes it.</summary>
public partial class CommandPaletteWindow : Window
{
    private readonly CommandPaletteViewModel _viewModel;
    private bool _closing;

    public CommandPaletteWindow(CommandPaletteViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => Search.Focus();
        Closing += (_, _) => _closing = true;
    }

    /// <summary>Every command of a menu, with the path of its submenus as the group ("Dosya", "Araçlar").</summary>
    public static IEnumerable<PaletteCommand> FromMenu(Menu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        foreach (var top in menu.Items.OfType<MenuItem>())
        {
            var group = Plain(top.Header);
            foreach (var item in Leaves(top))
            {
                var captured = item;
                yield return new PaletteCommand(
                    Plain(item.Header),
                    group,
                    string.IsNullOrEmpty(item.InputGestureText) ? null : item.InputGestureText,
                    () => Invoke(captured),
                    () => captured.Command?.CanExecute(captured.CommandParameter) ?? captured.IsEnabled);
            }
        }
    }

    private static IEnumerable<MenuItem> Leaves(MenuItem parent)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            if (item.HasItems)
            {
                foreach (var leaf in Leaves(item))
                {
                    yield return leaf;
                }
            }
            else
            {
                yield return item;
            }
        }
    }

    // A command runs as the menu would run it: a check item flips first, then its Click and Command.
    private static void Invoke(MenuItem item)
    {
        if (item.IsCheckable && item.Command is null)
        {
            item.IsChecked = !item.IsChecked;
        }

        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        if (item.Command is { } command && command.CanExecute(item.CommandParameter))
        {
            command.Execute(item.CommandParameter);
        }
    }

    // Access keys ("_Dosya") are shown without their underscore.
    private static string Plain(object? header) => (header?.ToString() ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal);

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _viewModel.Move(1);
                break;
            case Key.Up:
                _viewModel.Move(-1);
                break;
            case Key.PageDown:
                _viewModel.Move(8);
                break;
            case Key.PageUp:
                _viewModel.Move(-8);
                break;
            case Key.Enter:
                RunSelected();
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }

        if (_viewModel.Selected is { } selected)
        {
            List.ScrollIntoView(selected);
        }

        e.Handled = true;
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => RunSelected();

    private void RunSelected()
    {
        // Closed first: the command may open a dialog of its own.
        var viewModel = _viewModel;
        Close();
        viewModel.Run();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_closing)
        {
            Close();
        }
    }
}
