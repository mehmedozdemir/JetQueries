using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Core.Connections;
using JetQueries.Core.Jobs;
using JetQueries.Core.Metadata;
using JetQueries.Sdk;
using JetQueries.Wpf.Services;

namespace JetQueries.Wpf.ViewModels;

/// <summary>View model of the shell window.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly IThemeService _themeService;
    private readonly IUiDispatcher _ui;
    private readonly ISettingsDialog? _settings;
    private readonly IConnectionManager? _manager;
    private readonly IMetadataService? _metadata;
    private readonly IJobQueue? _jobs;
    private readonly Core.Settings.ISettingsStore? _store;

    /// <param name="manager">Open sessions, for object details; without it (tests) details are not shown.</param>
    public MainWindowViewModel(
        IThemeService themeService, IUiDispatcher ui, ConnectionsViewModel connections, DocumentsViewModel documents, HistoryViewModel history,
        ISettingsDialog? settings = null, IConnectionManager? manager = null, IMetadataService? metadata = null, IJobQueue? jobs = null,
        Core.Settings.ISettingsStore? store = null, IAppearanceService? appearance = null, UpdatesViewModel? updates = null,
        SavedQueriesViewModel? savedQueries = null)
    {
        Updates = updates;
        SavedQueries = savedQueries;
        _store = store;
        _appearance = appearance;
        Services.AppearanceService.Changed += (_, _) => OnPropertyChanged(nameof(IsCompact));
        _manager = manager;
        _metadata = metadata;
        _jobs = jobs;
        ArgumentNullException.ThrowIfNull(themeService);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(documents);
        _themeService = themeService;
        _ui = ui;
        Connections = connections;
        Documents = documents;
        History = history;
        _settings = settings;
        themeService.Changed += (_, _) => OnPropertyChanged(nameof(IsDark));

        // "New query" on an explorer node opens a tab on its connection and database.
        connections.NewQueryRequested += (_, request) => _ = documents.OpenAsync(request);

        // Details of an object: from the explorer (Details, F4) and from the editor (Ctrl+Click, F12).
        connections.DetailsRequested += (_, request) => ShowDetails(request);
        connections.KeyRequested += (_, key) => documents.OpenKey(key);
        documents.DetailsRequested += (_, request) => ShowDetails(request);

        // A new query starts on the connection that is selected in the explorer.
        connections.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionsViewModel.Selected) && connections.Selected is ConnectionNodeViewModel { IsConnected: true } node)
            {
                documents.PreferredConnectionId = node.Definition.Id;
            }
        };
    }

    /// <summary>The queries that were run.</summary>
    public HistoryViewModel History { get; }

    /// <summary>The library of saved queries; null in tests that do not need it.</summary>
    public SavedQueriesViewModel? SavedQueries { get; }

    /// <summary>Raised to show the saved queries window.</summary>
    public event EventHandler? SavedQueriesRequested;

    /// <summary>Main menu: Tools, Saved queries.</summary>
    [RelayCommand]
    private void ShowSavedQueries() => SavedQueriesRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The open query tabs.</summary>
    public DocumentsViewModel Documents { get; }

    /// <summary>The connection tree shown in the explorer panel.</summary>
    public ConnectionsViewModel Connections { get; }

    /// <summary>Whether the dark theme is showing (the theme button shows the other one).</summary>
    public bool IsDark => _themeService.IsDark;

    /// <summary>Status bar text about extensions; empty until they have been loaded.</summary>
    [ObservableProperty]
    public partial string ExtensionStatus { get; set; } = string.Empty;

    /// <summary>Shows how many extensions were loaded. Safe to call from any thread.</summary>
    /// <param name="loaded">Number of active extensions.</param>
    /// <param name="failed">Number of extensions that could not be loaded.</param>
    public void ShowExtensions(int loaded, int failed)
    {
        var text = failed == 0
            ? string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ExtensionsLoaded), loaded)
            : string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.ExtensionsLoadedWithFailures), loaded, failed);
        _ui.Post(() => ExtensionStatus = text);
    }

    /// <summary>Hands the loaded providers to the explorer. Safe to call from any thread.</summary>
    public void ShowProviders(IReadOnlyCollection<IDatabaseProvider> providers) => _ui.Post(() => Connections.SetProviders(providers));

    /// <summary>Raised to show Go to object for the open connections.</summary>
    public event EventHandler<GoToObjectViewModel>? GoToObjectRequested;

    /// <summary>Go to object (Ctrl+Shift+N).</summary>
    [RelayCommand]
    private void GoToObject() => GoToObjectRequested?.Invoke(this, new GoToObjectViewModel(Connections, _ui));

    /// <summary>Raised to show the query history window.</summary>
    public event EventHandler? HistoryRequested;

    /// <summary>Opens the details of an object on an open connection.</summary>
    public void ShowDetails(ObjectDetailsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_manager?.Sessions.FirstOrDefault(s => s.Definition.Id == request.ConnectionId) is not { } session || _metadata is null || _jobs is null)
        {
            return;
        }

        var details = new ObjectDetailsViewModel(session, request.Target, _metadata, _jobs);
        details.NewQueryRequested += (_, query) => _ = Documents.OpenAsync(query);
        details.DetailsRequested += (_, next) => ShowDetails(next);
        details.ExecuteRequested += (_, routine) => _ = Connections.ExecuteRoutineAsync(session, routine.Target);

        // A tab next to the queries (as DBeaver's object editor); an open one for the object is selected instead.
        if (ReferenceEquals(Documents.OpenDetails(details), details))
        {
            _ = details.LoadAsync();
        }
    }

    /// <summary>Raised to show the keyboard shortcuts.</summary>
    public event EventHandler? ShortcutsRequested;

    /// <summary>Main menu: Tools, Keyboard shortcuts (F1).</summary>
    [RelayCommand]
    private void ShowShortcuts() => ShortcutsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The explorer's visibility and width from the last run; defaults without a store.</summary>
    public async Task<ShellLayout> LoadLayoutAsync()
    {
        if (_store is null || _jobs is null)
        {
            return new ShellLayout();
        }

        try
        {
            var store = _store;
            return await _jobs.Enqueue("Read layout", context => store.LoadAsync(ShellLayout.Section, new ShellLayout(), context.CancellationToken), JobPriority.High)
                .Completion;
        }
#pragma warning disable CA1031 // A layout that cannot be read is the default one.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new ShellLayout();
        }
    }

    /// <summary>Remembers the explorer's visibility and width (written in the background).</summary>
    public void SaveLayout(ShellLayout layout)
    {
        if (_store is null || _jobs is null)
        {
            return;
        }

        var store = _store;
        _jobs.Enqueue("Save layout", context => store.SaveAsync(ShellLayout.Section, layout, context.CancellationToken), JobPriority.Low);
    }

    /// <summary>Main menu: Tools, Query history.</summary>
    [RelayCommand]
    private void ShowHistory() => HistoryRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ToggleTheme() => _themeService.Toggle();

    private readonly IAppearanceService? _appearance;

    /// <summary>The application's updates (status bar chip, Tools > Check for updates); null in tests without one.</summary>
    public UpdatesViewModel? Updates { get; }

    /// <summary>Whether the compact density is in effect (View > Compact mode).</summary>
    public bool IsCompact => _appearance?.Current.CompactMode == true;

    /// <summary>View > Compact mode: switches the density at once and remembers it.</summary>
    [RelayCommand]
    private void ToggleCompact()
    {
        if (_appearance is null)
        {
            return;
        }

        var settings = _appearance.Current with { CompactMode = !_appearance.Current.CompactMode };
        _appearance.Apply(settings);
        OnPropertyChanged(nameof(IsCompact));
        if (_store is { } store && _jobs is { } jobs)
        {
            jobs.Enqueue("Save appearance", context => store.SaveAsync(AppearanceSettings.Section, settings, context.CancellationToken), JobPriority.Low);
        }
    }

    [RelayCommand]
    private void OpenSettings() => _settings?.Show();
}

/// <summary>The shell's layout, remembered between runs (the <c>shell</c> settings section).</summary>
public sealed record ShellLayout
{
    /// <summary>The settings section (file) name.</summary>
    public const string Section = "shell";

    /// <summary>Whether the explorer panel is shown.</summary>
    public bool ExplorerVisible { get; init; } = true;

    /// <summary>The explorer panel's width.</summary>
    public double ExplorerWidth { get; init; } = 280;

    /// <summary>Whether the explorer stays beside the tabs (true) or hides behind a rail at the left edge.</summary>
    public bool ExplorerPinned { get; init; } = true;
}
