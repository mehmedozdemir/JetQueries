using JetQueries.Sdk;

namespace JetQueries.TestExtension;

/// <summary>
/// Test fixture. Marker files in the extension directory switch on misbehaviour, so one assembly covers every
/// scenario of the extension host tests. (A real extension must not do I/O in Initialize.)
/// </summary>
public sealed class SampleExtension : IExtension
{
    public void Initialize(IExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var idFile = Path.Combine(context.ExtensionDirectory, "provider-id.txt");
        var id = File.Exists(idFile) ? File.ReadAllText(idFile).Trim() : "sample";
        context.RegisterDatabaseProvider(new SampleProvider(id, context));

        if (File.Exists(Path.Combine(context.ExtensionDirectory, "throw.marker")))
        {
            throw new InvalidOperationException("Sample extension was asked to fail.");
        }
    }
}

public sealed class SampleProvider(string id, IExtensionContext context) : IDatabaseProvider
{
    public string Id { get; } = id;

    public string DisplayName => "Sample";

    public ProviderCapabilities Capabilities => ProviderCapabilities.None;

    public ISqlLanguageService? LanguageService => null;

    /// <summary>Quoting "late-registration" tries to register a provider after initialization, which must throw.</summary>
    public string QuoteIdentifier(string identifier)
    {
        if (identifier == "late-registration")
        {
            context.RegisterDatabaseProvider(this);
        }

        return $"\"{identifier}\"";
    }

    public Task<IDatabaseConnection> OpenConnectionAsync(ConnectionProfile profile, ConnectionCredential? credential, CancellationToken cancellationToken) =>
        Task.FromException<IDatabaseConnection>(new NotSupportedException("The sample provider cannot connect."));
}
