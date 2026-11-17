namespace JetQueries.Sdk;

/// <summary>
/// Entry point of an extension. The host creates one instance of every public, non-abstract implementation with
/// a parameterless constructor in the extension's entry assembly and calls <see cref="Initialize"/> once.
/// </summary>
/// <remarks>
/// Preview contract: may change until SDK 1.0. <see cref="Initialize"/> runs on a background thread during
/// start-up; it must only register contributions and return quickly. It must not do I/O, start threads or touch
/// the user interface. Work belongs in the contributed services, which are asynchronous and cancelable.
/// </remarks>
public interface IExtension
{
    /// <summary>Registers what this extension contributes to the application.</summary>
    /// <param name="context">The contribution points offered by the host. Valid only during this call.</param>
    void Initialize(IExtensionContext context);
}

/// <summary>The contribution points the host offers to an extension while it initializes.</summary>
/// <remarks>Preview contract: more contribution points are added before SDK 1.0.</remarks>
public interface IExtensionContext
{
    /// <summary>The manifest the extension was loaded from.</summary>
    ExtensionManifest Manifest { get; }

    /// <summary>Full path of the directory the extension was loaded from, for locating its own resources.</summary>
    string ExtensionDirectory { get; }

    /// <summary>Contributes a database provider.</summary>
    /// <param name="provider">The provider. Its <see cref="IDatabaseProvider.Id"/> must be unique in the application.</param>
    /// <exception cref="InvalidOperationException">The context is used after <see cref="IExtension.Initialize"/> returned.</exception>
    void RegisterDatabaseProvider(IDatabaseProvider provider);
}

/// <summary>Describes an extension. Read from the <c>extension.json</c> file in the extension's directory.</summary>
/// <param name="Id">
/// Unique identifier: lower-case ASCII letters, digits, <c>.</c> and <c>-</c>, at most 64 characters
/// (for example <c>jetqueries.oracle</c>).
/// </param>
/// <param name="Name">Name shown to the user.</param>
/// <param name="Version">Version of the extension.</param>
/// <param name="Assembly">File name of the entry assembly inside the extension's directory.</param>
/// <param name="SdkVersion">The <see cref="SdkContract.Version"/> the extension was built against.</param>
public sealed record ExtensionManifest(string Id, string Name, Version Version, string Assembly, int SdkVersion);

/// <summary>Identifies the revision of the SDK contract.</summary>
public static class SdkContract
{
    /// <summary>
    /// Revision of the contract between host and extensions. While the SDK is in preview the host loads only
    /// extensions whose manifest declares exactly this value; it is incremented on every breaking change.
    /// </summary>
    /// <remarks>A property, not a constant, so that extensions read the host's value at run time.</remarks>
    public static int Version => 0;
}
