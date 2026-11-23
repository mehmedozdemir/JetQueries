using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using JetQueries.Core.Connections;
using JetQueries.Core.Extensions;
using JetQueries.Core.Jobs;
using JetQueries.Wpf.ViewModels;
using JetQueries.Wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UiKit.Theming;

namespace JetQueries.Wpf.Services;

/// <summary>The WPF UI dispatcher.</summary>
internal sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    public void Post(Action action) => _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
}

/// <summary>Light or dark theme; UiKit's theme manager swaps the colour tokens and remembers the choice.</summary>
public interface IThemeService
{
    /// <summary>Whether the dark theme is showing.</summary>
    bool IsDark { get; }

    /// <summary>Raised after the theme changed.</summary>
    event EventHandler? Changed;

    /// <summary>Switches between light and dark.</summary>
    void Toggle();
}

internal sealed class ThemeService : IThemeService
{
    public ThemeService() => ThemeManager.ThemeChanged += () => Changed?.Invoke(this, EventArgs.Empty);

    public event EventHandler? Changed;

    public bool IsDark => ThemeManager.Current == AppTheme.Dark;

    public void Toggle() => ThemeManager.Toggle();
}

/// <summary>Shows dialogs owned by the main window. Must be called on the UI thread.</summary>
internal sealed class WpfDialogService(IServiceProvider services) : IDialogService
{
    // Resolved on use: the main window's own view model depends, indirectly, on this service.
    private Window Owner => services.GetRequiredService<MainWindow>();

    public async Task<bool> EditConnectionAsync(ConnectionDefinition? existing)
    {
        var extensions = services.GetRequiredService<IExtensionHost>();

        // Extensions load in the background at start-up; the provider list needs them.
        await extensions.LoadAsync(CancellationToken.None);
        var viewModel = new ConnectionEditorViewModel(
            existing,
            extensions.DatabaseProviders,
            services.GetRequiredService<IConnectionStore>(),
            services.GetRequiredService<ICredentialStore>(),
            services.GetRequiredService<IConnectionManager>(),
            services.GetRequiredService<IJobQueue>());
        return new ConnectionDialog(viewModel) { Owner = Owner }.ShowDialog() == true;
    }

    public Task<string?> PromptPasswordAsync(string connectionName)
    {
        var dialog = new PromptDialog(
            Strings.PasswordPromptTitle,
            string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.PasswordPromptMessage), connectionName),
            askForSecret: true) { Owner = Owner };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Secret : null);
    }

    public Task<bool> ConfirmAsync(string title, string message) =>
        Task.FromResult(new PromptDialog(title, message, askForSecret: false) { Owner = Owner }.ShowDialog() == true);
}

/// <summary>
/// Starts loading extensions as a background job when the application starts. The window is already visible
/// by then; nothing waits for the result.
/// </summary>
internal sealed class ExtensionStartupService(IJobQueue jobs, IExtensionHost extensions, MainWindowViewModel shell) : IHostedService
{
    private JobHandle? _job;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _job = jobs.Enqueue(
            "Load extensions",
            async context =>
            {
                var results = await extensions.LoadAsync(context.CancellationToken).ConfigureAwait(false);
                var loaded = results.Count(static r => r.State == ExtensionState.Loaded);
                shell.ShowExtensions(loaded, results.Count - loaded);
            },
            JobPriority.High,
            CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _job?.Cancel();
        return Task.CompletedTask;
    }
}
