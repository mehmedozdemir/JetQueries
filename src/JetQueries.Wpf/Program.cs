using System.IO;
using JetQueries.Core;
using JetQueries.Wpf.Services;
using JetQueries.Wpf.ViewModels;
using JetQueries.Wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;

namespace JetQueries.Wpf;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run before anything else: during install, update and uninstall Velopack starts the application
        // with hook arguments, handles them here and exits. In a normal start it returns immediately.
        VelopackApp.Build().Run();

        using var host = CreateHost(args);
        var app = new App { Host = host };
        app.InitializeComponent();
        return app.Run();
    }

    internal static IHost CreateHost(string[] args)
    {
        // The empty builder skips appsettings file watchers and the console lifetime: nothing a desktop app
        // needs, and it keeps cold start short.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });
        builder.Logging.AddDebug();

        var localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JetQueries");
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JetQueries");
        builder.Services.AddJetQueriesCore(
            extensions =>
            {
                // Built-in extensions ship next to the executable; the user's own live in the profile.
                extensions.Directories.Add(Path.Combine(AppContext.BaseDirectory, "extensions"));
                extensions.Directories.Add(Path.Combine(localData, "extensions"));
            },
            cache => cache.DatabasePath = Path.Combine(localData, "cache", "metadata.db"),

            // Roaming profile, not the local one: the installer owns %LocalAppData%\JetQueries and removes it
            // on uninstall, and the user's connections and history must survive that (ADR 0005).
            connections => connections.FilePath = Path.Combine(userData, "connections.json"),
            history => history.DatabasePath = Path.Combine(userData, "history.db"));
        builder.Services.AddSingleton<IDialogService, WpfDialogService>();
        builder.Services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        builder.Services.AddSingleton<ConnectionsViewModel>();
        builder.Services.AddSingleton<DocumentsViewModel>();
        builder.Services.AddSingleton<HistoryViewModel>();
        builder.Services.AddSingleton<IThemeService, ThemeService>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddHostedService<ExtensionStartupService>();
        return builder.Build();
    }
}
