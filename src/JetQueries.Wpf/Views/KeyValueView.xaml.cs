using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.ViewModels;
using UiKit.Controls;

namespace JetQueries.Wpf.Views;

/// <summary>
/// A key in a tab: a string's text, or a collection's items in a grid (name, value, score as the type has them)
/// with the selected item in an editor beside it.
/// </summary>
public partial class KeyValueView : UserControl
{
    private KeyValueViewModel? _viewModel;
    private bool _selecting;

    public KeyValueView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as KeyValueViewModel);
        ItemsGrid.CurrentCellChanged += OnCurrentCellChanged;
    }

    /// <summary>The items grid, for tests.</summary>
    internal VirtualGrid Grid => ItemsGrid;

    private void Attach(KeyValueViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.Items.CollectionChanged -= OnItemsChanged;
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.Items.CollectionChanged += OnItemsChanged;
            viewModel.PropertyChanged += OnViewModelChanged;
        }

        ShowItems();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.BeginInvoke(ShowItems);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KeyValueViewModel.SelectedItem) && !_selecting && _viewModel?.SelectedItem is { } selected)
        {
            var row = _viewModel.Items.IndexOf(selected);
            if (row >= 0 && row != ItemsGrid.CurrentRow)
            {
                ItemsGrid.MoveTo(row, 0);
            }
        }
        else if (e.PropertyName == nameof(KeyValueViewModel.Failed))
        {
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, _viewModel?.Failed == true ? "Brush.Danger" : "Brush.TextSecondary");
        }
    }

    // The columns follow the type: hash (field, value), list (index, value), set (member), zset (member, score), stream (id, fields).
    private void ShowItems()
    {
        if (_viewModel is not { } viewModel)
        {
            ItemsGrid.Source = ArrayGridDataSource.Empty;
            return;
        }

        var items = viewModel.Items.ToArray();
        var columns = new List<(string Header, GridColumnKind Kind, Func<KeyItemViewModel, string> Text)>();
        if (viewModel.HasNames)
        {
            columns.Add((viewModel.NameHeader, viewModel.Value?.Type == Sdk.KeyValueType.List ? GridColumnKind.Integer : GridColumnKind.Text, static i => i.Name));
        }

        columns.Add((viewModel.ValueHeader, GridColumnKind.Text, static i => i.Preview));
        if (viewModel.HasScores)
        {
            columns.Add((Strings.KeyScore, GridColumnKind.Number, static i => i.Score));
        }

        ItemsGrid.Source = new ArrayGridDataSource(
            [.. columns.Select(static c => c.Header)],
            [.. columns.Select(static c => c.Kind)],
            items.Length,
            (r, c) => columns[c].Text(items[r]));
        if (viewModel.SelectedItem is { } selected && Array.IndexOf(items, selected) is var row and >= 0)
        {
            ItemsGrid.MoveTo(row, 0);
        }
    }

    private void OnCurrentCellChanged()
    {
        if (_viewModel is not { } viewModel || ItemsGrid.CurrentRow < 0 || ItemsGrid.CurrentRow >= viewModel.Items.Count)
        {
            return;
        }

        _selecting = true;
        try
        {
            viewModel.SelectedItem = viewModel.Items[ItemsGrid.CurrentRow];
        }
        finally
        {
            _selecting = false;
        }
    }
}
