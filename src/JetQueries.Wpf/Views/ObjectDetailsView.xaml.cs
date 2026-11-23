using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UiKit.Controls;

namespace JetQueries.Wpf.Views;

/// <summary>
/// The details of a database object as a document tab: properties, a tab per table the provider gives
/// (columns, constraints, foreign keys, references, indexes, triggers, parameters, dependencies) and the DDL.
/// Opened by Ctrl+Click in the editor, F4 and the explorer's "Details".
/// </summary>
public partial class ObjectDetailsView : UserControl
{
    private readonly List<TabItem> _sectionTabs = [];
    private ObjectDetailsViewModel? _viewModel;

    public ObjectDetailsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as ObjectDetailsViewModel);
    }

    private void Attach(ObjectDetailsViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.Sections.CollectionChanged -= OnSectionsChanged;
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.Sections.CollectionChanged += OnSectionsChanged;
            viewModel.PropertyChanged += OnViewModelChanged;
        }

        DdlEditor.Text = viewModel?.Ddl ?? string.Empty;
        BuildSectionTabs();
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ObjectDetailsViewModel.Ddl))
        {
            DdlEditor.Text = _viewModel?.Ddl ?? string.Empty;
        }
    }

    public ObjectDetailsViewModel? ViewModel => _viewModel;

    /// <summary>The section tabs' titles, for tests.</summary>
    internal IReadOnlyList<string> SectionTitles => [.. _sectionTabs.Select(static t => (string)t.Header)];

    private void OnSectionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => BuildSectionTabs();

    /// <summary>One tab per section, between Properties and DDL; the columns tab is selected first when there is one.</summary>
    private void BuildSectionTabs()
    {
        var selectedKey = (Tabs.SelectedItem as TabItem)?.Tag as string;
        foreach (var tab in _sectionTabs)
        {
            Tabs.Items.Remove(tab);
        }

        _sectionTabs.Clear();
        var index = Tabs.Items.IndexOf(DdlTab);
        foreach (var section in _viewModel?.Sections ?? [])
        {
            var tab = new TabItem { Header = section.Header, Tag = section.Key, Content = SectionContent(section) };
            Ui.SetIcon(tab, IconOf(section.Key));
            Tabs.Items.Insert(index++, tab);
            _sectionTabs.Add(tab);
        }

        Tabs.SelectedItem = _sectionTabs.FirstOrDefault(t => (string)t.Tag == selectedKey)
            ?? (selectedKey is null ? _sectionTabs.FirstOrDefault(static t => (string)t.Tag == Sdk.ObjectDetailKeys.Columns || (string)t.Tag == Sdk.ObjectDetailKeys.Parameters) : null)
            ?? Tabs.SelectedItem ?? PropertiesTab;
    }

    private static string IconOf(string key) => key switch
    {
        Sdk.ObjectDetailKeys.Columns => Icons.Column,
        Sdk.ObjectDetailKeys.Constraints => Icons.Key,
        Sdk.ObjectDetailKeys.ForeignKeys or Sdk.ObjectDetailKeys.References => "",
        Sdk.ObjectDetailKeys.Indexes => Icons.List,
        Sdk.ObjectDetailKeys.Triggers => "",
        Sdk.ObjectDetailKeys.Dependencies => "",
        Sdk.ObjectDetailKeys.Parameters => Icons.Code,
        _ => Icons.Table,
    };

    /// <summary>A read-only grid of the rows; under it the current row's source, when rows carry one.</summary>
    private DockPanel SectionContent(DetailSectionView section)
    {
        var rows = section.Rows;
        var grid = new VirtualGrid
        {
            NullText = string.Empty,
            ShowTypeTags = false,
            ShowFilterButtons = false,
            Source = new ArrayGridDataSource(
                [.. section.Headers],
                [.. section.Headers.Select(static _ => GridColumnKind.Text)],
                rows.Count,
                (r, c) => c < rows[r].Cells.Count ? rows[r].Cells[c] : null),
        };
        var surface = new Border { Child = grid };
        surface.SetResourceReference(StyleProperty, "Card.Surface");
        var host = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (section.HasTargets)
        {
            var hint = new TextBlock { Text = Strings.DetailsRowHint, Margin = new Thickness(2, 6, 0, 0) };
            hint.SetResourceReference(StyleProperty, "Text.Caption");
            DockPanel.SetDock(hint, Dock.Bottom);
            host.Children.Add(hint);
            // The grid handles the press itself (it moves the current cell first); the second click of a double-click opens the row.
            grid.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) =>
            {
                if (e.ClickCount == 2 && e.GetPosition(grid).Y > 40 && grid.CurrentRow >= 0 && grid.CurrentRow < rows.Count)
                {
                    _viewModel?.OpenRow(rows[grid.CurrentRow]);
                }
            }), handledEventsToo: true);
        }

        if (section.HasSource)
        {
            var source = new Editor.SqlEditor { IsReadOnly = true, Margin = new Thickness(4, 6, 4, 4), LanguageService = _viewModel?.LanguageService };
            var sourceCard = new Border { Child = source, Height = 190, Margin = new Thickness(0, 8, 0, 0) };
            sourceCard.SetResourceReference(StyleProperty, "Card.Surface");
            DockPanel.SetDock(sourceCard, Dock.Bottom);
            host.Children.Add(sourceCard);
            void Show()
            {
                var row = grid.CurrentRow >= 0 && grid.CurrentRow < rows.Count ? rows[grid.CurrentRow] : rows.FirstOrDefault(static r => r.Source is not null);
                source.Text = row?.Source ?? string.Empty;
            }

            grid.CurrentCellChanged += Show;
            Show();
        }

        host.Children.Add(surface);
        return host;
    }
}
