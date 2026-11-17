using JetQueries.Sdk;

namespace JetQueries.Core.Extensions;

/// <summary>Finds, loads and initializes extensions, and exposes what they contributed.</summary>
public interface IExtensionHost
{
    /// <summary>Every extension directory that was examined, loaded or not. Empty until loading has finished.</summary>
    IReadOnlyList<ExtensionLoadResult> Extensions { get; }

    /// <summary>Database providers contributed by loaded extensions. Empty until loading has finished.</summary>
    IReadOnlyCollection<IDatabaseProvider> DatabaseProviders { get; }

    /// <summary>
    /// Loads all extensions from the configured directories. Runs entirely off the calling thread. Loading
    /// happens once; later calls return the result of the first.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the result; an extension already being loaded finishes.</param>
    /// <returns>One entry per extension directory that was examined.</returns>
    Task<IReadOnlyList<ExtensionLoadResult>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Finds a contributed database provider by its identifier.</summary>
    /// <param name="id">The provider identifier, compared ordinally.</param>
    /// <returns>The provider, or <see langword="null"/> if no loaded extension contributes it.</returns>
    IDatabaseProvider? FindDatabaseProvider(string id);
}

/// <summary>Where the extension host looks for extensions.</summary>
public sealed class ExtensionHostOptions
{
    /// <summary>
    /// Directories whose sub-directories each hold one extension (an <c>extension.json</c> and its assemblies).
    /// Searched in order; when two extensions share an identifier the first one wins. Missing directories are
    /// ignored.
    /// </summary>
    public IList<string> Directories { get; } = [];
}

/// <summary>Outcome of loading one extension.</summary>
public enum ExtensionState
{
    /// <summary>The extension was loaded and its contributions are registered.</summary>
    Loaded,

    /// <summary>The extension was rejected or threw; none of its contributions are registered.</summary>
    Failed,
}

/// <summary>What happened to one extension directory.</summary>
/// <param name="Directory">Full path of the extension's directory.</param>
/// <param name="Manifest">The manifest, or <see langword="null"/> if it could not be read.</param>
/// <param name="State">Whether the extension is active.</param>
/// <param name="Error">Why loading failed, in English, for logs and the extension list; <see langword="null"/> on success.</param>
public sealed record ExtensionLoadResult(string Directory, ExtensionManifest? Manifest, ExtensionState State, string? Error);
