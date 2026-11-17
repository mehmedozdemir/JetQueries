using JetQueries.Core.Connections;
using JetQueries.Core.Extensions;
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

    private sealed class SingleProvider(IDatabaseProvider provider) : IExtensionHost
    {
        public IReadOnlyList<ExtensionLoadResult> Extensions => [];

        public IReadOnlyCollection<IDatabaseProvider> DatabaseProviders => [provider];

        public Task<IReadOnlyList<ExtensionLoadResult>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Extensions);

        public IDatabaseProvider? FindDatabaseProvider(string id) => id == provider.Id ? provider : null;
    }
}
