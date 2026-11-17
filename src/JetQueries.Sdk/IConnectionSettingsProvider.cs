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
}

/// <summary>An allowed value of a choice setting.</summary>
/// <param name="Value">The value stored in the connection profile.</param>
/// <param name="DisplayName">English label.</param>
public sealed record ConnectionSettingOption(string Value, string DisplayName);

/// <summary>A TCP endpoint.</summary>
/// <param name="Host">Host name or IP address.</param>
/// <param name="Port">TCP port.</param>
public sealed record NetworkEndpoint(string Host, int Port);
