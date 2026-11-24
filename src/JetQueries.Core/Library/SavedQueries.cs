using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace JetQueries.Core.Library;

/// <summary>
/// A query kept in the user's library: its text, a name and folder to find it by, and optionally the connection
/// and database it is meant for. Holds no secret (the text is the user's own SQL); synced later with the
/// connection definitions (ANALIZ: sync of saved queries, Phase 2).
/// </summary>
public sealed record SavedQuery
{
    /// <summary>Stable identity.</summary>
    public required Guid Id { get; init; }

    /// <summary>The name shown in the library.</summary>
    public required string Name { get; init; }

    /// <summary>The folder ("Raporlar/Aylık"); empty for the top level.</summary>
    public string Folder { get; init; } = string.Empty;

    /// <summary>What it is for; may be empty.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The SQL, with its parameters (<c>:name</c>, <c>${name}</c>) as written.</summary>
    public required string Sql { get; init; }

    /// <summary>The connection it runs on when opened; null for whichever is selected.</summary>
    public Guid? ConnectionId { get; init; }

    /// <summary>The database it runs in; null for the connection's default.</summary>
    public string? Database { get; init; }

    /// <summary>When it was saved first.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When it was saved last.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Options of <see cref="JsonSavedQueryStore"/>.</summary>
public sealed class SavedQueryStoreOptions
{
    /// <summary>Path of the JSON file; <see langword="null"/> keeps the queries in memory only.</summary>
    public string? FilePath { get; set; }
}

/// <summary>The user's saved queries.</summary>
public interface ISavedQueryStore
{
    /// <summary>All saved queries, by folder and name.</summary>
    Task<IReadOnlyList<SavedQuery>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Adds a query or replaces the one with its id.</summary>
    Task SaveAsync(SavedQuery query, CancellationToken cancellationToken);

    /// <summary>Removes a query; nothing happens when there is none with the id.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ISavedQueryStore"/> on one JSON file next to the connections (the roaming profile, ADR 0005).
/// Writes go to a temporary file that replaces the original in one step; an unreadable file is set aside.
/// </summary>
public sealed partial class JsonSavedQueryStore : ISavedQueryStore, IDisposable
{
    private const int FileVersion = 1;

    private readonly string? _path;
    private readonly ILogger<JsonSavedQueryStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<SavedQuery>? _queries;

    /// <param name="options">Location of the file.</param>
    /// <param name="logger">Receives a warning when the file cannot be read.</param>
    public JsonSavedQueryStore(SavedQueryStoreOptions options, ILogger<JsonSavedQueryStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _path = options.FilePath;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedQuery>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var queries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return [.. queries
                .OrderBy(static q => q.Folder, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(static q => q.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(SavedQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = new List<SavedQuery>(await LoadAsync(cancellationToken).ConfigureAwait(false));
            var index = updated.FindIndex(q => q.Id == query.Id);
            if (index >= 0)
            {
                updated[index] = query;
            }
            else
            {
                updated.Add(query);
            }

            await WriteAsync(updated, cancellationToken).ConfigureAwait(false);
            _queries = updated;
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
            var queries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var updated = queries.Where(q => q.Id != id).ToList();
            if (updated.Count != queries.Count)
            {
                await WriteAsync(updated, cancellationToken).ConfigureAwait(false);
                _queries = updated;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task<List<SavedQuery>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_queries is not null)
        {
            return _queries;
        }

        if (_path is null || !File.Exists(_path))
        {
            return _queries = [];
        }

        try
        {
            var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            QueryFile? file;
            await using (stream.ConfigureAwait(false))
            {
                file = await JsonSerializer.DeserializeAsync(stream, QueryJson.Default.QueryFile, cancellationToken).ConfigureAwait(false);
            }

            return _queries = [.. (file?.Queries ?? []).Where(static q => q.Id != Guid.Empty && q.Name is not null && q.Sql is not null)];
        }
        catch (JsonException ex)
        {
            var aside = _path + ".unreadable";
            File.Copy(_path, aside, overwrite: true);
            LogUnreadable(_path, aside, ex);
            return _queries = [];
        }
    }

    private async Task WriteAsync(List<SavedQuery> queries, CancellationToken cancellationToken)
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
            await JsonSerializer.SerializeAsync(stream, new QueryFile { Version = FileVersion, Queries = queries }, QueryJson.Default.QueryFile, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, _path, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Saved query file {Path} could not be read; it was copied to {Aside} and an empty list is used.")]
    private partial void LogUnreadable(string path, string aside, Exception exception);

    private sealed class QueryFile
    {
        public int Version { get; set; }

        public List<SavedQuery> Queries { get; set; } = [];
    }

    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(QueryFile))]
    private sealed partial class QueryJson : JsonSerializerContext;
}
