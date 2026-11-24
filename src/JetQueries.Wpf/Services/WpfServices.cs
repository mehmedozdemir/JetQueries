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
internal sealed class ThemeService : IThemeService
{
    public ThemeService() => ThemeManager.ThemeChanged += () => Changed?.Invoke(this, EventArgs.Empty);

    public event EventHandler? Changed;

    public bool IsDark => ThemeManager.Current == AppTheme.Dark;

    public void Toggle() => ThemeManager.Toggle();

    public void SetDark(bool dark) => ThemeManager.Apply(dark ? AppTheme.Dark : AppTheme.Light);

    public string SchemeOf(bool dark) => dark ? ThemeManager.DarkScheme : ThemeManager.LightScheme;

    public void SetScheme(bool dark, string scheme) => ThemeManager.SetScheme(dark ? AppTheme.Dark : AppTheme.Light, scheme);
}

/// <summary>
/// Puts appearance settings into the application's resources: the UI font and the text-size tokens UiKit's
/// styles read (body, small, caption, section, subtitle, title), the editor's font and size, and the grids'
/// scale. Everything that uses them is a DynamicResource, so the whole window follows at once.
/// </summary>
public sealed class AppearanceService : IAppearanceService
{
    /// <summary>The grids' size relative to UiKit's 13 px design, from the grid text size setting.</summary>
    public static double GridScale { get; private set; } = 1;

    /// <summary>Whether result headers show the data type under the column name.</summary>
    public static bool ShowColumnTypes { get; private set; } = true;

    /// <summary>Whether the compact density is in effect (result rows are lower too).</summary>
    public static bool Compact { get; private set; }

    /// <summary>Raised on the UI thread after the appearance changed.</summary>
    public static event EventHandler? Changed;

    public AppearanceSettings Current { get; private set; } = new();

    public void Apply(AppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalized();
        Current = settings;
        var resources = Application.Current.Resources;
        var size = settings.UiFontSize;
        resources["Font.UI"] = new System.Windows.Media.FontFamily(settings.UiFontFamily);
        resources["FontSize.Body"] = size;
        resources["FontSize.Small"] = size - 1;
        resources["FontSize.Caption"] = size - 1.5;
        resources["FontSize.Section"] = size - 2;
        resources["FontSize.Subtitle"] = size + 2;
        resources["FontSize.Title"] = size + 9;
        resources["Font.Code"] = new System.Windows.Media.FontFamily(settings.EditorFontFamily);
        resources["FontSize.Code"] = settings.EditorFontSize;
        GridScale = settings.GridFontSize / 13;
        ShowColumnTypes = settings.ShowColumnTypes;

        // Density: UiKit's control sizes, then the application's own tighter ones, one step smaller in compact.
        Compact = settings.CompactMode;
        UiKit.Theming.Density.Apply(Compact ? UiKit.Theming.UiDensity.Compact : UiKit.Theming.UiDensity.Normal);
        resources["Pad.AppToolbar"] = Compact ? new Thickness(8, 3, 8, 3) : new Thickness(10, 5, 10, 5);
        resources["Size.AppToolbarButton"] = Compact ? 26.0 : 30.0;
        resources["Size.AppIcon"] = Compact ? 26.0 : 30.0;
        resources["Size.Footer"] = Compact ? 22.0 : 26.0;
        resources["Pad.AppTab"] = Compact ? new Thickness(8, 3, 8, 3) : new Thickness(10, 6, 10, 6);
        resources["Pad.AppTreeItem"] = Compact ? new Thickness(2, 1, 8, 1) : new Thickness(2, 2, 8, 2);
        resources["Space.Page.Compact"] = Compact ? new Thickness(6, 4, 6, 0) : new Thickness(8, 6, 8, 0);

        // Run, Cancel, Save: icons in each tab, or text buttons in the main toolbar.
        resources["QueryButtons.Tab"] = settings.QueryButtonsInTab ? Visibility.Visible : Visibility.Collapsed;
        resources["QueryButtons.Toolbar"] = settings.QueryButtonsInTab ? Visibility.Collapsed : Visibility.Visible;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Opens the settings screen.</summary>
public interface ISettingsDialog
{
    /// <summary>Shows the settings screen over the main window.</summary>
    void Show();
}

/// <summary>Shows the settings window over the main window.</summary>
internal sealed class SettingsDialog(IServiceProvider services) : ISettingsDialog
{
    public void Show()
    {
        var fonts = System.Windows.Media.Fonts.SystemFontFamilies.Select(static f => f.Source)
            .Append(new AppearanceSettings().UiFontFamily)
            .Append(new AppearanceSettings().EditorFontFamily);
        var viewModel = new SettingsViewModel(
            services.GetRequiredService<Core.Settings.ISettingsStore>(),
            services.GetRequiredService<IJobQueue>(),
            services.GetRequiredService<IAppearanceService>(),
            services.GetRequiredService<IThemeService>(),
            fonts,
            PreviewLanguage(),
            services.GetService<ISnippetService>(),
            services.GetService<UpdatesViewModel>());
        new SettingsWindow(viewModel) { Owner = services.GetRequiredService<MainWindow>() }.ShowDialog();
    }

    /// <summary>A loaded provider's language for the previews, preferring one that can format (SQL Server first).</summary>
    private Sdk.ISqlLanguageService? PreviewLanguage()
    {
        var languages = services.GetRequiredService<IExtensionHost>().DatabaseProviders
            .OrderBy(static p => p.Id.Equals("sqlserver", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(static p => p.LanguageService)
            .OfType<Sdk.ISqlLanguageService>()
            .ToList();
        return languages.FirstOrDefault(static l => l is Sdk.ISqlFormatter) ?? languages.FirstOrDefault();
    }
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

    public Task<string?> PromptTextAsync(string title, string message, string initial)
    {
        var dialog = new PromptDialog(title, message, initial) { Owner = Owner };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Text : null);
    }

    public Task<RoutineExecutionChoice> ExecuteRoutineAsync(ExecuteRoutineViewModel routine)
    {
        var window = new ExecuteRoutineWindow(routine) { Owner = Owner };
        return Task.FromResult(window.ShowDialog() == true ? window.Choice : RoutineExecutionChoice.Cancel);
    }

    public Task<bool> ConfirmAsync(string title, string message) =>
        Task.FromResult(new PromptDialog(title, message, askForSecret: false) { Owner = Owner }.ShowDialog() == true);

    private static string FileFilter => $"{Strings.SqlFiles} (*.sql)|*.sql|{Strings.AllFiles} (*.*)|*.*";

    public Task<string?> PickFileToOpenAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = FileFilter, CheckFileExists = true };
        return Task.FromResult(dialog.ShowDialog(Owner) == true ? dialog.FileName : null);
    }

    public Task<string?> PickFileToSaveAsync(string suggestedName, string? currentPath)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = FileFilter,
            FileName = suggestedName,
            DefaultExt = ".sql",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = currentPath is null ? string.Empty : System.IO.Path.GetDirectoryName(currentPath) ?? string.Empty,
        };
        return Task.FromResult(dialog.ShowDialog(Owner) == true ? dialog.FileName : null);
    }

    public Task<SaveChangesChoice> AskSaveChangesAsync(string documentName)
    {
        var dialog = new SaveChangesDialog(documentName) { Owner = Owner };
        dialog.ShowDialog();
        return Task.FromResult(dialog.Choice);
    }

    public async Task<bool> ImportConnectionsAsync()
    {
        var extensions = services.GetRequiredService<IExtensionHost>();
        await extensions.LoadAsync(CancellationToken.None);
        var viewModel = new ImportConnectionsViewModel(
            extensions.DatabaseProviders, services.GetRequiredService<Core.Connections.IConnectionStore>(), services.GetRequiredService<IJobQueue>());
        var window = new ImportConnectionsWindow(viewModel) { Owner = Owner };
        window.ShowDialog();
        return window.Imported;
    }

    public Task<TransactionChoice> AskOpenTransactionAsync(string documentNames)
    {
        var dialog = new TransactionDialog(documentNames) { Owner = Owner };
        dialog.ShowDialog();
        return Task.FromResult(dialog.Choice);
    }
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
                shell.ShowProviders(extensions.DatabaseProviders);
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
