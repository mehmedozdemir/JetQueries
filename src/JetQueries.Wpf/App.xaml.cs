using System.Windows;
using JetQueries.Wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UiKit.Controls;
using UiKit.Theming;

namespace JetQueries.Wpf;

public partial class App : Application
{
    private bool _hostStopped;

    /// <summary>The application host. Null only when the application is created by tests.</summary>
    public IHost? Host { get; init; }

    private ILogger Logger => Host!.Services.GetRequiredService<ILogger<App>>();

    /// <summary>The grid's own texts (sort, filter, copy menus) in the user's language.</summary>
    public static void LocalizeGrid()
    {
        GridStrings.SortAscending = Strings.GridSortAscending;
        GridStrings.SortDescending = Strings.GridSortDescending;
        GridStrings.ClearSort = Strings.GridClearSort;
        GridStrings.Filter = Strings.GridFilter;
        GridStrings.FitColumn = Strings.GridFitColumn;
        GridStrings.FitAllColumns = Strings.GridFitAllColumns;
        GridStrings.HideColumn = Strings.GridHideColumn;
        GridStrings.CopyColumnName = Strings.GridCopyColumnName;
        GridStrings.Copy = Strings.GridCopy;
        GridStrings.CopyWithHeaders = Strings.GridCopyWithHeaders;
        GridStrings.FilterByValue = Strings.GridFilterByValue;
        GridStrings.ExcludeValue = Strings.GridExcludeValue;
        GridStrings.SelectAll = Strings.GridSelectAll;
        GridStrings.NoRows = Strings.GridNoRows;
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Application host failed to start.")]
    private static partial void LogHostStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Application host did not stop cleanly.")]
    private static partial void LogHostStopFailed(ILogger logger, Exception exception);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.Initialize("JetQueries");
        LocalizeGrid();
        if (Host is null)
        {
            return;
        }

        _ = ShowMainWindowAsync();
    }

    // The saved appearance is read first (a small file, off the UI thread), so the window opens in it.
    private async Task ShowMainWindowAsync()
    {
        var services = Host!.Services;
        try
        {
            var store = services.GetRequiredService<Core.Settings.ISettingsStore>();
            var settings = await Task.Run(() => store.LoadAsync(AppearanceSettings.Section, new AppearanceSettings(), CancellationToken.None));
            services.GetRequiredService<IAppearanceService>().Apply(settings);
            var snippets = await Task.Run(() => store.LoadAsync(SnippetSettings.Section, new SnippetSettings(), CancellationToken.None));
            services.GetRequiredService<ISnippetService>().Apply(snippets.Snippets);
        }
#pragma warning disable CA1031 // Settings that cannot be read leave the defaults; the application still starts.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSettingsUnreadable(Logger, ex);
        }

        var window = services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Closing += OnMainWindowClosing;
        window.Show();

        // The tabs of the last run come back (they reconnect when their connections are opened).
        await services.GetRequiredService<DocumentsViewModel>().RestoreAsync();
        await StartHostAsync();

        // Updates: a quiet look at the feed when the settings ask for it; "restart" closes the window the normal
        // way (the open tabs are saved) and Velopack applies the update after the process exits.
        var updates = services.GetRequiredService<UpdatesViewModel>();
        updates.RestartRequested += (_, _) => window.Close();
        _ = updates.StartupCheckAsync();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Appearance settings could not be read; defaults are used.")]
    private static partial void LogSettingsUnreadable(ILogger logger, Exception exception);

    // The window is already visible: extensions and the rest of the host start behind it.
    private async Task StartHostAsync()
    {
        try
        {
            await Host!.StartAsync();
        }
#pragma warning disable CA1031 // Top level: whatever stops the host from starting is logged and ends the application.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogHostStartFailed(Logger, ex);
            Shutdown(1);
        }
    }

    // async void: a top-level event handler; every failure is caught and logged. The close is held until the
    // hosted services have stopped, without blocking the UI thread, then repeated.
    private async void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_hostStopped || sender is not Window window)
        {
            return;
        }

        e.Cancel = true;
        _hostStopped = true;

        // Open transactions are committed or rolled back only on the user's word; Cancel keeps the window.
        var documents = Host!.Services.GetRequiredService<DocumentsViewModel>();
        bool resolved;
        try
        {
            resolved = await documents.ResolveOpenTransactionsAsync();
        }
#pragma warning disable CA1031 // A failure here must not lose the window: it stays open and the error is logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogHostStopFailed(Logger, ex);
            resolved = false;
        }

        if (!resolved)
        {
            _hostStopped = false;
            return;
        }

        try
        {
            // Unsaved texts are kept in the workspace, not asked about: they are back at the next start.
            await Host!.Services.GetRequiredService<DocumentsViewModel>().SaveWorkspaceAsync();
            await Host.StopAsync(TimeSpan.FromSeconds(5));
        }
#pragma warning disable CA1031 // Shutting down: a failure to stop cleanly is logged, never shown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogHostStopFailed(Logger, ex);
        }

        window.Close();
    }
}
