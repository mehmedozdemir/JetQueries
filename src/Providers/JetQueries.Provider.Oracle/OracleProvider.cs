using System.Globalization;
using JetQueries.Provider.Oracle.Language;
using JetQueries.Providers.Language;
using JetQueries.Providers.Metadata;
using JetQueries.Sdk;
using Oracle.ManagedDataAccess.Client;

namespace JetQueries.Provider.Oracle;

/// <summary>Entry point of the Oracle extension.</summary>
public sealed class OracleExtension : IExtension
{
    /// <inheritdoc />
    public void Initialize(IExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterDatabaseProvider(new OracleProvider());
    }
}

/// <summary>Database provider for Oracle Database, on the managed ODP.NET driver (no Oracle client installation needed).</summary>
public sealed class OracleProvider : IDatabaseProvider, IConnectionSettingsProvider, IScriptSplitter, IRowCountQueryBuilder, IResultFilterQueryBuilder, IRowLimitedSelect, IConnectionImport,
    IConnectionStringImport, IProviderAppearance, IObjectScripting
{
    /// <summary>The provider identifier stored in connection profiles.</summary>
    public const string ProviderId = "oracle";

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public string DisplayName => "Oracle";

    /// <inheritdoc />
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Schemas | ProviderCapabilities.Transactions | ProviderCapabilities.ServerSideCancel |
        ProviderCapabilities.Routines | ProviderCapabilities.Packages | ProviderCapabilities.Sequences |
        ProviderCapabilities.Synonyms | ProviderCapabilities.UserTypes;

    /// <inheritdoc />
    public string IconPathData => "M8 6h8a6 6 0 0 1 0 12H8A6 6 0 0 1 8 6z M8.5 8.5h7a3.5 3.5 0 0 1 0 7h-7a3.5 3.5 0 0 1 0-7z";

    /// <inheritdoc />
    public string AccentColor => "#D9622B";

    /// <inheritdoc />
    public string Description => "Oracle Database, no client installation needed";

    /// <inheritdoc />
    public ParsedConnectionString ParseConnectionString(string connectionString) => OracleConnectionStringParser.Parse(connectionString);

    /// <inheritdoc />
    public ISqlLanguageService? LanguageService { get; } = new OracleLanguageService();

    /// <inheritdoc />
    public IReadOnlyList<ConnectionSettingDescriptor> ConnectionSettings { get; } =
    [
        new(OracleConnectionSettings.Host, "Host", ConnectionSettingKind.Text) { IsRequired = true },
        new(OracleConnectionSettings.Port, "Port", ConnectionSettingKind.Number) { DefaultValue = "1521" },
        new(OracleConnectionSettings.ServiceName, "Service name", ConnectionSettingKind.Text) { Hint = "for example ORCLPDB1; leave empty when a SID is given" },
        new(OracleConnectionSettings.Sid, "SID", ConnectionSettingKind.Text) { Hint = "only for databases without a service name" },
        new(OracleConnectionSettings.Role, "Connect as", ConnectionSettingKind.Choice)
        {
            DefaultValue = "normal",
            Options = [new("normal", "Normal"), new("sysdba", "SYSDBA"), new("sysoper", "SYSOPER")],
        },
        new(OracleConnectionSettings.ConnectTimeoutSeconds, "Connect timeout (s)", ConnectionSettingKind.Number) { DefaultValue = "15" },
    ];

    /// <inheritdoc />
    public bool RequiresCredential(IReadOnlyDictionary<string, string> settings) => true;

    /// <inheritdoc />
    public NetworkEndpoint? GetEndpoint(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.TryGetValue(OracleConnectionSettings.Host, out var host) && !string.IsNullOrWhiteSpace(host) &&
               OracleConnectionSettings.TryGetPort(settings, out var port)
            ? new NetworkEndpoint(host.Trim(), port)
            : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<ScriptBatch> SplitBatches(string script) => OracleScriptSplitter.Split(script);

    /// <inheritdoc />
    public string? BuildCountQuery(string sql) =>
        CountQueryBuilder.Build(sql.TrimEnd().TrimEnd('/').TrimEnd(), PlSqlTokenizer.Tokenize(sql.TrimEnd().TrimEnd('/').TrimEnd()), withAllowedInside: true, orderByNeedsPaging: false);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> ToSettings(ImportedEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (endpoint.Host is { Length: > 0 } host)
        {
            settings[OracleConnectionSettings.Host] = host;
        }

        settings[OracleConnectionSettings.Port] = (endpoint.Port ?? 1521).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (endpoint.ServiceName is { Length: > 0 } service)
        {
            settings[OracleConnectionSettings.ServiceName] = service;
        }
        else if (endpoint.Sid is { Length: > 0 } sid)
        {
            settings[OracleConnectionSettings.Sid] = sid;
        }
        else if (endpoint.Database is { Length: > 0 } database)
        {
            settings[OracleConnectionSettings.ServiceName] = database;
        }

        return settings;
    }

    /// <inheritdoc />
    public string SelectRows(string qualifiedName, IReadOnlyList<string> quotedColumns, int rows)
    {
        ArgumentNullException.ThrowIfNull(qualifiedName);
        ArgumentNullException.ThrowIfNull(quotedColumns);
        return "SELECT " + Columns(quotedColumns) + "\nFROM " + qualifiedName + "\nFETCH FIRST " + rows.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ROWS ONLY";

        // One column per line once there are several.
        static string Columns(IReadOnlyList<string> columns) => columns.Count == 0 ? "*" : columns.Count <= 3 ? string.Join(", ", columns) : "\n    " + string.Join(",\n    ", columns);
    }

    /// <inheritdoc />
    public string? BuildFilterQuery(string sql, string condition)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(condition);
        var query = sql.TrimEnd().TrimEnd('/').TrimEnd();
        return CountQueryBuilder.BuildFilter(query, PlSqlTokenizer.Tokenize(query), condition, PlSqlTokenizer.Tokenize(condition), withAllowedInside: true, orderByNeedsPaging: false);
    }

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier) => OracleNames.Quote(identifier);


    /// <inheritdoc />
    public string? ScriptDrop(DatabaseObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Kind is DatabaseObjectKinds.Database or DatabaseObjectKinds.Schema || DdlText.DropKeyword(target.Kind) is not { } keyword
            ? null
            : $"DROP {keyword} {OracleNames.Quote(target.Name)};";
    }

    /// <inheritdoc />
    public string? ScriptAlter(DatabaseObject target, string definition)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(definition);

        // Tables, sequences and synonyms are changed by ALTER statements written for the change, not by their DDL.
        return target.Kind is DatabaseObjectKinds.View or DatabaseObjectKinds.Procedure or DatabaseObjectKinds.Function or DatabaseObjectKinds.Package
            or DatabaseObjectKinds.PackageBody or DatabaseObjectKinds.Trigger or DatabaseObjectKinds.Type
            ? DdlText.ReplaceCreate(definition, "CREATE OR REPLACE")
            : null;
    }

    /// <inheritdoc />
    public async Task<IDatabaseConnection> OpenConnectionAsync(
        ConnectionProfile profile,
        ConnectionCredential? credential,
        CancellationToken cancellationToken)
    {
        // The connection string holds the password: it lives only in memory, here and in the connection object
        // (which needs it to open a second connection that cancels a waiting statement on the server).
        var connectionString = OracleConnectionSettings.BuildConnectionString(profile, credential);
        var connection = new OracleConnection(connectionString);
        string? sid = null;
        long? serial = null;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // DBMS_OUTPUT is how PL/SQL prints; without this the buffer stays off and nothing can be read back.
            var enable = connection.CreateCommand();
            await using (enable.ConfigureAwait(false))
            {
                enable.CommandText = "BEGIN DBMS_OUTPUT.ENABLE(NULL); END;";
                await enable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Who this session is, for a server-side cancel later (best effort: the serial number needs a
            // package that is not granted everywhere; it is then looked up when needed).
            var identity = connection.CreateCommand();
            await using (identity.ConfigureAwait(false))
            {
                identity.CommandText = "SELECT SYS_CONTEXT('USERENV', 'SID') FROM dual";
                sid = Convert.ToString(await identity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    identity.CommandText = "SELECT dbms_debug_jdwp.current_session_serial FROM dual";
                    serial = Convert.ToInt64(await identity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (OracleException)
                {
                    serial = null;
                }
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new OracleDatabaseConnection(connection, connectionString, sid, serial);
    }
}

internal sealed class OracleDatabaseConnection(OracleConnection connection, string? connectionString = null, string? sid = null, long? serial = null)
    : IDatabaseConnection, IDatabaseContext, ITransactionControl
{
    // ODP.NET commits each command on its own unless a transaction object is open on the connection.
    private OracleTransaction? _transaction;

    public Task BeginAsync(CancellationToken cancellationToken)
    {
        _transaction ??= connection.BeginTransaction();
        return Task.CompletedTask;
    }

    /// <summary>The server's view: a transaction exists once something changed (also after the user's own COMMIT it does not).</summary>
    public async Task<bool> HasOpenTransactionAsync(CancellationToken cancellationToken) =>
        await ScalarAsync("SELECT dbms_transaction.local_transaction_id FROM dual", 30, cancellationToken).ConfigureAwait(false) is string;

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (_transaction is { } transaction)
        {
            _transaction = null;
            await using (transaction.ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await ScalarAsync("COMMIT", 0, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (_transaction is { } transaction)
        {
            _transaction = null;
            await using (transaction.ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await ScalarAsync("ROLLBACK", 0, cancellationToken).ConfigureAwait(false);
        }
    }

    // Short commands of the transaction control: constant text, no parameters.
    private async Task<object?> ScalarAsync(string sql, int timeout, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Constant text.
            command.CommandText = sql;
#pragma warning restore CA2100
            command.CommandTimeout = timeout;
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public IQueryExecutor Executor { get; } = new OracleQueryExecutor(connection, token => CancelOnServerAsync(connectionString, sid, serial, token));

    /// <summary>
    /// Asks the server, from a second connection, to cancel what this session is running (ALTER SYSTEM CANCEL SQL,
    /// Oracle 18c and later). False when it could not: no such privilege, or the session could not be named.
    /// </summary>
    internal static async Task<bool> CancelOnServerAsync(string? connectionString, string? sid, long? serial, CancellationToken cancellationToken)
    {
        if (connectionString is null || !int.TryParse(sid, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var sessionId))
        {
            return false;
        }

        try
        {
            var second = new OracleConnection(connectionString);
            await using (second.ConfigureAwait(false))
            {
                await second.OpenAsync(cancellationToken).ConfigureAwait(false);
                var command = second.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    var serialNumber = serial;
                    if (serialNumber is null)
                    {
                        command.CommandText = "SELECT serial# FROM v$session WHERE sid = :sid";
                        command.Parameters.Add(new OracleParameter("sid", sessionId));
                        serialNumber = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
                        command.Parameters.Clear();
                    }

                    // ALTER SYSTEM takes no bind variables: the only variable parts are two integers.
#pragma warning disable CA2100 // Integers formatted invariantly; nothing typed by a user.
                    command.CommandText = string.Create(
                        System.Globalization.CultureInfo.InvariantCulture, $"ALTER SYSTEM CANCEL SQL '{sessionId}, {serialNumber}'");
#pragma warning restore CA2100
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }
            }
        }
        catch (OracleException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
    }

    public IMetadataProvider Metadata { get; } = new OracleMetadataProvider(connection);

    /// <summary>An Oracle connection is to one database; what a query window switches is the current schema.</summary>
    public DatabaseContextKind Kind => DatabaseContextKind.Schema;

    public async Task<string?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var names = await QueryAsync("SELECT SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') FROM dual", cancellationToken).ConfigureAwait(false);
        return names.Count > 0 ? names[0] : null;
    }

    /// <summary>The schemas the explorer shows: not Oracle's own, and always the user's.</summary>
    public Task<IReadOnlyList<string>> GetAvailableAsync(CancellationToken cancellationToken) =>
        QueryAsync("SELECT u.username FROM all_users u WHERE u.oracle_maintained = 'N' OR u.username = USER ORDER BY u.username", cancellationToken);

    public async Task ChangeAsync(string name, CancellationToken cancellationToken)
    {
        // ALTER SESSION takes no bind variables: the name is quoted by the provider's quoting helper.
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // The only variable part is a quoted identifier.
            command.CommandText = "ALTER SESSION SET CURRENT_SCHEMA = " + OracleNames.Quote(name);
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();

    private async Task<IReadOnlyList<string>> QueryAsync(string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Constant text.
            command.CommandText = sql;
#pragma warning restore CA2100
            command.CommandTimeout = 30;
            var names = new List<string>();
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    names.Add(reader.GetString(0));
                }
            }

            return names;
        }
    }
}

internal static class OracleNames
{
    /// <summary>
    /// Double-quotes one identifier part. Oracle has no escape for a double quote inside a quoted identifier —
    /// such a name cannot exist — so one is refused rather than passed through.
    /// </summary>
    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        if (identifier.Contains('"', StringComparison.Ordinal) || identifier.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("An Oracle identifier cannot contain a double quote.", nameof(identifier));
        }

        return "\"" + identifier + "\"";
    }

    /// <summary>Quoted <c>"SCHEMA"."NAME"</c>.</summary>
    public static string Quote(DatabaseObjectName name) =>
        string.IsNullOrEmpty(name.Schema) ? Quote(name.Name) : Quote(name.Schema) + "." + Quote(name.Name);
}

/// <summary>
/// Turns a connection profile into an ODP.NET connection string. Settings: <c>host</c> (required), <c>port</c>
/// (default 1521), <c>serviceName</c> or <c>sid</c> (one of them), <c>role</c> (<c>normal</c>, <c>sysdba</c>,
/// <c>sysoper</c>), <c>connectTimeoutSeconds</c> (default 15).
/// </summary>
internal static class OracleConnectionSettings
{
    public const string Host = "host";
    public const string Port = "port";
    public const string ServiceName = "serviceName";
    public const string Sid = "sid";
    public const string Role = "role";
    public const string ConnectTimeoutSeconds = "connectTimeoutSeconds";

    public static bool TryGetPort(IReadOnlyDictionary<string, string> settings, out int port)
    {
        port = 1521;
        return !settings.TryGetValue(Port, out var text) || string.IsNullOrWhiteSpace(text) ||
               (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535);
    }

    /// <summary>Builds the connection string. The result contains the password and must never be logged or stored.</summary>
    /// <exception cref="ArgumentException">A setting is missing or has a value that is not understood.</exception>
    public static string BuildConnectionString(ConnectionProfile profile, ConnectionCredential? credential)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var settings = profile.Settings;

        var host = Required(settings, Host, "The connection has no host.");
        if (!TryGetPort(settings, out var port))
        {
            throw new ArgumentException($"'{Port}' must be a whole number from 1 to 65535.", nameof(profile));
        }

        var service = settings.GetValueOrDefault(ServiceName)?.Trim();
        var sid = settings.GetValueOrDefault(Sid)?.Trim();
        if (string.IsNullOrEmpty(service) == string.IsNullOrEmpty(sid))
        {
            throw new ArgumentException("Give either a service name or a SID.", nameof(profile));
        }

        if (string.IsNullOrEmpty(credential?.UserName))
        {
            throw new ArgumentException("An Oracle connection needs a user name.", nameof(credential));
        }

        // The address goes into a connect descriptor, whose syntax is built from parentheses and '='. Those
        // characters cannot occur in a host, service or SID, so a value containing them is refused instead of
        // being allowed to change the descriptor.
        var target = string.IsNullOrEmpty(service) ? $"(SID={Plain(sid!, Sid)})" : $"(SERVICE_NAME={Plain(service, ServiceName)})";
        var builder = new OracleConnectionStringBuilder
        {
            DataSource = string.Create(
                CultureInfo.InvariantCulture,
                $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={Plain(host, Host)})(PORT={port}))(CONNECT_DATA={target}))"),
            UserID = credential.UserName,
            Password = credential.Password ?? string.Empty,
            PersistSecurityInfo = false,
            ConnectionTimeout = 15,
        };

        var role = settings.GetValueOrDefault(Role, "normal").Trim().ToUpperInvariant();
        switch (role)
        {
            case "" or "NORMAL":
                break;
            case "SYSDBA" or "SYSOPER":
                builder.DBAPrivilege = role;
                break;
            default:
                throw new ArgumentException($"Unknown role '{settings[Role]}'. Use 'normal', 'sysdba' or 'sysoper'.", nameof(profile));
        }

        if (settings.TryGetValue(ConnectTimeoutSeconds, out var timeout) && !string.IsNullOrWhiteSpace(timeout))
        {
            if (!int.TryParse(timeout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 600)
            {
                throw new ArgumentException($"'{ConnectTimeoutSeconds}' must be a whole number from 1 to 600.", nameof(profile));
            }

            builder.ConnectionTimeout = seconds;
        }

        return builder.ConnectionString;
    }

    private static string Required(IReadOnlyDictionary<string, string> settings, string key, string message) =>
        settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new ArgumentException(message, nameof(settings));

    private static string Plain(string value, string setting)
    {
        foreach (var c in value)
        {
            if (!(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '$' or '#'))
            {
                throw new ArgumentException($"'{setting}' contains a character that is not allowed there: '{c}'.", nameof(value));
            }
        }

        return value;
    }
}
