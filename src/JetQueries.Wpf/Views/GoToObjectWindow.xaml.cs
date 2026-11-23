using System.Windows;
using System.Windows.Input;
using UiKit.Theming;

namespace JetQueries.Wpf.Views;

/// <summary>Go to object: type, move with ↑↓, Enter reveals the object in the explorer, Esc closes.</summary>
public partial class GoToObjectWindow : Window
{
    private readonly GoToObjectViewModel _viewModel;

    public GoToObjectWindow(GoToObjectViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        viewModel.CloseRequested += (_, _) =>
        {
            if (IsVisible)
            {
                Close();
            }
        };
        Closed += (_, _) => viewModel.CloseCommand.Execute(null);
        Loaded += (_, _) =>
        {
            Search.Focus();
            _ = viewModel.IndexAsync();
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down or Key.Up when _viewModel.Results.Count > 0:
                var index = _viewModel.Selected is { } selected ? _viewModel.Results.IndexOf(selected) : -1;
                index = Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, _viewModel.Results.Count - 1);
                _viewModel.Selected = _viewModel.Results[index];
                List.ScrollIntoView(_viewModel.Selected);
                e.Handled = true;
                break;
            case Key.Enter:
                _viewModel.OpenCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when string.IsNullOrEmpty(_viewModel.SearchText):
                Close();
                e.Handled = true;
                break;
            default:
                break;
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => _viewModel.OpenCommand.Execute(null);
}
