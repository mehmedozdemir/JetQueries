using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Presentation.Resources;
using JetQueries.Presentation.Services;
using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>An object found by Go to object, with the explorer nodes above it.</summary>
/// <param name="Node">The object's node.</param>
/// <param name="Ancestors">Its parents, from the connection down.</param>
public sealed record GoToObjectItem(DatabaseObjectNodeViewModel Node, IReadOnlyList<ExplorerNodeViewModel> Ancestors)
{
    /// <summary>The object's name.</summary>
    public string Name => Node.Name;

    /// <summary>Where it is: connection · database · schema.</summary>
    public string Location => string.Join(" · ", Ancestors.Where(static a => a is not CategoryNodeViewModel).Select(static a => a.Name));

    /// <summary>What it is (table, view…), for the icon.</summary>
    public DatabaseObjectKinds Kind => Node.Target.Kind;
}

/// <summary>
/// Go to object (Ctrl+Shift+N, as in DataGrip): finds tables, views, routines and packages of every open connection
/// by name, as the explorer's tree has them. Lists not loaded yet are loaded in the background through the metadata
/// cache (the tree stays as it is); results appear as they are found. Choosing one reveals it in the explorer.
/// </summary>
public sealed partial class GoToObjectViewModel : ObservableObject
{
    /// <summary>Most results shown.</summary>
    public const int MaxResults = 200;

    private readonly ConnectionsViewModel _connections;
    private readonly IUiDispatcher _ui;
    private readonly List<GoToObjectItem> _index = [];
    private volatile bool _closed;
    private int _searchVersion;

    /// <param name="connections">The explorer whose open connections are searched.</param>
    /// <param name="ui">Where results are shown.</param>
    public GoToObjectViewModel(ConnectionsViewModel connections, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _connections = connections;
        _ui = ui;
    }

    /// <summary>Raised when the window should close (an object was chosen, or Esc).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The name, or part of it, to look for.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>What matches, best first (names starting with the text before those containing it).</summary>
    public ObservableCollection<GoToObjectItem> Results { get; } = [];

    [ObservableProperty]
    public partial GoToObjectItem? Selected { get; set; }

    /// <summary>Lists are still being loaded.</summary>
    [ObservableProperty]
    public partial bool IsIndexing { get; set; }

    /// <summary>"1,234 objects" or what is missing (no open connection).</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    partial void OnSearchTextChanged(string value) => Filter();

    /// <summary>
    /// Walks every open connection's tree, loading what is not loaded yet; results follow as lists arrive. Done
    /// once: later calls wait for the same walk.
    /// </summary>
    public Task IndexAsync() => _indexing ??= IndexOnceAsync();

    private Task? _indexing;

    private async Task IndexOnceAsync()
    {
        var roots = Flatten(_connections.Roots).OfType<ConnectionNodeViewModel>().Where(static c => c.IsConnected).ToList();
        if (roots.Count == 0)
        {
            StatusText = Strings.GoToObjectNoConnection;
            return;
        }

        IsIndexing = true;
        try
        {
            foreach (var root in roots)
            {
                await WalkAsync(root, [root]);
            }
        }
        finally
        {
            IsIndexing = false;
            Filter();
        }
    }

    private async Task WalkAsync(ExplorerNodeViewModel node, IReadOnlyList<ExplorerNodeViewModel> path)
    {
        if (_closed)
        {
            return;
        }

        if (node is LazyNodeViewModel lazy && lazy.Children is [MessageNodeViewModel] && !lazy.IsLoading)
        {
            await lazy.LoadAsync();
        }

        var found = false;
        foreach (var child in node.Children.ToList())
        {
            switch (child)
            {
                case DatabaseObjectNodeViewModel { Target.Kind: DatabaseObjectKinds.Database or DatabaseObjectKinds.Schema } container:
                    await WalkAsync(container, [.. path, container]);
                    break;
                case CategoryNodeViewModel category:
                    await WalkAsync(category, [.. path, category]);
                    break;
                case DatabaseObjectNodeViewModel item:
                    _index.Add(new GoToObjectItem(item, path));
                    found = true;
                    break;
                default:
                    break;
            }
        }

        if (found)
        {
            Filter();
        }
    }

    /// <summary>Shows what matches the text now.</summary>
    private void Filter()
    {
        var version = ++_searchVersion;
        var text = SearchText.Trim();
        var culture = CultureInfo.CurrentCulture.CompareInfo;
        var matches = text.Length == 0
            ? []
            : _index
                .Select(item => (item, at: culture.IndexOf(item.Name, text, CompareOptions.IgnoreCase)))
                .Where(static m => m.at >= 0)
                .OrderBy(static m => m.at == 0 ? 0 : 1)
                .ThenBy(static m => m.item.Name.Length)
                .ThenBy(static m => m.item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxResults)
                .Select(static m => m.item)
                .ToList();
        _ui.Post(() =>
        {
            if (version != _searchVersion)
            {
                return;
            }

            Results.Clear();
            foreach (var match in matches)
            {
                Results.Add(match);
            }

            Selected = Results.FirstOrDefault();
            StatusText = IsIndexing ? Strings.GoToObjectIndexing : string.Format(CultureInfo.CurrentCulture, System.Text.CompositeFormat.Parse(Strings.GoToObjectCount), _index.Count);
        });
    }

    /// <summary>Reveals the chosen object in the explorer and closes.</summary>
    [RelayCommand]
    private void Open(GoToObjectItem? item)
    {
        if ((item ?? Selected) is not { } chosen)
        {
            return;
        }

        foreach (var ancestor in chosen.Ancestors)
        {
            ancestor.IsVisible = true;
            ancestor.IsExpanded = true;
        }

        chosen.Node.IsVisible = true;
        _connections.Reveal(chosen.Node);
        Close();
    }

    /// <summary>Stops loading and closes.</summary>
    [RelayCommand]
    private void Close()
    {
        _closed = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static IEnumerable<ExplorerNodeViewModel> Flatten(IEnumerable<ExplorerNodeViewModel> nodes) =>
        nodes.SelectMany(static n => n is ConnectionNodeViewModel ? [n] : new[] { n }.Concat(Flatten(n.Children)));
}
