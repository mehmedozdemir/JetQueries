using Avalonia;
using JetQueries.Core;
using JetQueries.Desktop.Services;
using JetQueries.Desktop.ViewModels;
using JetQueries.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JetQueries.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var host = CreateHost(args);
        return BuildAvaloniaApp(host).StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Entry point for the XAML previewer, which runs without a host.</summary>
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null);

    private static AppBuilder BuildAvaloniaApp(IHost? host) =>
        AppBuilder.Configure(() => new App { Host = host })
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static IHost CreateHost(string[] args)
    {
        // The empty builder skips appsettings file watchers and the console lifetime: nothing a desktop app
        // needs, and it keeps cold start short.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });
        builder.Logging.AddDebug();

        builder.Services.AddJetQueriesCore();
        builder.Services.AddSingleton<IThemeService, ThemeService>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        return builder.Build();
    }
}
