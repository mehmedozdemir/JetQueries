using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace JetQueries.Core.Connections;

/// <summary>
/// <see cref="IConnectionStore"/> on one JSON file. The file has no field for a password, so a secret cannot
/// end up in it. Writes go to a temporary file first and replace the original in one step, so a crash never
/// leaves a half-written file. An unreadable file is set aside, not overwritten.
/// </summary>
public sealed partial class JsonConnectionStore : IConnectionStore, IDisposable
{
    private const int FileVersion = 1;

    private readonly string? _path;
    private readonly ILogger<JsonConnectionStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<ConnectionDefinition>? _connections;

    /// <summary>Creates the store. The file is read on first use.</summary>
    /// <param name="options">Location of the file.</param>
    /// <param name="logger">Receives a warning when the file cannot be read.</param>
    public JsonConnectionStore(ConnectionStoreOptions options, ILogger<JsonConnectionStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _path = options.FilePath;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConnectionDefinition>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connections = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return [.. connections
                .OrderBy(static c => c.Folder, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static c => c.Name, StringComparer.OrdinalIgnoreCase)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(ConnectionDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connections = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var updated = new List<ConnectionDefinition>(connections);
            var index = updated.FindIndex(c => c.Id == definition.Id);
            if (index >= 0)
            {
                updated[index] = definition;
            }
            else
            {
                updated.Add(definition);
            }

            await WriteAsync(updated, cancellationToken).ConfigureAwait(false);
            _connections = updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connections = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var updated = connections.Where(c => c.Id != id).ToList();
            if (updated.Count != connections.Count)
            {
                await WriteAsync(updated, cancellationToken).ConfigureAwait(false);
                _connections = updated;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task<List<ConnectionDefinition>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_connections is not null)
        {
            return _connections;
        }

        if (_path is null || !File.Exists(_path))
        {
            return _connections = [];
        }

        try
        {
            var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            ConnectionFile? file;
            await using (stream.ConfigureAwait(false))
            {
                file = await JsonSerializer.DeserializeAsync(stream, ConnectionJson.Default.ConnectionFile, cancellationToken).ConfigureAwait(false);
            }

            return _connections = [.. (file?.Connections ?? []).Where(static c => c.Id != Guid.Empty && !string.IsNullOrWhiteSpace(c.ProviderId))];
        }
        catch (JsonException ex)
        {
            // The user's connections are in there somewhere: keep the file for recovery instead of overwriting it.
            var aside = _path + ".unreadable";
            File.Copy(_path, aside, overwrite: true);
            LogUnreadable(_path, aside, ex);
            return _connections = [];
        }
    }

    private async Task WriteAsync(List<ConnectionDefinition> connections, CancellationToken cancellationToken)
    {
        if (_path is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _path + ".tmp";
        var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, new ConnectionFile { Version = FileVersion, Connections = connections }, ConnectionJson.Default.ConnectionFile, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporary, _path, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection file {Path} could not be read; it was copied to {Aside} and an empty list is used.")]
    private partial void LogUnreadable(string path, string aside, Exception exception);

    private sealed class ConnectionFile
    {
        public int Version { get; set; }

        public List<ConnectionDefinition> Connections { get; set; } = [];
    }

    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UseStringEnumConverter = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(ConnectionFile))]
    private sealed partial class ConnectionJson : JsonSerializerContext;
}
