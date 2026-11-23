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

        var window = Host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Closing += OnMainWindowClosing;
        window.Show();
        _ = StartHostAsync();
    }

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
        try
        {
            await Host!.StopAsync(TimeSpan.FromSeconds(5));
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
