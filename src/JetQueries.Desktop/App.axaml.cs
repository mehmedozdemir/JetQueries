using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using JetQueries.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JetQueries.Desktop;

public partial class App : Application
{
    private bool _hostStopped;

    /// <summary>The application host. Null only in the XAML previewer.</summary>
    public IHost? Host { get; init; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (Host is not null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = Host.Services.GetRequiredService<MainWindow>();
            desktop.Startup += OnStartup;
            desktop.ShutdownRequested += OnShutdownRequested;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private ILogger Logger => Host!.Services.GetRequiredService<ILogger<App>>();

    [LoggerMessage(Level = LogLevel.Critical, Message = "Application host failed to start.")]
    private static partial void LogHostStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Application host did not stop cleanly.")]
    private static partial void LogHostStopFailed(ILogger logger, Exception exception);

    // async void is acceptable for these two top-level event handlers: every exception is caught and logged.
    private async void OnStartup(object? sender, ControlledApplicationLifetimeStartupEventArgs e)
    {
        try
        {
            await Host!.StartAsync();
        }
        catch (Exception ex)
        {
            LogHostStartFailed(Logger, ex);
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(1);
        }
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_hostStopped)
        {
            return;
        }

        // Hold the shutdown until hosted services have stopped, without blocking the UI thread.
        e.Cancel = true;
        _hostStopped = true;
        try
        {
            await Host!.StopAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            LogHostStopFailed(Logger, ex);
        }

        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
