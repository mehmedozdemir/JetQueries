using System.Reflection;
using System.Runtime.Loader;
using JetQueries.Sdk;

namespace JetQueries.Core.Extensions;

/// <summary>
/// One load context per extension: the extension's own dependencies resolve from its directory (through its
/// <c>.deps.json</c>), so two extensions can ship different versions of the same library. The SDK always comes
/// from the host, otherwise the extension's <see cref="IExtension"/> would be a different type from ours.
/// </summary>
/// <remarks>This isolates dependencies. It is not a security boundary: extensions run with the user's rights.</remarks>
internal sealed class ExtensionLoadContext(string name, string entryAssemblyPath) : AssemblyLoadContext(name, isCollectible: false)
{
    private static readonly string SdkAssemblyName = typeof(IExtension).Assembly.GetName().Name!;

    private readonly AssemblyDependencyResolver _resolver = new(entryAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, SdkAssemblyName, StringComparison.Ordinal))
        {
            return null;
        }

        // Null falls back to the host's context: the base class library and anything the extension does not ship.
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
