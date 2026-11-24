using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JetQueries.Sdk;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace JetQueries.Core.Connections;

/// <summary>
/// An SSH tunnel to the database: the connection goes to an SSH server (a bastion host) and from there to the
/// database's host and port. Not secret: the password or the key's passphrase is in the
/// <see cref="ICredentialStore"/> under <see cref="SshTunnel.SecretId"/>.
/// </summary>
public sealed record SshTunnelSettings
{
    /// <summary>The SSH server.</summary>
    public required string Host { get; init; }

    /// <summary>Its port.</summary>
    public int Port { get; init; } = 22;

    /// <summary>The SSH user.</summary>
    public required string UserName { get; init; }

    /// <summary>A private key file (OpenSSH or PuTTY format) instead of a password.</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>Whether the SSH password or the key's passphrase is kept in the credential store.</summary>
    public bool SavePassword { get; init; }
}

/// <summary>
/// The SSH server keys seen before, by host and port (trust on first use, as OpenSSH's known_hosts): a key that
/// changes refuses the tunnel. Kept as JSON next to the connections; not secret.
/// </summary>
public sealed class SshKnownHosts(string? path)
{
    private readonly Lock _lock = new();

    /// <summary>Whether the key may be used: first seen (and remembered) or the one remembered.</summary>
    /// <param name="host">host:port.</param>
    /// <param name="fingerprint">The key's SHA-256 fingerprint.</param>
    public bool Accept(string host, string fingerprint)
    {
        lock (_lock)
        {
            var known = Read();
            if (known.TryGetValue(host, out var remembered))
            {
                return string.Equals(remembered, fingerprint, StringComparison.Ordinal);
            }

            known[host] = fingerprint;
            Write(known);
            return true;
        }
    }

    /// <summary>Forgets a host's key (after the server's key was changed on purpose).</summary>
    public void Forget(string host)
    {
        lock (_lock)
        {
            var known = Read();
            if (known.Remove(host))
            {
                Write(known);
            }
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly Dictionary<string, string> _memory = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, string> Read()
    {
        if (path is null || !File.Exists(path))
        {
            return path is null ? _memory : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            return new Dictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Write(Dictionary<string, string> known)
    {
        if (path is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(known, Indented));
    }
}

/// <summary>An open SSH tunnel: the local end the database driver connects to, kept open until disposed.</summary>
public sealed class SshTunnel : IAsyncDisposable
{
    private readonly SshClient _client;
    private readonly ForwardedPortLocal _forward;

    private SshTunnel(SshClient client, ForwardedPortLocal forward, NetworkEndpoint local)
    {
        _client = client;
        _forward = forward;
        Local = local;
    }

    /// <summary>Where the driver connects: 127.0.0.1 and the port the tunnel listens on.</summary>
    public NetworkEndpoint Local { get; }

    /// <summary>The credential-store key of the tunnel's password or passphrase, derived from the connection's id.</summary>
    public static Guid SecretId(Guid connectionId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(connectionId.ToString("N") + "/ssh"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>Connects to the SSH server and forwards a free local port to <paramref name="target"/>.</summary>
    /// <param name="settings">The SSH server and user.</param>
    /// <param name="secret">The password, or the private key's passphrase (null for a key without one).</param>
    /// <param name="target">The database's host and port, as the SSH server sees them.</param>
    /// <param name="knownHosts">Checks the server's key.</param>
    /// <param name="cancellationToken">Cancels connecting.</param>
    /// <exception cref="SshException">The server refused the user, the key changed, or forwarding failed.</exception>
    public static async Task<SshTunnel> OpenAsync(
        SshTunnelSettings settings, string? secret, NetworkEndpoint target, SshKnownHosts knownHosts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(knownHosts);
        AuthenticationMethod method = settings.PrivateKeyPath is { Length: > 0 } keyPath
            ? new PrivateKeyAuthenticationMethod(settings.UserName, string.IsNullOrEmpty(secret) ? new PrivateKeyFile(keyPath) : new PrivateKeyFile(keyPath, secret))
            : new PasswordAuthenticationMethod(settings.UserName, secret ?? string.Empty);
        var info = new ConnectionInfo(settings.Host, settings.Port, settings.UserName, method) { Timeout = TimeSpan.FromSeconds(15) };
        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        var hostKey = $"{settings.Host}:{settings.Port}";
        client.HostKeyReceived += (_, e) => e.CanTrust = knownHosts.Accept(hostKey, e.FingerPrintSHA256);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var forward = new ForwardedPortLocal("127.0.0.1", 0, target.Host, (uint)target.Port);
            client.AddForwardedPort(forward);
            forward.Start();
            return new SshTunnel(client, forward, new NetworkEndpoint("127.0.0.1", (int)forward.BoundPort));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Stops forwarding and disconnects from the SSH server.</summary>
    public ValueTask DisposeAsync()
    {
        try
        {
            if (_forward.IsStarted)
            {
                _forward.Stop();
            }

            _client.Disconnect();
        }
        catch (SshException)
        {
            // Already gone (network); nothing to close.
        }
        catch (ObjectDisposedException)
        {
        }

        _forward.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
