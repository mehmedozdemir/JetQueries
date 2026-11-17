using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using JetQueries.Sdk;
using Microsoft.Extensions.Logging;

namespace JetQueries.Core.Extensions;

/// <summary>
/// Loads extensions from directories, each into its own <see cref="System.Runtime.Loader.AssemblyLoadContext"/>.
/// A faulty extension is recorded as failed and never prevents the others, or the application, from starting.
/// </summary>
public sealed partial class ExtensionHost : IExtensionHost
{
    /// <summary>Name of the manifest file in an extension's directory.</summary>
    public const string ManifestFileName = "extension.json";

    private const int MaxManifestBytes = 64 * 1024;

    private readonly ExtensionHostOptions _options;
    private readonly ILogger<ExtensionHost> _logger;
    private readonly Lock _gate = new();
    private Task<IReadOnlyList<ExtensionLoadResult>>? _load;
    private volatile IReadOnlyList<ExtensionLoadResult> _extensions = [];
    private volatile FrozenDictionary<string, IDatabaseProvider> _providers = FrozenDictionary<string, IDatabaseProvider>.Empty;

    /// <summary>Creates the host. Nothing is loaded until <see cref="LoadAsync"/> is called.</summary>
    /// <param name="options">The directories to search.</param>
    /// <param name="logger">Receives one entry per loaded or failed extension.</param>
    public ExtensionHost(ExtensionHostOptions options, ILogger<ExtensionHost> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<ExtensionLoadResult> Extensions => _extensions;

    /// <inheritdoc />
    public IReadOnlyCollection<IDatabaseProvider> DatabaseProviders => _providers.Values;

    /// <inheritdoc />
    public Task<IReadOnlyList<ExtensionLoadResult>> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            // Task.Run: assembly loading and extension code are synchronous and must not run on the caller's
            // (possibly UI) thread. The load itself is not cancelable half-way; only the wait is.
            _load ??= Task.Run(LoadAllAsync, CancellationToken.None);
            return _load.WaitAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public IDatabaseProvider? FindDatabaseProvider(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _providers.GetValueOrDefault(id);
    }

    private async Task<IReadOnlyList<ExtensionLoadResult>> LoadAllAsync()
    {
        var results = new List<ExtensionLoadResult>();
        var loadedIds = new HashSet<string>(StringComparer.Ordinal);
        var providers = new Dictionary<string, IDatabaseProvider>(StringComparer.Ordinal);

        foreach (var root in _options.Directories.ToArray())
        {
            string[] directories;
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                directories = Directory.GetDirectories(root);
                Array.Sort(directories, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogDirectoryUnreadable(root, ex);
                continue;
            }

            foreach (var directory in directories)
            {
                if (!File.Exists(Path.Combine(directory, ManifestFileName)))
                {
                    continue;
                }

                var result = await LoadOneAsync(Path.GetFullPath(directory), loadedIds, providers).ConfigureAwait(false);
                results.Add(result);
                if (result.State == ExtensionState.Loaded)
                {
                    LogLoaded(result.Manifest!.Id, result.Manifest.Version, result.Directory);
                }
                else
                {
                    LogFailed(result.Directory, result.Error!);
                }
            }
        }

        _providers = providers.ToFrozenDictionary(StringComparer.Ordinal);
        _extensions = results.AsReadOnly();
        return _extensions;
    }

#pragma warning disable CA1031 // Extension code is third-party: whatever it throws must not take the application down.
    private static async Task<ExtensionLoadResult> LoadOneAsync(
        string directory,
        HashSet<string> loadedIds,
        Dictionary<string, IDatabaseProvider> providers)
    {
        ExtensionManifest? manifest = null;
        try
        {
            (manifest, var error) = await ReadManifestAsync(directory).ConfigureAwait(false);
            if (manifest is null)
            {
                return Failed(error!);
            }

            if (manifest.SdkVersion != SdkContract.Version)
            {
                return Failed($"Built for SDK contract {manifest.SdkVersion}; this application provides {SdkContract.Version}.");
            }

            if (loadedIds.Contains(manifest.Id))
            {
                return Failed($"An extension with the id '{manifest.Id}' is already loaded.");
            }

            var assemblyPath = Path.Combine(directory, manifest.Assembly);
            if (!File.Exists(assemblyPath))
            {
                return Failed($"Entry assembly '{manifest.Assembly}' not found.");
            }

            var assembly = new ExtensionLoadContext(manifest.Id, assemblyPath).LoadFromAssemblyPath(assemblyPath);
            var entryTypes = assembly.GetExportedTypes()
                .Where(static t => typeof(IExtension).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false } &&
                                   t.GetConstructor(Type.EmptyTypes) is not null)
                .ToArray();
            if (entryTypes.Length == 0)
            {
                return Failed($"'{manifest.Assembly}' has no public {nameof(IExtension)} implementation with a parameterless constructor.");
            }

            // Contributions are staged and committed only when every entry point succeeded: an extension is
            // either fully active or not at all.
            var context = new ExtensionContext(manifest, directory);
            foreach (var type in entryTypes)
            {
                ((IExtension)Activator.CreateInstance(type)!).Initialize(context);
            }

            context.Close();
            foreach (var provider in context.DatabaseProviders)
            {
                var id = provider.Id;
                if (string.IsNullOrWhiteSpace(id))
                {
                    return Failed("A database provider has no id.");
                }

                if (providers.ContainsKey(id) || context.DatabaseProviders.Count(p => string.Equals(p.Id, id, StringComparison.Ordinal)) > 1)
                {
                    return Failed($"Database provider '{id}' is already registered.");
                }
            }

            foreach (var provider in context.DatabaseProviders)
            {
                providers.Add(provider.Id, provider);
            }

            loadedIds.Add(manifest.Id);
            return new ExtensionLoadResult(directory, manifest, ExtensionState.Loaded, null);
        }
        catch (Exception ex)
        {
            return Failed($"{ex.GetType().Name}: {ex.Message}");
        }

        ExtensionLoadResult Failed(string error) => new(directory, manifest, ExtensionState.Failed, error);
    }
#pragma warning restore CA1031

    private static async Task<(ExtensionManifest? Manifest, string? Error)> ReadManifestAsync(string directory)
    {
        var path = Path.Combine(directory, ManifestFileName);
        if (new FileInfo(path).Length > MaxManifestBytes)
        {
            return (null, $"{ManifestFileName} is larger than {MaxManifestBytes / 1024} KB.");
        }

        ManifestFile? file;
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                file = await JsonSerializer.DeserializeAsync(stream, ManifestJsonContext.Default.ManifestFile).ConfigureAwait(false);
            }
        }
        catch (JsonException ex)
        {
            return (null, $"{ManifestFileName} is not valid JSON: {ex.Message}");
        }

        if (file is null)
        {
            return (null, $"{ManifestFileName} is empty.");
        }

        if (!IsValidId(file.Id))
        {
            return (null, "Manifest 'id' must be 1-64 characters of a-z, 0-9, '.' or '-'.");
        }

        if (string.IsNullOrWhiteSpace(file.Name))
        {
            return (null, "Manifest 'name' is missing.");
        }

        if (!Version.TryParse(file.Version, out var version))
        {
            return (null, "Manifest 'version' is missing or not a version number such as 1.2.0.");
        }

        // A bare file name only: the entry assembly may not point outside the extension's own directory.
        if (string.IsNullOrWhiteSpace(file.Assembly) || file.Assembly != Path.GetFileName(file.Assembly) ||
            file.Assembly.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !file.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "Manifest 'assembly' must be the file name of a .dll in the extension's directory.");
        }

        if (file.Sdk is not { } sdk)
        {
            return (null, "Manifest 'sdk' is missing.");
        }

        return (new ExtensionManifest(file.Id!, file.Name.Trim(), version, file.Assembly, sdk), null);
    }

    private static bool IsValidId(string? id)
    {
        if (id is not { Length: > 0 and <= 64 })
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Extension {Id} {Version} loaded from {Directory}.")]
    private partial void LogLoaded(string id, Version version, string directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Extension in {Directory} was not loaded: {Error}")]
    private partial void LogFailed(string directory, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Extension directory {Directory} could not be read.")]
    private partial void LogDirectoryUnreadable(string directory, Exception exception);

    private sealed class ExtensionContext(ExtensionManifest manifest, string directory) : IExtensionContext
    {
        private bool _closed;

        public List<IDatabaseProvider> DatabaseProviders { get; } = [];

        public ExtensionManifest Manifest { get; } = manifest;

        public string ExtensionDirectory { get; } = directory;

        public void RegisterDatabaseProvider(IDatabaseProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (_closed)
            {
                throw new InvalidOperationException("Contributions can only be registered during IExtension.Initialize.");
            }

            DatabaseProviders.Add(provider);
        }

        public void Close() => _closed = true;
    }

    private sealed class ManifestFile
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Version { get; set; }

        public string? Assembly { get; set; }

        public int? Sdk { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
    [JsonSerializable(typeof(ManifestFile))]
    private sealed partial class ManifestJsonContext : JsonSerializerContext;
}
