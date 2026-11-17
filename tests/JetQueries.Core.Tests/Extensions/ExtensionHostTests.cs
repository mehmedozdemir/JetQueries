using System.Reflection;
using System.Runtime.Loader;
using JetQueries.Core.Extensions;
using JetQueries.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Core.Tests.Extensions;

/// <summary>
/// Loads the real JetQueries.TestExtension assembly from disk. Each test builds its own extensions directory
/// under the test output folder (loaded assemblies stay locked on Windows, so nothing is deleted afterwards).
/// </summary>
public sealed class ExtensionHostTests
{
    private const string SampleId = "jetqueries.test.sample";

    private static readonly string TestExtensionDirectory = typeof(ExtensionHostTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(static a => a.Key == "TestExtensionDirectory").Value!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Loads_an_extension_and_registers_its_provider()
    {
        var root = NewRoot();
        AddExtension(root, "sample");
        var host = CreateHost(root);

        var results = await host.LoadAsync(Token);

        var result = Assert.Single(results);
        Assert.Equal(ExtensionState.Loaded, result.State);
        Assert.Null(result.Error);
        Assert.Equal(SampleId, result.Manifest!.Id);
        Assert.Equal(new Version(1, 2, 3), result.Manifest.Version);
        Assert.Same(results, host.Extensions);

        var provider = host.FindDatabaseProvider("sample");
        Assert.NotNull(provider);
        Assert.Equal("Sample", provider.DisplayName);
        Assert.Same(provider, Assert.Single(host.DatabaseProviders));
    }

    [Fact]
    public async Task Extension_code_lives_in_its_own_load_context_but_shares_the_sdk()
    {
        var root = NewRoot();
        var directory = AddExtension(root, "sample");

        // Even when an extension ships its own copy of the SDK, the host's copy must be used.
        File.Copy(typeof(IExtension).Assembly.Location, Path.Combine(directory, Path.GetFileName(typeof(IExtension).Assembly.Location)));
        var host = CreateHost(root);

        await host.LoadAsync(Token);

        var provider = host.FindDatabaseProvider("sample")!;
        var context = AssemblyLoadContext.GetLoadContext(provider.GetType().Assembly);
        Assert.NotSame(AssemblyLoadContext.Default, context);
        Assert.Equal(SampleId, context!.Name);
        Assert.Same(typeof(IDatabaseProvider).Assembly, provider.GetType().GetInterface(nameof(IDatabaseProvider))!.Assembly);
    }

    [Fact]
    public async Task Missing_directories_and_folders_without_a_manifest_are_ignored()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "not-an-extension"));
        var host = CreateHost(Path.Combine(root, "does-not-exist"), root);

        var results = await host.LoadAsync(Token);

        Assert.Empty(results);
        Assert.Empty(host.DatabaseProviders);
        Assert.Null(host.FindDatabaseProvider("sample"));
    }

    [Theory]
    [InlineData("{ not json", "not valid JSON")]
    [InlineData("""{"id":"Bad Id","name":"x","version":"1.0","assembly":"JetQueries.TestExtension.dll","sdk":0}""", "'id'")]
    [InlineData("""{"id":"a.b","name":"x","version":"one","assembly":"JetQueries.TestExtension.dll","sdk":0}""", "'version'")]
    [InlineData("""{"id":"a.b","name":"x","version":"1.0","assembly":"../JetQueries.TestExtension.dll","sdk":0}""", "'assembly'")]
    [InlineData("""{"id":"a.b","name":"x","version":"1.0","assembly":"missing.dll","sdk":0}""", "not found")]
    [InlineData("""{"id":"a.b","name":"x","version":"1.0","assembly":"JetQueries.TestExtension.dll"}""", "'sdk'")]
    [InlineData("""{"id":"a.b","name":"x","version":"1.0","assembly":"JetQueries.TestExtension.dll","sdk":999}""", "SDK contract 999")]
    public async Task A_bad_manifest_fails_that_extension_only(string manifest, string expectedError)
    {
        var root = NewRoot();
        AddExtension(root, "a-broken", manifest);
        AddExtension(root, "b-good");
        var host = CreateHost(root);

        var results = await host.LoadAsync(Token);

        Assert.Equal(2, results.Count);
        Assert.Equal(ExtensionState.Failed, results[0].State);
        Assert.Contains(expectedError, results[0].Error, StringComparison.Ordinal);
        Assert.Equal(ExtensionState.Loaded, results[1].State);
        Assert.NotNull(host.FindDatabaseProvider("sample"));
    }

    [Fact]
    public async Task An_extension_that_throws_contributes_nothing_and_does_not_stop_the_others()
    {
        var root = NewRoot();
        var broken = AddExtension(root, "a-throws", Manifest("jetqueries.test.throws"));
        File.WriteAllText(Path.Combine(broken, "throw.marker"), string.Empty);
        File.WriteAllText(Path.Combine(broken, "provider-id.txt"), "from-broken");
        AddExtension(root, "b-good");
        var host = CreateHost(root);

        var results = await host.LoadAsync(Token);

        Assert.Equal(ExtensionState.Failed, results[0].State);
        Assert.Contains("asked to fail", results[0].Error, StringComparison.Ordinal);
        Assert.Null(host.FindDatabaseProvider("from-broken"));
        Assert.Equal(ExtensionState.Loaded, results[1].State);
        Assert.NotNull(host.FindDatabaseProvider("sample"));
    }

    [Fact]
    public async Task The_first_extension_with_an_id_wins()
    {
        var builtIn = NewRoot();
        var user = NewRoot();
        AddExtension(builtIn, "sample");
        var duplicate = AddExtension(user, "sample-copy");
        File.WriteAllText(Path.Combine(duplicate, "provider-id.txt"), "other");
        var host = CreateHost(builtIn, user);

        var results = await host.LoadAsync(Token);

        Assert.Equal(ExtensionState.Loaded, results[0].State);
        Assert.Equal(ExtensionState.Failed, results[1].State);
        Assert.Contains("already loaded", results[1].Error, StringComparison.Ordinal);
        Assert.Null(host.FindDatabaseProvider("other"));
    }

    [Fact]
    public async Task A_provider_id_can_be_registered_only_once()
    {
        var root = NewRoot();
        AddExtension(root, "a-first");
        AddExtension(root, "b-second", Manifest("jetqueries.test.second"));
        var host = CreateHost(root);

        var results = await host.LoadAsync(Token);

        Assert.Equal(ExtensionState.Loaded, results[0].State);
        Assert.Equal(ExtensionState.Failed, results[1].State);
        Assert.Contains("'sample' is already registered", results[1].Error, StringComparison.Ordinal);
        Assert.Single(host.DatabaseProviders);
    }

    [Fact]
    public async Task Loading_happens_once()
    {
        var root = NewRoot();
        AddExtension(root, "sample");
        var host = CreateHost(root);

        var first = await host.LoadAsync(Token);
        var provider = host.FindDatabaseProvider("sample");
        var second = await host.LoadAsync(Token);

        Assert.Same(first, second);
        Assert.Same(provider, host.FindDatabaseProvider("sample"));
    }

    [Fact]
    public async Task Contributions_are_rejected_after_initialization()
    {
        var root = NewRoot();
        AddExtension(root, "sample");
        var host = CreateHost(root);
        await host.LoadAsync(Token);
        var provider = host.FindDatabaseProvider("sample")!;

        Assert.Throws<InvalidOperationException>(() => provider.QuoteIdentifier("late-registration"));
        Assert.Single(host.DatabaseProviders);
    }

    [Fact]
    public async Task Waiting_for_the_load_can_be_canceled()
    {
        var root = NewRoot();
        AddExtension(root, "sample");
        var host = CreateHost(root);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.LoadAsync(canceled.Token));

        // The load itself still completes for later callers.
        Assert.Single(await host.LoadAsync(Token));
    }

    private static ExtensionHost CreateHost(params string[] directories)
    {
        var options = new ExtensionHostOptions();
        foreach (var directory in directories)
        {
            options.Directories.Add(directory);
        }

        return new ExtensionHost(options, NullLogger<ExtensionHost>.Instance);
    }

    private static string NewRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "extension-scenarios", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Manifest(string id) =>
        $$"""{"id":"{{id}}","name":"Sample","version":"1.0","assembly":"JetQueries.TestExtension.dll","sdk":{{SdkContract.Version}}}""";

    /// <summary>Copies the built test extension into <paramref name="root"/>, optionally with another manifest.</summary>
    private static string AddExtension(string root, string folder, string? manifest = null)
    {
        var target = Path.Combine(root, folder);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(TestExtensionDirectory, "JetQueries.TestExtension.*"))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        var manifestPath = Path.Combine(target, ExtensionHost.ManifestFileName);
        if (manifest is null)
        {
            File.Copy(Path.Combine(TestExtensionDirectory, ExtensionHost.ManifestFileName), manifestPath);
        }
        else
        {
            File.WriteAllText(manifestPath, manifest);
        }

        return target;
    }
}
