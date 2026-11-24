namespace JetQueries.Sdk;

/// <summary>
/// Optional interface of an <see cref="IDatabaseProvider"/>: describes the settings a connection to this database
/// needs, so the host can show a connection form and check reachability without knowing the provider.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0. All members are pure and must not do I/O.</remarks>
public interface IConnectionSettingsProvider
{
    /// <summary>The settings to ask the user for, in display order. Keys match <see cref="ConnectionProfile.Settings"/>.</summary>
    IReadOnlyList<ConnectionSettingDescriptor> ConnectionSettings { get; }

    /// <summary>Whether a connection with these settings needs a user name and password from the host.</summary>
    /// <param name="settings">The current values of the settings.</param>
    /// <returns><see langword="false"/> for integrated or file-based authentication.</returns>
    bool RequiresCredential(IReadOnlyDictionary<string, string> settings);

    /// <summary>
    /// The TCP endpoint a connection with these settings would use, for a quick reachability check before the
    /// driver's own (slower) connection attempt.
    /// </summary>
    /// <param name="settings">The current values of the settings.</param>
    /// <returns>The endpoint, or <see langword="null"/> if it cannot be told from the settings alone.</returns>
    NetworkEndpoint? GetEndpoint(IReadOnlyDictionary<string, string> settings);

    /// <summary>
    /// The settings changed to connect to another endpoint: the local end of an SSH tunnel the host opened to the
    /// endpoint of <see cref="GetEndpoint"/>. Everything else (database, options) stays.
    /// </summary>
    /// <param name="settings">The connection's settings.</param>
    /// <param name="endpoint">Where to connect instead (127.0.0.1 and a local port).</param>
    /// <returns>The new settings, or <see langword="null"/> when the provider cannot connect through a tunnel.</returns>
    /// <remarks>Preview; defaults to <see langword="null"/>.</remarks>
    IReadOnlyDictionary<string, string>? WithEndpoint(IReadOnlyDictionary<string, string> settings, NetworkEndpoint endpoint) => null;
}

/// <summary>One setting of a connection, as shown in the connection form.</summary>
/// <param name="Key">The key in <see cref="ConnectionProfile.Settings"/>.</param>
/// <param name="DisplayName">English label; the host may replace it with a translation it has for <paramref name="Key"/>.</param>
/// <param name="Kind">How the value is edited.</param>
public sealed record ConnectionSettingDescriptor(string Key, string DisplayName, ConnectionSettingKind Kind)
{
    /// <summary>Whether a connection cannot be made without this setting.</summary>
    public bool IsRequired { get; init; }

    /// <summary>The value used when the setting is absent, shown as the initial value; <see langword="null"/> for none.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>The allowed values of a <see cref="ConnectionSettingKind.Choice"/> setting.</summary>
    public IReadOnlyList<ConnectionSettingOption> Options { get; init; } = [];

    /// <summary>Optional English hint shown next to the field (for example the expected format).</summary>
    public string? Hint { get; init; }

    /// <summary>
    /// Shows the setting only while another setting has a given value (an Oracle TNS alias only for the "TNS"
    /// connection type); a hidden setting is neither required nor saved. <see langword="null"/>: always shown.
    /// </summary>
    /// <remarks>Preview.</remarks>
    public ConnectionSettingCondition? VisibleWhen { get; init; }

    /// <summary>
    /// Whether the provider can suggest values for this setting through
    /// <see cref="IConnectionSettingSuggestions"/>; the host then offers them in a list the user may still type over.
    /// </summary>
    /// <remarks>Preview.</remarks>
    public bool HasSuggestions { get; init; }
}

/// <summary>A condition on another setting: its value equals <paramref name="Value"/> (ignoring case).</summary>
/// <param name="Key">The other setting's key.</param>
/// <param name="Value">The value it must have.</param>
/// <remarks>Preview.</remarks>
public sealed record ConnectionSettingCondition(string Key, string Value)
{
    /// <summary>Whether the condition holds for these values; an absent setting counts as its default.</summary>
    /// <param name="settings">The current values.</param>
    /// <param name="defaultValue">The other setting's default.</param>
    public bool IsMet(IReadOnlyDictionary<string, string> settings, string? defaultValue = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var value = settings.TryGetValue(Key, out var current) && !string.IsNullOrWhiteSpace(current) ? current.Trim() : defaultValue ?? string.Empty;
        return string.Equals(value, Value, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Optional interface of an <see cref="IDatabaseProvider"/>: values to suggest for settings marked
/// <see cref="ConnectionSettingDescriptor.HasSuggestions"/> (the aliases of a tnsnames.ora file). May read local
/// files; the host calls it off the UI thread.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0.</remarks>
public interface IConnectionSettingSuggestions
{
    /// <summary>The values to suggest for a setting.</summary>
    /// <param name="key">The setting's key.</param>
    /// <param name="settings">The current values of the other settings (a folder to read from, for example).</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>The suggestions, possibly none.</returns>
    Task<IReadOnlyList<ConnectionSettingOption>> GetSuggestionsAsync(string key, IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken);
}

/// <summary>How a connection setting is edited.</summary>
public enum ConnectionSettingKind
{
    /// <summary>Free text.</summary>
    Text,

    /// <summary>A whole number.</summary>
    Number,

    /// <summary><c>true</c> or <c>false</c>.</summary>
    Boolean,

    /// <summary>One of <see cref="ConnectionSettingDescriptor.Options"/>.</summary>
    Choice,

    /// <summary>A path to a local file; the host offers to browse for it.</summary>
    FilePath,

    /// <summary>A path to a local folder; the host offers to browse for it.</summary>
    /// <remarks>Preview.</remarks>
    FolderPath,
}

/// <summary>An allowed value of a choice setting.</summary>
/// <param name="Value">The value stored in the connection profile.</param>
/// <param name="DisplayName">English label.</param>
public sealed record ConnectionSettingOption(string Value, string DisplayName);

/// <summary>A TCP endpoint.</summary>
/// <param name="Host">Host name or IP address.</param>
/// <param name="Port">TCP port.</param>
public sealed record NetworkEndpoint(string Host, int Port);
