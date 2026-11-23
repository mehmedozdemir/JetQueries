using JetQueries.Core.Connections;

namespace JetQueries.Presentation.Services;

/// <summary>Opens modal dialogs for view models, which must not know about windows.</summary>
public interface IDialogService
{
    /// <summary>Shows the connection editor.</summary>
    /// <param name="existing">The connection to edit, or <see langword="null"/> for a new one.</param>
    /// <returns><see langword="true"/> if the connection was saved.</returns>
    Task<bool> EditConnectionAsync(ConnectionDefinition? existing);

    /// <summary>Asks for the password of a connection that has none stored.</summary>
    /// <param name="connectionName">Name shown in the prompt.</param>
    /// <returns>The password, or <see langword="null"/> if the user canceled.</returns>
    Task<string?> PromptPasswordAsync(string connectionName);

    /// <summary>Asks a yes/no question.</summary>
    /// <param name="title">Window title.</param>
    /// <param name="message">The question.</param>
    /// <returns><see langword="true"/> if the user confirmed.</returns>
    Task<bool> ConfirmAsync(string title, string message);
}
