using System.Globalization;
using System.Text;
using Avalonia.Controls;
using JetQueries.Core.Connections;
using JetQueries.Core.Extensions;
using JetQueries.Core.Jobs;
using JetQueries.Presentation.Resources;
using JetQueries.Desktop.ViewModels;
using JetQueries.Presentation.ViewModels;
using JetQueries.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace JetQueries.Desktop.Services;

/// <summary>Shows dialogs owned by the main window. Must be called on the UI thread.</summary>
internal sealed class DialogService(IServiceProvider services) : IDialogService
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
        return await new ConnectionDialog(viewModel).ShowDialog<bool>(Owner);
    }

    public Task<string?> PromptPasswordAsync(string connectionName) =>
        new PromptDialog(
            Strings.PasswordPromptTitle,
            string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(Strings.PasswordPromptMessage), connectionName),
            askForSecret: true).ShowDialog<string?>(Owner);

    public async Task<bool> ConfirmAsync(string title, string message) =>
        await new PromptDialog(title, message, askForSecret: false).ShowDialog<string?>(Owner) is not null;
}
