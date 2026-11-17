using System.Diagnostics;
using System.Net.Sockets;
using JetQueries.Core.Extensions;
using JetQueries.Sdk;

namespace JetQueries.Core.Connections;

/// <summary>How a connection attempt ended.</summary>
public enum ConnectionOutcome
{
    /// <summary>The connection was opened.</summary>
    Succeeded,

    /// <summary>No loaded extension provides the connection's database type.</summary>
    ProviderMissing,

    /// <summary>A password is needed and none was given or stored.</summary>
    PasswordRequired,

    /// <summary>The server's address did not answer: wrong host or port, firewall, or the VPN is not connected.</summary>
    Unreachable,

    /// <summary>The server answered but the connection was refused (login, database, encryption, …).</summary>
    Failed,
}

/// <summary>Result of testing or opening a connection.</summary>
/// <param name="Outcome">How the attempt ended.</param>
/// <param name="Detail">The provider's or network stack's own message, for display; never contains the password.</param>
/// <param name="Elapsed">How long the attempt took.</param>
public sealed record ConnectionAttempt(ConnectionOutcome Outcome, string? Detail, TimeSpan Elapsed)
{
    /// <summary>The open session when <see cref="Outcome"/> is <see cref="ConnectionOutcome.Succeeded"/> and the attempt was not a test.</summary>
    public ConnectionSession? Session { get; init; }
}

/// <summary>An open connection to a saved database.</summary>
/// <param name="Definition">The saved connection it was opened from.</param>
/// <param name="Provider">The provider that opened it.</param>
/// <param name="Connection">The live connection, owned by the <see cref="IConnectionManager"/>.</param>
public sealed record ConnectionSession(ConnectionDefinition Definition, IDatabaseProvider Provider, IDatabaseConnection Connection);

/// <summary>Checks whether a TCP endpoint answers.</summary>
public interface IReachabilityProbe
{
    /// <summary>Tries to open a TCP connection.</summary>
    /// <param name="endpoint">Where to connect.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns><see langword="null"/> if the endpoint answered, otherwise a short reason.</returns>
    Task<string?> ProbeAsync(NetworkEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Tests and opens saved connections and keeps track of the open ones.</summary>
public interface IConnectionManager
{
    /// <summary>Raised, on an arbitrary thread, after a session was opened or closed.</summary>
    event EventHandler? SessionsChanged;

    /// <summary>The currently open sessions.</summary>
    IReadOnlyCollection<ConnectionSession> Sessions { get; }

    /// <summary>
    /// Tries to connect and disconnects again. Checks that the address answers first, so an unreachable server
    /// (VPN not connected) is reported in seconds instead of after the driver's timeout.
    /// </summary>
    /// <param name="definition">The connection, saved or still being edited.</param>
    /// <param name="password">The password to use, or <see langword="null"/> to use the stored one.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    Task<ConnectionAttempt> TestAsync(ConnectionDefinition definition, string? password, CancellationToken cancellationToken);

    /// <summary>Opens a session, or returns the existing one for this connection.</summary>
    /// <param name="definition">The saved connection.</param>
    /// <param name="password">The password to use, or <see langword="null"/> to use the stored one.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    Task<ConnectionAttempt> OpenAsync(ConnectionDefinition definition, string? password, CancellationToken cancellationToken);

    /// <summary>Closes the session of a connection, if one is open.</summary>
    /// <param name="connectionId">The connection's identity.</param>
    Task CloseAsync(Guid connectionId);
}

/// <summary>Default <see cref="IReachabilityProbe"/>: a plain TCP connect.</summary>
public sealed class TcpReachabilityProbe : IReachabilityProbe
{
    /// <inheritdoc />
    public async Task<string?> ProbeAsync(NetworkEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, limit.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"No answer from {endpoint.Host}:{endpoint.Port} within {timeout.TotalSeconds:0} s.";
        }
        catch (SocketException ex)
        {
            return $"{endpoint.Host}:{endpoint.Port}: {ex.Message}";
        }
    }
}

/// <summary>Default <see cref="IConnectionManager"/>.</summary>
public sealed class ConnectionManager : IConnectionManager, IAsyncDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    private readonly IExtensionHost _extensions;
    private readonly ICredentialStore _credentials;
    private readonly IReachabilityProbe _probe;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, ConnectionSession> _sessions = [];

    /// <summary>Creates the manager.</summary>
    /// <param name="extensions">Source of database providers.</param>
    /// <param name="credentials">Source of stored passwords.</param>
    /// <param name="probe">Reachability check.</param>
    public ConnectionManager(IExtensionHost extensions, ICredentialStore credentials, IReachabilityProbe probe)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(probe);
        _extensions = extensions;
        _credentials = credentials;
        _probe = probe;
    }

    /// <inheritdoc />
    public event EventHandler? SessionsChanged;

    /// <inheritdoc />
    public IReadOnlyCollection<ConnectionSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions.Values];
            }
        }
    }

    /// <inheritdoc />
    public async Task<ConnectionAttempt> TestAsync(ConnectionDefinition definition, string? password, CancellationToken cancellationToken)
    {
        var (attempt, connection, _) = await ConnectAsync(definition, password, cancellationToken).ConfigureAwait(false);
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        return attempt;
    }

    /// <inheritdoc />
    public async Task<ConnectionAttempt> OpenAsync(ConnectionDefinition definition, string? password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            if (_sessions.TryGetValue(definition.Id, out var existing))
            {
                return new ConnectionAttempt(ConnectionOutcome.Succeeded, null, TimeSpan.Zero) { Session = existing };
            }
        }

        var (attempt, connection, provider) = await ConnectAsync(definition, password, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return attempt;
        }

        var session = new ConnectionSession(definition, provider!, connection);
        ConnectionSession? winner = null;
        lock (_gate)
        {
            // Two opens of the same connection raced: keep the first, drop this one.
            if (!_sessions.TryAdd(definition.Id, session))
            {
                winner = _sessions[definition.Id];
            }
        }

        if (winner is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return attempt with { Session = winner };
        }

        SessionsChanged?.Invoke(this, EventArgs.Empty);
        return attempt with { Session = session };
    }

    /// <inheritdoc />
    public async Task CloseAsync(Guid connectionId)
    {
        ConnectionSession? session;
        lock (_gate)
        {
            _sessions.Remove(connectionId, out session);
        }

        if (session is not null)
        {
            await session.Connection.DisposeAsync().ConfigureAwait(false);
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Closes every open session.</summary>
    public async ValueTask DisposeAsync()
    {
        ConnectionSession[] sessions;
        lock (_gate)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            await session.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<(ConnectionAttempt Attempt, IDatabaseConnection? Connection, IDatabaseProvider? Provider)> ConnectAsync(
        ConnectionDefinition definition, string? password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var started = Stopwatch.GetTimestamp();
        ConnectionAttempt Done(ConnectionOutcome outcome, string? detail = null) => new(outcome, detail, Stopwatch.GetElapsedTime(started));

        // Extensions load in the background at start-up; a connection attempt simply waits for that.
        await _extensions.LoadAsync(cancellationToken).ConfigureAwait(false);
        var provider = _extensions.FindDatabaseProvider(definition.ProviderId);
        if (provider is null)
        {
            return (Done(ConnectionOutcome.ProviderMissing, definition.ProviderId), null, null);
        }

        var settings = provider as IConnectionSettingsProvider;
        ConnectionCredential? credential = null;
        if (settings?.RequiresCredential(definition.Settings) ?? true)
        {
            password ??= await _credentials.GetPasswordAsync(definition.Id, cancellationToken).ConfigureAwait(false);
            if (password is null)
            {
                return (Done(ConnectionOutcome.PasswordRequired), null, provider);
            }

            credential = new ConnectionCredential(definition.UserName, password);
        }

        if (settings?.GetEndpoint(definition.Settings) is { } endpoint &&
            await _probe.ProbeAsync(endpoint, ProbeTimeout, cancellationToken).ConfigureAwait(false) is { } unreachable)
        {
            return (Done(ConnectionOutcome.Unreachable, unreachable), null, provider);
        }

        try
        {
            var connection = await provider.OpenConnectionAsync(definition.ToProfile(), credential, cancellationToken).ConfigureAwait(false);
            return (Done(ConnectionOutcome.Succeeded), connection, provider);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Whatever a driver throws is a failed connection to report, never a crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The driver's message only: exception details may echo settings, and never the connection string.
            return (Done(ConnectionOutcome.Failed, ex.Message), null, provider);
        }
    }
}
