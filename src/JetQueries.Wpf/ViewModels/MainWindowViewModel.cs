using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Sdk;
using JetQueries.Wpf.Services;

namespace JetQueries.Wpf.ViewModels;

/// <summary>View model of the shell window.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly IThemeService _themeService;
    private readonly IUiDispatcher _ui;
    private readonly ISettingsDialog? _settings;

    public MainWindowViewModel(
        IThemeService themeService, IUiDispatcher ui, ConnectionsViewModel connections, DocumentsViewModel documents, HistoryViewModel history,
        ISettingsDialog? settings = null)
    {
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

    [RelayCommand]
    private void ToggleTheme() => _themeService.Toggle();

    [RelayCommand]
    private void OpenSettings() => _settings?.Show();
}
