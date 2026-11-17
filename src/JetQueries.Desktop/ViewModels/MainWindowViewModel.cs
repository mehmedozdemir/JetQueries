using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JetQueries.Desktop.Resources;
using JetQueries.Desktop.Services;

namespace JetQueries.Desktop.ViewModels;

/// <summary>View model of the shell window.</summary>
public partial class MainWindowViewModel(IThemeService themeService, ConnectionsViewModel connections) : ObservableObject
{
    /// <summary>The connection tree shown in the explorer panel.</summary>
    public ConnectionsViewModel Connections { get; } = connections;

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
    private void ToggleTheme() => themeService.Toggle();
}
