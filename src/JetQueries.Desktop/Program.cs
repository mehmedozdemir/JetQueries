using Avalonia;
using JetQueries.Core;
using JetQueries.Desktop.Services;
using JetQueries.Desktop.ViewModels;
using JetQueries.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;

namespace JetQueries.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run before anything else: during install, update and uninstall Velopack starts the application
        // with hook arguments, handles them here and exits. In a normal start it returns immediately.
        VelopackApp.Build().Run();

        using var host = CreateHost(args);
        return BuildAvaloniaApp(host).StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Entry point for the XAML previewer, which runs without a host.</summary>
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null);

    private static AppBuilder BuildAvaloniaApp(IHost? host) =>
        AppBuilder.Configure(() => new App { Host = host })
            .UsePlatformDetect()
            .With(CreateWin32Options())
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Chooses how the window is presented on Windows. The low-latency DXGI swap chain comes first: measured on
    /// the grid spike, Avalonia's default (WinUI composition) skipped 6-75 % of display refreshes whenever a
    /// large area was repainted, the swap chain about 1 % (docs/adr/0002). The cost is no window transparency
    /// or acrylic, which the design does not use. The remaining modes are fallbacks for older systems.
    /// </summary>
    /// <remarks>
    /// <c>JETQUERIES_COMPOSITION</c> and <c>JETQUERIES_RENDERING</c> override the preference lists with
    /// comma-separated <see cref="Win32CompositionMode"/> / <see cref="Win32RenderingMode"/> names, for
    /// measuring and for working around GPU driver problems. Unrecognized names are ignored.
    /// </remarks>
    private static Win32PlatformOptions CreateWin32Options()
    {
        var options = new Win32PlatformOptions
        {
            CompositionMode =
            [
                Win32CompositionMode.LowLatencyDxgiSwapChain,
                Win32CompositionMode.WinUIComposition,
                Win32CompositionMode.DirectComposition,
                Win32CompositionMode.RedirectionSurface,
            ],
        };

        if (ParseModes<Win32CompositionMode>("JETQUERIES_COMPOSITION") is { Count: > 0 } composition)
        {
            options.CompositionMode = composition;
        }

        if (ParseModes<Win32RenderingMode>("JETQUERIES_RENDERING") is { Count: > 0 } rendering)
        {
            options.RenderingMode = rendering;
        }

        return options;
    }

    private static List<T> ParseModes<T>(string variable)
        where T : struct, Enum
    {
        var modes = new List<T>();
        var value = Environment.GetEnvironmentVariable(variable) ?? string.Empty;
        foreach (var name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<T>(name, ignoreCase: true, out var mode) && Enum.IsDefined(mode))
            {
                modes.Add(mode);
            }
        }

        return modes;
    }

    private static IHost CreateHost(string[] args)
    {
        // The empty builder skips appsettings file watchers and the console lifetime: nothing a desktop app
        // needs, and it keeps cold start short.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });
        builder.Logging.AddDebug();

        var localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JetQueries");
        builder.Services.AddJetQueriesCore(
            extensions =>
            {
                // Built-in extensions ship next to the executable; the user's own live in the profile.
                extensions.Directories.Add(Path.Combine(AppContext.BaseDirectory, "extensions"));
                extensions.Directories.Add(Path.Combine(localData, "extensions"));
            },
            cache => cache.DatabasePath = Path.Combine(localData, "cache", "metadata.db"));
        builder.Services.AddHostedService<ExtensionStartupService>();
        builder.Services.AddSingleton<IThemeService, ThemeService>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        return builder.Build();
    }
}
