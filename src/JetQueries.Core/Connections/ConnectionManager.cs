using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using JetQueries.Core.Extensions;
using JetQueries.Core.Queries;
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

/// <summary>
/// An open connection to a saved database. It owns a driver connection for running the user's SQL, one for
/// reading metadata (opened on first use) so a long query never blocks the object explorer or IntelliSense,
/// and the extra query channels that query windows open so each has its own current database.
/// </summary>
public sealed class ConnectionSession : IAsyncDisposable
{
    // Kept in memory for the life of the session only, to open further connections later.
    private readonly ConnectionCredential? _credential;
    private readonly SemaphoreSlim _metadataGate = new(1, 1);
    private readonly Lock _channelsLock = new();
    private readonly List<QueryChannel> _channels = [];
    private readonly QueryChannel _main;
    private bool _mainLeased;
    private IDatabaseConnection? _metadataConnection;
    private bool _disposed;

    internal ConnectionSession(ConnectionDefinition definition, IDatabaseProvider provider, IDatabaseConnection connection, ConnectionCredential? credential)
    {
        Definition = definition;
        Provider = provider;
        Connection = connection;
        _credential = credential;
        _main = new QueryChannel(this, connection, isMain: true);
    }

    /// <summary>The saved connection the session was opened from.</summary>
    public ConnectionDefinition Definition { get; }

    /// <summary>The provider that opened it.</summary>
    public IDatabaseProvider Provider { get; }

    /// <summary>The connection for running the user's SQL. Prefer <see cref="ExecuteAsync"/>, which applies the session's rules.</summary>
    public IDatabaseConnection Connection { get; }

    /// <summary>
    /// Runs SQL on the session's main query connection; see <see cref="QueryChannel.ExecuteAsync"/>. Query
    /// windows use a channel of their own (<see cref="OpenChannelAsync"/>) instead.
    /// </summary>
    /// <param name="request">The SQL and how to run it.</param>
    /// <param name="cancellationToken">Cancels waiting for the connection and the statement itself.</param>
    /// <exception cref="ReadOnlyConnectionException">The connection is read-only and the text can change data.</exception>
    public IAsyncEnumerable<QueryEvent> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken) =>
        _main.ExecuteAsync(request, cancellationToken);

    /// <summary>
    /// A query channel for one query window: its own driver connection, so that its current database and
    /// running statement do not affect other windows. The first window gets the session's main connection;
    /// later ones open another with the same profile and credential.
    /// </summary>
    /// <param name="cancellationToken">Cancels opening.</param>
    public async Task<QueryChannel> OpenChannelAsync(CancellationToken cancellationToken)
    {
        lock (_channelsLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_mainLeased)
            {
                _mainLeased = true;
                return _main;
            }
        }

        var connection = await Provider.OpenConnectionAsync(Definition.ToProfile(), _credential, cancellationToken).ConfigureAwait(false);
        var channel = new QueryChannel(this, connection, isMain: false);
        bool disposed;
        lock (_channelsLock)
        {
            disposed = _disposed;
            if (!disposed)
            {
                _channels.Add(channel);
            }
        }

        if (disposed)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(ConnectionSession));
        }

        return channel;
    }

    internal void Release(QueryChannel channel)
    {
        lock (_channelsLock)
        {
            if (ReferenceEquals(channel, _main))
            {
                _mainLeased = false;
            }
            else
            {
                _channels.Remove(channel);
            }
        }
    }

    internal bool IsDisposed => _disposed;

    /// <summary>
    /// Runs one metadata operation on the session's metadata connection. Operations are run one at a time, as
    /// a driver connection requires. If the operation fails the connection is dropped and reopened next time.
    /// </summary>
    /// <typeparam name="T">The result of the operation.</typeparam>
    /// <param name="work">The operation.</param>
    /// <param name="cancellationToken">Cancels waiting for the connection and the operation itself.</param>
    public async Task<T> UseMetadataAsync<T>(Func<IMetadataProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _metadataConnection ??= await Provider.OpenConnectionAsync(Definition.ToProfile(), _credential, cancellationToken).ConfigureAwait(false);
            try
            {
                return await work(_metadataConnection.Metadata, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The connection may be broken (network, VPN): do not keep failing on it.
                var broken = _metadataConnection;
                _metadataConnection = null;
                await broken.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    /// <summary>Closes every driver connection of the session: query channels, metadata, main.</summary>
    public async ValueTask DisposeAsync()
    {
        await _metadataGate.WaitAsync().ConfigureAwait(false);
        QueryChannel[] channels;
        try
        {
            lock (_channelsLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                channels = [.. _channels];
                _channels.Clear();
            }

            if (_metadataConnection is not null)
            {
                await _metadataConnection.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _metadataGate.Release();
        }

        foreach (var channel in channels)
        {
            await channel.CloseConnectionAsync().ConfigureAwait(false);
        }

        await Connection.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// A query window's connection within a session. Statements run one at a time; the current database can be
/// read and switched when the provider supports it (<see cref="IDatabaseContext"/>).
/// </summary>
public sealed class QueryChannel : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDatabaseConnection _connection;
    private readonly bool _isMain;
    private int _released;

    internal QueryChannel(ConnectionSession session, IDatabaseConnection connection, bool isMain)
    {
        Session = session;
        _connection = connection;
        _isMain = isMain;
    }

    /// <summary>The session the channel belongs to.</summary>
    public ConnectionSession Session { get; }

    /// <summary>What the current database is called for this provider; null when it cannot be switched.</summary>
    public DatabaseContextKind? ContextKind => (_connection as IDatabaseContext)?.Kind;

    /// <summary>
    /// Runs SQL and streams what happens. Statements are run one at a time: a second caller waits until the
    /// first has finished or was canceled. On a connection marked read-only, text that can change data is
    /// refused before anything is sent. If the provider splits scripts into batches (SQL Server's GO), each
    /// batch is sent in turn and message line numbers refer to the whole text; an error ends the run at the
    /// batch that failed.
    /// </summary>
    /// <param name="request">The SQL and how to run it.</param>
    /// <param name="cancellationToken">Cancels waiting for the connection and the statement itself.</param>
    /// <exception cref="ReadOnlyConnectionException">The connection is read-only and the text can change data.</exception>
    public async IAsyncEnumerable<QueryEvent> ExecuteAsync(QueryRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Checked here, below every user interface, so that no code path can forget it.
        if (Session.Definition.IsReadOnly && StatementGuard.Analyze(request.Sql).WritesData)
        {
            throw new ReadOnlyConnectionException();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Session.IsDisposed, Session);
            if (Session.Provider is not IScriptSplitter splitter)
            {
                await foreach (var item in _connection.Executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    yield return item;
                }

                yield break;
            }

            foreach (var batch in splitter.SplitBatches(request.Sql))
            {
                var batchRequest = request with { Sql = batch.Text };
                for (var run = 0; run < Math.Max(1, batch.RepeatCount); run++)
                {
                    await foreach (var item in _connection.Executor.ExecuteAsync(batchRequest, cancellationToken).ConfigureAwait(false))
                    {
                        yield return item is QueryMessage { Line: { } line } message ? message with { Line = line + batch.StartLine } : item;
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The current database (or schema); null when the provider cannot tell.</summary>
    /// <param name="cancellationToken">Cancels waiting and the request.</param>
    public Task<string?> GetCurrentDatabaseAsync(CancellationToken cancellationToken) =>
        _connection is IDatabaseContext context ? UseAsync(context.GetCurrentAsync, cancellationToken) : Task.FromResult<string?>(null);

    /// <summary>The databases (or schemas) that can be switched to; empty when switching is not supported.</summary>
    /// <param name="cancellationToken">Cancels waiting and the request.</param>
    public Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken) =>
        _connection is IDatabaseContext context ? UseAsync(context.GetAvailableAsync, cancellationToken) : Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Switches the current database (or schema).</summary>
    /// <param name="name">The database.</param>
    /// <param name="cancellationToken">Cancels waiting and the request.</param>
    /// <exception cref="NotSupportedException">The provider cannot switch databases.</exception>
    public Task ChangeDatabaseAsync(string name, CancellationToken cancellationToken) =>
        _connection is IDatabaseContext context
            ? UseAsync(async token =>
            {
                await context.ChangeAsync(name, token).ConfigureAwait(false);
                return true;
            }, cancellationToken)
            : throw new NotSupportedException("This connection cannot switch databases.");

    /// <summary>Whether the provider lets a query window keep a transaction open and end it (manual commit).</summary>
    public bool SupportsTransactions => _connection is ITransactionControl;

    /// <summary>Starts a transaction unless one is open; what runs next waits for Commit or Rollback.</summary>
    /// <param name="cancellationToken">Cancels waiting and the request.</param>
    public Task BeginTransactionAsync(CancellationToken cancellationToken) =>
        Transaction(static (control, token) => control.BeginAsync(token), cancellationToken);

    /// <summary>Whether a transaction with changes is open (started here or by the user's own SQL); false when not supported.</summary>
    /// <param name="cancellationToken">Cancels waiting and the request.</param>
    public Task<bool> HasOpenTransactionAsync(CancellationToken cancellationToken) =>
        _connection is ITransactionControl control ? UseAsync(control.HasOpenTransactionAsync, cancellationToken) : Task.FromResult(false);

    /// <summary>Commits the open transaction.</summary>
    /// <param name="cancellationToken">Cancels waiting and the request.</param>
    public Task CommitAsync(CancellationToken cancellationToken) =>
        Transaction(static (control, token) => control.CommitAsync(token), cancellationToken);

    /// <summary>Rolls the open transaction back.</summary>
    /// <param name="cancellationToken">Cancels waiting and the request.</param>
    public Task RollbackAsync(CancellationToken cancellationToken) =>
        Transaction(static (control, token) => control.RollbackAsync(token), cancellationToken);

    private Task<bool> Transaction(Func<ITransactionControl, CancellationToken, Task> work, CancellationToken cancellationToken) =>
        _connection is ITransactionControl control
            ? UseAsync(async token =>
            {
                await work(control, token).ConfigureAwait(false);
                return true;
            }, cancellationToken)
            : throw new NotSupportedException("This connection has no transaction control.");

    /// <summary>
    /// Gives the channel back: its own connection is closed (the server rolls back what was not committed); the
    /// session's main one stays open, so anything left uncommitted on it is rolled back first, never handed to the
    /// next window.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        if (_isMain && _connection is ITransactionControl control && !Session.IsDisposed)
        {
            try
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await control.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
#pragma warning disable CA1031 // Best effort: a broken connection has nothing left to roll back.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        Session.Release(this);
        if (!_isMain)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Closes the channel's own connection when its session closes.</summary>
    internal async ValueTask CloseConnectionAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0 && !_isMain)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<T> UseAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Session.IsDisposed, Session);
            return await work(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

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
        var (attempt, connection, _, _) = await ConnectAsync(definition, password, cancellationToken).ConfigureAwait(false);
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

        var (attempt, connection, provider, credential) = await ConnectAsync(definition, password, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return attempt;
        }

        var session = new ConnectionSession(definition, provider!, connection, credential);
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
            await session.DisposeAsync().ConfigureAwait(false);
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
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<(ConnectionAttempt Attempt, IDatabaseConnection? Connection, IDatabaseProvider? Provider, ConnectionCredential? Credential)> ConnectAsync(
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
            return (Done(ConnectionOutcome.ProviderMissing, definition.ProviderId), null, null, null);
        }

        var settings = provider as IConnectionSettingsProvider;
        ConnectionCredential? credential = null;
        if (settings?.RequiresCredential(definition.Settings) ?? true)
        {
            password ??= await _credentials.GetPasswordAsync(definition.Id, cancellationToken).ConfigureAwait(false);
            if (password is null)
            {
                return (Done(ConnectionOutcome.PasswordRequired), null, provider, null);
            }

            credential = new ConnectionCredential(definition.UserName, password);
        }

        if (settings?.GetEndpoint(definition.Settings) is { } endpoint &&
            await _probe.ProbeAsync(endpoint, ProbeTimeout, cancellationToken).ConfigureAwait(false) is { } unreachable)
        {
            return (Done(ConnectionOutcome.Unreachable, unreachable), null, provider, null);
        }

        try
        {
            var connection = await provider.OpenConnectionAsync(definition.ToProfile(), credential, cancellationToken).ConfigureAwait(false);
            return (Done(ConnectionOutcome.Succeeded), connection, provider, credential);
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
            return (Done(ConnectionOutcome.Failed, ex.Message), null, provider, null);
        }
    }
}
