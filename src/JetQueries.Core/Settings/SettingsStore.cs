using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace JetQueries.Core.Settings;

/// <summary>Where settings are kept.</summary>
public sealed class SettingsStoreOptions
{
    /// <summary>The folder of the settings files; null keeps settings in memory only (tests).</summary>
    public string? Directory { get; set; }

    /// <summary>
    /// Sections that may hold what the user typed (the open tabs' text) and so are written encrypted by
    /// <see cref="Protector"/>, as <c>{section}.bin</c>. Without a protector they are kept in memory only.
    /// </summary>
    public ISet<string> ProtectedSections { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Encrypts the <see cref="ProtectedSections"/>; null when the platform has none.</summary>
    public ILocalDataProtector? Protector { get; set; }
}

/// <summary>
/// The user's settings: one JSON file per section (<c>appearance.json</c>, …), read and written whole. Plain
/// sections never hold secrets; a section that may hold what the user typed is encrypted for the current user
/// (<see cref="SettingsStoreOptions.ProtectedSections"/>). A file that cannot be read gives the defaults; it is
/// left where it is.
/// </summary>
public interface ISettingsStore
{
    /// <summary>Reads a section; <paramref name="defaults"/> when it was never saved or cannot be read.</summary>
    /// <typeparam name="T">The section's type: a record with defaults for every property.</typeparam>
    /// <param name="section">The section name, also the file name.</param>
    /// <param name="defaults">The value without a saved file.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    Task<T> LoadAsync<T>(string section, T defaults, CancellationToken cancellationToken);

    /// <summary>Saves a section, replacing the file atomically.</summary>
    /// <typeparam name="T">The section's type.</typeparam>
    /// <param name="section">The section name.</param>
    /// <param name="value">The settings.</param>
    /// <param name="cancellationToken">Cancels writing.</param>
    Task SaveAsync<T>(string section, T value, CancellationToken cancellationToken);
}

/// <summary>JSON files in a folder of the user's profile.</summary>
public sealed partial class JsonSettingsStore(SettingsStoreOptions options, ILogger<JsonSettingsStore> logger) : ISettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Dictionary<string, string> _memory = new(StringComparer.Ordinal);
    private readonly Lock _memoryLock = new();

    /// <inheritdoc />
    public async Task<T> LoadAsync<T>(string section, T defaults, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(section);
        string? text;
        if (options.Directory is null)
        {
            lock (_memoryLock)
            {
                text = _memory.GetValueOrDefault(section);
            }
        }
        else if (IsProtected(section) && File.Exists(ProtectedPathOf(section)))
        {
            if (options.Protector is not { } protector)
            {
                return defaults;
            }

            try
            {
                var bytes = await File.ReadAllBytesAsync(ProtectedPathOf(section), cancellationToken).ConfigureAwait(false);
                text = Encoding.UTF8.GetString(protector.Unprotect(bytes));
            }
            catch (CryptographicException ex)
            {
                LogUnreadable(logger, section, ex);
                return defaults;
            }
        }
        else
        {
            // A protected section saved before it was protected is read once from its plain file; the next save
            // encrypts it and deletes the plain one.
            var path = PathOf(section);
            if (!File.Exists(path))
            {
                return defaults;
            }

            text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }

        if (text is null)
        {
            return defaults;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, Json) ?? defaults;
        }
        catch (JsonException ex)
        {
            LogUnreadable(logger, section, ex);
            return defaults;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync<T>(string section, T value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(section);
        var text = JsonSerializer.Serialize(value, Json);
        if (options.Directory is null || (IsProtected(section) && options.Protector is null))
        {
            lock (_memoryLock)
            {
                _memory[section] = text;
            }

            return;
        }

        Directory.CreateDirectory(options.Directory);
        if (IsProtected(section))
        {
            var bytes = options.Protector!.Protect(Encoding.UTF8.GetBytes(text));
            await WriteAtomicallyAsync(ProtectedPathOf(section), bytes, cancellationToken).ConfigureAwait(false);
            File.Delete(PathOf(section));
            return;
        }

        await WriteAtomicallyAsync(PathOf(section), Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private bool IsProtected(string section) => options.ProtectedSections.Contains(section);

    private string PathOf(string section) => Path.Combine(options.Directory!, section + ".json");

    private string ProtectedPathOf(string section) => Path.Combine(options.Directory!, section + ".bin");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings section {Section} could not be read; defaults are used.")]
    private static partial void LogUnreadable(ILogger logger, string section, Exception exception);
}
