namespace JetQueries.Sdk;

/// <summary>What a connection string said, as a provider's own settings plus the credential it carried.</summary>
/// <remarks>Preview: part of SDK contract version 0.</remarks>
public sealed record ParsedConnectionString
{
    /// <summary>The settings (as <see cref="IConnectionSettingsProvider"/> defines them) the string gave.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The user name, if the string had one.</summary>
    public string? UserName { get; init; }

    /// <summary>
    /// The password, if the string had one. The host hands it to the OS credential store only; it never becomes a
    /// setting.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>Keywords of the string that have no matching setting and were left out (in the string's spelling).</summary>
    public IReadOnlyList<string> Ignored { get; init; } = [];
}

/// <summary>
/// Optional feature of an <see cref="IDatabaseProvider"/>: reads a connection string (in the driver's own syntax,
/// and where the provider supports it a URL such as a JDBC URL) into the provider's connection settings.
/// </summary>
/// <remarks>Preview: part of SDK contract version 0. Pure: no I/O.</remarks>
public interface IConnectionStringImport
{
    /// <summary>Reads <paramref name="connectionString"/>.</summary>
    /// <param name="connectionString">The text the user pasted. It may hold a password: never log it.</param>
    /// <returns>The settings and credential it gave.</returns>
    /// <exception cref="FormatException">The text is not a connection string this provider understands; the message says why.</exception>
    ParsedConnectionString ParseConnectionString(string connectionString);
}
