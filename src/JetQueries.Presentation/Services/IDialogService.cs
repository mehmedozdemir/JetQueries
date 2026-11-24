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

    /// <summary>"Commit or roll back the open transaction of …?" (Cancel keeps it open and stops what asked).</summary>
    /// <param name="documentNames">The query tab, or several tabs, holding open transactions.</param>
    Task<TransactionChoice> AskOpenTransactionAsync(string documentNames);

    /// <summary>Shows Import connections (DBeaver, SSMS); true when something was imported.</summary>
    Task<bool> ImportConnectionsAsync();

    /// <summary>Asks for a line of text (a folder name); null when canceled.</summary>
    /// <param name="title">The window title.</param>
    /// <param name="message">What to enter.</param>
    /// <param name="initial">The text the box starts with.</param>
    Task<string?> PromptTextAsync(string title, string message, string initial) => Task.FromResult<string?>(null);

    /// <summary>Shows the Export dialog of a result; true when the user chose to export.</summary>
    /// <param name="options">The dialog's view model.</param>
    Task<bool> ExportResultAsync(ViewModels.ExportResultViewModel options) => Task.FromResult(false);

    /// <summary>Opens a file with the application Windows associates with it (an exported result).</summary>
    /// <param name="path">The file.</param>
    void OpenWithShell(string path)
    {
    }

    /// <summary>The user's documents folder, where exports are offered.</summary>
    string DocumentsFolder => Path.GetTempPath();

    /// <summary>Shows the Execute dialog of a stored procedure or function (its parameters are read while it is open).</summary>
    /// <param name="routine">The dialog's view model.</param>
    Task<ViewModels.RoutineExecutionChoice> ExecuteRoutineAsync(ViewModels.ExecuteRoutineViewModel routine) =>
        Task.FromResult(ViewModels.RoutineExecutionChoice.Cancel);
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

/// <summary>What to do with an open transaction.</summary>
public enum TransactionChoice
{
    /// <summary>Keep it open; stop what asked.</summary>
    Cancel,

    /// <summary>Commit it.</summary>
    Commit,

    /// <summary>Roll it back.</summary>
    Rollback,
}
