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

    /// <summary>Asks for an SQL file to open.</summary>
    /// <returns>The path, or <see langword="null"/> if the user canceled.</returns>
    Task<string?> PickFileToOpenAsync();

    /// <summary>Asks where to save a query.</summary>
    /// <param name="suggestedName">The file name offered, such as "Query 1.sql".</param>
    /// <param name="currentPath">The file the query came from, if any: its folder is offered.</param>
    /// <returns>The path, or <see langword="null"/> if the user canceled.</returns>
    Task<string?> PickFileToSaveAsync(string suggestedName, string? currentPath);

    /// <summary>Asks what to do with a query's unsaved changes before its tab closes.</summary>
    /// <param name="documentName">The tab's name.</param>
    Task<SaveChangesChoice> AskSaveChangesAsync(string documentName);
}

/// <summary>The answer to "save changes?".</summary>
public enum SaveChangesChoice
{
    /// <summary>Save, then go on.</summary>
    Save,

    /// <summary>Go on without saving.</summary>
    Discard,

    /// <summary>Do not go on.</summary>
    Cancel,
}
