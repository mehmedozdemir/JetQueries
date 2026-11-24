using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using JetQueries.Core.Connections;
using JetQueries.Core.Extensions;
using JetQueries.Sdk;
using Testcontainers.PostgreSql;

namespace JetQueries.Provider.PostgreSql.Tests;

/// <summary>
/// A connection through an SSH tunnel: PostgreSQL in a Docker network reachable only by its alias there, an SSH
/// server in the same network (built from SshServer/Dockerfile) as the bastion. Runs with the PostgreSQL
/// integration tests (<c>JETQUERIES_POSTGRES_TESTS=Y</c>, Docker); skipped otherwise.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SshTunnelTests
{
    private const string SshUser = "jq";
    private const string SshPassword = "jq-tunnel-secret";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_connection_goes_through_an_ssh_server_to_a_database_it_alone_can_reach()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable(PostgreSqlFixture.OptInVariable), "Y", StringComparison.OrdinalIgnoreCase),
            $"Set {PostgreSqlFixture.OptInVariable}=Y to run the SSH tunnel test (Docker).");

        INetwork network = new NetworkBuilder().Build();
        IFutureDockerImage image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.Combine(AppContext.BaseDirectory, "SshServer"))
            .WithName("jetqueries-test-sshd:latest")
            .WithCleanUp(true)
            .Build();
        await network.CreateAsync(Token);
        await image.CreateAsync(Token);
        var database = new PostgreSqlBuilder(PostgreSqlFixture.Image).WithNetwork(network).WithNetworkAliases("pgdb").Build();
        IContainer ssh = new ContainerBuilder(image.FullName)
            .WithNetwork(network)
            .WithPortBinding(22, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(22))
            .Build();
        await using (network)
        await using (database)
        await using (ssh)
        {
            await Task.WhenAll(database.StartAsync(Token), ssh.StartAsync(Token));
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(database.GetConnectionString());
            var definition = new ConnectionDefinition
            {
                Id = Guid.NewGuid(),
                Name = "through ssh",
                ProviderId = PostgreSqlProvider.ProviderId,

                // The database as the SSH server sees it: only its alias in the Docker network.
                Settings = new Dictionary<string, string> { ["host"] = "pgdb", ["port"] = "5432", ["database"] = builder.Database! },
                UserName = builder.Username,
                Ssh = new SshTunnelSettings { Host = ssh.Hostname, Port = ssh.GetMappedPublicPort(22), UserName = SshUser },
            };
            var knownHosts = new SshKnownHosts(Path.Combine(AppContext.BaseDirectory, "ssh-tests", Guid.NewGuid().ToString("N"), "known.json"));
            await using var manager = new ConnectionManager(new SingleProvider(new PostgreSqlProvider()), new SessionCredentialStore(), new TcpReachabilityProbe(), knownHosts);

            // Without the SSH password: asked for.
            Assert.Equal(ConnectionOutcome.SshPasswordRequired, (await manager.TestAsync(definition, builder.Password, Token)).Outcome);

            // A wrong one: the SSH server's refusal.
            var refused = await manager.TestAsync(definition, builder.Password, "wrong", Token);
            Assert.Equal(ConnectionOutcome.Failed, refused.Outcome);
            Assert.StartsWith("SSH:", refused.Detail, StringComparison.Ordinal);

            // The right one: a session whose every connection (queries, metadata) goes through the tunnel.
            var attempt = await manager.OpenAsync(definition, builder.Password, SshPassword, Token);
            Assert.Equal(ConnectionOutcome.Succeeded, attempt.Outcome);
            var session = attempt.Session!;
            Assert.True(session.IsTunneled);
            var events = new List<QueryEvent>();
            await foreach (var e in session.ExecuteAsync(new QueryRequest("SELECT inet_server_addr() IS NOT NULL AS remote, 41 + 1 AS n"), Token))
            {
                events.Add(e);
            }

            Assert.Equal(42, Convert.ToInt32(events.OfType<RowBatch>().Single().Rows[0][1], System.Globalization.CultureInfo.InvariantCulture));
            var schemas = await session.UseMetadataAsync(
                async (metadata, token) =>
                {
                    var list = new List<DatabaseObject>();
                    await foreach (var o in metadata.GetObjectsAsync(null, DatabaseObjectKinds.Schema, token))
                    {
                        list.Add(o);
                    }

                    return list;
                },
                Token);
            Assert.Contains(schemas, static s => s.Name.Name == "public");

            // The server's key was remembered; another key for the same address would be refused.
            Assert.False(knownHosts.Accept($"{ssh.Hostname}:{ssh.GetMappedPublicPort(22)}", "SHA256:not-the-key"));
            await manager.CloseAsync(definition.Id);
        }
    }

    private sealed class SingleProvider(IDatabaseProvider provider) : IExtensionHost
    {
        public IReadOnlyList<ExtensionLoadResult> Extensions => [];

        public IReadOnlyCollection<IDatabaseProvider> DatabaseProviders => [provider];

        public Task<IReadOnlyList<ExtensionLoadResult>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Extensions);

        public IDatabaseProvider? FindDatabaseProvider(string id) => provider;
    }
}
