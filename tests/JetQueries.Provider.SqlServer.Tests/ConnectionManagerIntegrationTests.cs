using JetQueries.Core.Connections;
using JetQueries.Core.Extensions;
using JetQueries.Core.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using JetQueries.Sdk;

namespace JetQueries.Provider.SqlServer.Tests;

/// <summary>The host's connection manager with the real provider, the real TCP probe and a real server.</summary>
[Trait("Category", "Integration")]
public sealed class ConnectionManagerIntegrationTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Test_connection_succeeds_fails_on_a_wrong_password_and_detects_an_unreachable_address()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        var credentials = new SessionCredentialStore();
        await using var manager = new ConnectionManager(new SingleProvider(new SqlServerProvider()), credentials, new TcpReachabilityProbe());
        var definition = new ConnectionDefinition
        {
            Id = Guid.NewGuid(),
            Name = "container",
            ProviderId = SqlServerProvider.ProviderId,
            Settings = fixture.Profile.Settings,
            UserName = fixture.Credential.UserName,
            SavePassword = true,
        };

        var ok = await manager.TestAsync(definition, fixture.Credential.Password, Token);
        Assert.True(ok.Outcome == ConnectionOutcome.Succeeded, ok.Detail);

        var wrong = await manager.TestAsync(definition, "definitely-not-the-password", Token);
        Assert.Equal(ConnectionOutcome.Failed, wrong.Outcome);
        Assert.Contains("Login failed", wrong.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("definitely-not-the-password", wrong.ToString(), StringComparison.Ordinal);

        Assert.Equal(ConnectionOutcome.PasswordRequired, (await manager.TestAsync(definition, null, Token)).Outcome);

        // Nothing listens on port 1 of this machine.
        var closed = definition with { Settings = new Dictionary<string, string>(definition.Settings) { ["server"] = "127.0.0.1,1" } };
        var unreachable = await manager.TestAsync(closed, "x", Token);
        Assert.Equal(ConnectionOutcome.Unreachable, unreachable.Outcome);
        Assert.True(unreachable.Elapsed < TimeSpan.FromSeconds(8), $"took {unreachable.Elapsed}");

        // With the password stored, a session opens and can run a query.
        await credentials.SetPasswordAsync(definition.Id, fixture.Credential.Password!, Token);
        var opened = await manager.OpenAsync(definition, null, Token);
        Assert.Equal(ConnectionOutcome.Succeeded, opened.Outcome);
        var rows = new List<object?[]>();
        await foreach (var item in opened.Session!.Connection.Executor.ExecuteAsync(new QueryRequest("SELECT DB_NAME()"), Token))
        {
            if (item is RowBatch batch)
            {
                rows.AddRange(batch.Rows);
            }
        }

        Assert.Equal("master", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task Metadata_is_read_on_its_own_connection_while_a_query_runs_and_is_cached()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);
        await using var manager = new ConnectionManager(new SingleProvider(new SqlServerProvider()), new SessionCredentialStore(), new TcpReachabilityProbe());
        await using var cache = new SqliteMetadataCache(new MetadataCacheOptions(), TimeProvider.System, NullLogger<SqliteMetadataCache>.Instance);
        var metadata = new MetadataService(cache, TimeProvider.System);
        var definition = new ConnectionDefinition
        {
            Id = Guid.NewGuid(),
            Name = "container",
            ProviderId = SqlServerProvider.ProviderId,
            Settings = fixture.Profile.Settings,
            UserName = fixture.Credential.UserName,
        };
        var session = (await manager.OpenAsync(definition, fixture.Credential.Password, Token)).Session!;

        await foreach (var item in session.Connection.Executor.ExecuteAsync(
                           new QueryRequest("IF OBJECT_ID(N'dbo.jq_probe') IS NULL CREATE TABLE dbo.jq_probe (id int NOT NULL PRIMARY KEY, note nvarchar(20) NULL);"), Token))
        {
            _ = item;
        }

        // A slow statement occupies the query connection...
        var slow = Task.Run(
            async () =>
            {
                await foreach (var item in session.Connection.Executor.ExecuteAsync(new QueryRequest("WAITFOR DELAY '00:00:04'; SELECT 1;"), Token))
                {
                    _ = item;
                }
            },
            Token);
        await Task.Delay(300, Token);

        // ...and the explorer still gets its answers, from the second connection.
        var started = DateTime.UtcNow;
        var databases = await metadata.GetObjectsAsync(session, null, DatabaseObjectKinds.Database, refresh: false, Token);
        var master = databases.Single(static d => d.Name.Name == "master");
        var schemas = await metadata.GetObjectsAsync(session, master, DatabaseObjectKinds.Schema, refresh: false, Token);
        var views = await metadata.GetObjectsAsync(session, schemas.Single(static s => s.Name.Name == "dbo"), DatabaseObjectKinds.Table | DatabaseObjectKinds.View, refresh: false, Token);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "metadata waited for the running query");
        Assert.False(slow.IsCompleted);
        var probe = Assert.Single(views, static v => v.Name.Name == "jq_probe");
        var columns = await metadata.GetColumnsAsync(session, probe.Name, refresh: false, Token);
        Assert.Equal(["id", "note"], columns.Select(static c => c.Name));

        // Later requests are answered from the cache (the very same list, no round trip) until a refresh.
        var cached = await metadata.GetObjectsAsync(session, null, DatabaseObjectKinds.Database, refresh: false, Token);
        Assert.Equal(databases, cached);
        Assert.Same(cached, await metadata.GetObjectsAsync(session, null, DatabaseObjectKinds.Database, refresh: false, Token));
        Assert.NotSame(cached, await metadata.GetObjectsAsync(session, null, DatabaseObjectKinds.Database, refresh: true, Token));
        await slow;
    }

    private sealed class SingleProvider(IDatabaseProvider provider) : IExtensionHost
    {
        public IReadOnlyList<ExtensionLoadResult> Extensions => [];

        public IReadOnlyCollection<IDatabaseProvider> DatabaseProviders => [provider];

        public Task<IReadOnlyList<ExtensionLoadResult>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Extensions);

        public IDatabaseProvider? FindDatabaseProvider(string id) => id == provider.Id ? provider : null;
    }
}
