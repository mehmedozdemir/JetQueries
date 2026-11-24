using JetQueries.Core.Connections;

namespace JetQueries.Core.Tests.Connections;

public sealed class SshKnownHostsTests
{
    [Fact]
    public void A_key_is_trusted_the_first_time_and_then_only_the_same_one()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "known-hosts-tests", Guid.NewGuid().ToString("N"), "known.json");
        var hosts = new SshKnownHosts(path);

        Assert.True(hosts.Accept("bastion:22", "SHA256:aaa"));
        Assert.True(hosts.Accept("bastion:22", "SHA256:aaa"));
        Assert.False(hosts.Accept("bastion:22", "SHA256:bbb"));
        Assert.True(hosts.Accept("other:22", "SHA256:bbb"));

        // Remembered in the file, for the next run.
        Assert.False(new SshKnownHosts(path).Accept("BASTION:22", "SHA256:bbb"));
        hosts.Forget("bastion:22");
        Assert.True(new SshKnownHosts(path).Accept("bastion:22", "SHA256:bbb"));
    }

    [Fact]
    public void The_ssh_secret_has_its_own_stable_key()
    {
        var id = Guid.NewGuid();
        Assert.Equal(SshTunnel.SecretId(id), SshTunnel.SecretId(id));
        Assert.NotEqual(id, SshTunnel.SecretId(id));
        Assert.NotEqual(SshTunnel.SecretId(id), SshTunnel.SecretId(Guid.NewGuid()));
    }
}
