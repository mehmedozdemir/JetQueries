using System.Globalization;
using System.Resources;

namespace JetQueries.Desktop.Resources;

/// <summary>
/// Typed access to the user-facing strings in <c>Strings.resx</c> (English) and <c>Strings.tr.resx</c> (Turkish).
/// The language follows <see cref="CultureInfo.CurrentUICulture"/>.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Manager = new(typeof(Strings));

    public static string ToggleTheme => Get(nameof(ToggleTheme));
    public static string ExplorerTitle => Get(nameof(ExplorerTitle));
    public static string ExplorerPlaceholder => Get(nameof(ExplorerPlaceholder));
    public static string DocumentsTitle => Get(nameof(DocumentsTitle));
    public static string DocumentsPlaceholder => Get(nameof(DocumentsPlaceholder));
    public static string ResultsTitle => Get(nameof(ResultsTitle));
    public static string ResultsPlaceholder => Get(nameof(ResultsPlaceholder));
    public static string StatusReady => Get(nameof(StatusReady));
    public static string ExtensionsLoaded => Get(nameof(ExtensionsLoaded));
    public static string ExtensionsLoadedWithFailures => Get(nameof(ExtensionsLoadedWithFailures));
    public static string GridSpikeStream => Get(nameof(GridSpikeStream));
    public static string GridSpikeBenchmark => Get(nameof(GridSpikeBenchmark));
    public static string GridSpikeRowCount => Get(nameof(GridSpikeRowCount));

    public static string ConnectionNew => Get(nameof(ConnectionNew));
    public static string ConnectionEdit => Get(nameof(ConnectionEdit));
    public static string ConnectionDelete => Get(nameof(ConnectionDelete));
    public static string ConnectionConnect => Get(nameof(ConnectionConnect));
    public static string ConnectionDisconnect => Get(nameof(ConnectionDisconnect));
    public static string ConnectionsEmpty => Get(nameof(ConnectionsEmpty));
    public static string ConnectionReadOnlyBadge => Get(nameof(ConnectionReadOnlyBadge));
    public static string ConnectionConnectedBadge => Get(nameof(ConnectionConnectedBadge));
    public static string ConnectionDialogTitleNew => Get(nameof(ConnectionDialogTitleNew));
    public static string ConnectionDialogTitleEdit => Get(nameof(ConnectionDialogTitleEdit));
    public static string FieldName => Get(nameof(FieldName));
    public static string FieldProvider => Get(nameof(FieldProvider));
    public static string FieldFolder => Get(nameof(FieldFolder));
    public static string FieldFolderHint => Get(nameof(FieldFolderHint));
    public static string FieldEnvironment => Get(nameof(FieldEnvironment));
    public static string FieldReadOnly => Get(nameof(FieldReadOnly));
    public static string FieldUserName => Get(nameof(FieldUserName));
    public static string FieldPassword => Get(nameof(FieldPassword));
    public static string FieldSavePassword => Get(nameof(FieldSavePassword));
    public static string PasswordUnchangedHint => Get(nameof(PasswordUnchangedHint));
    public static string PasswordNotPersistentHint => Get(nameof(PasswordNotPersistentHint));
    public static string EnvironmentUnspecified => Get(nameof(EnvironmentUnspecified));
    public static string EnvironmentDevelopment => Get(nameof(EnvironmentDevelopment));
    public static string EnvironmentTest => Get(nameof(EnvironmentTest));
    public static string EnvironmentProduction => Get(nameof(EnvironmentProduction));
    public static string ButtonTest => Get(nameof(ButtonTest));
    public static string ButtonCancelTest => Get(nameof(ButtonCancelTest));
    public static string ButtonSave => Get(nameof(ButtonSave));
    public static string ButtonCancel => Get(nameof(ButtonCancel));
    public static string ButtonOk => Get(nameof(ButtonOk));
    public static string ConnectionTesting => Get(nameof(ConnectionTesting));
    public static string OutcomeSucceeded => Get(nameof(OutcomeSucceeded));
    public static string OutcomeProviderMissing => Get(nameof(OutcomeProviderMissing));
    public static string OutcomePasswordRequired => Get(nameof(OutcomePasswordRequired));
    public static string OutcomeUnreachable => Get(nameof(OutcomeUnreachable));
    public static string OutcomeFailed => Get(nameof(OutcomeFailed));
    public static string OutcomeCanceled => Get(nameof(OutcomeCanceled));
    public static string ValidationNameRequired => Get(nameof(ValidationNameRequired));
    public static string ValidationProviderRequired => Get(nameof(ValidationProviderRequired));
    public static string ValidationSettingRequired => Get(nameof(ValidationSettingRequired));
    public static string ValidationUserRequired => Get(nameof(ValidationUserRequired));
    public static string PasswordPromptTitle => Get(nameof(PasswordPromptTitle));
    public static string PasswordPromptMessage => Get(nameof(PasswordPromptMessage));
    public static string DeleteConfirmTitle => Get(nameof(DeleteConfirmTitle));
    public static string DeleteConfirmMessage => Get(nameof(DeleteConfirmMessage));

    /// <summary>A string looked up by a computed name (for example <c>Setting_server</c>), or null if there is none.</summary>
    public static string? Find(string name) => Manager.GetString(name, CultureInfo.CurrentUICulture);

    private static string Get(string name) => Manager.GetString(name, CultureInfo.CurrentUICulture) ?? name;
}
