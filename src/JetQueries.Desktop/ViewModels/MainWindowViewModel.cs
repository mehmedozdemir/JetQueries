using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Desktop.Resources;
using JetQueries.Desktop.Services;

namespace JetQueries.Desktop.ViewModels;

/// <summary>View model of the shell window.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly IThemeService _themeService;

    public MainWindowViewModel(IThemeService themeService, ConnectionsViewModel connections, DocumentsViewModel documents, HistoryViewModel history)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _themeService = themeService;
        Connections = connections;
        Documents = documents;
        History = history;

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
        Dispatcher.UIThread.Post(() => ExtensionStatus = text);
    }

    [RelayCommand]
    private void ToggleTheme() => _themeService.Toggle();
}
