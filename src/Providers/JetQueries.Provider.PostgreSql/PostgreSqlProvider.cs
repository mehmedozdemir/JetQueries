using System.Globalization;
using JetQueries.Provider.PostgreSql.Language;
using JetQueries.Providers.Language;
using JetQueries.Providers.Metadata;
using JetQueries.Sdk;
using Npgsql;

namespace JetQueries.Provider.PostgreSql;

/// <summary>Entry point of the PostgreSQL extension.</summary>
public sealed class PostgreSqlExtension : IExtension
{
    /// <inheritdoc />
    public void Initialize(IExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterDatabaseProvider(new PostgreSqlProvider());
    }
}

/// <summary>Database provider for PostgreSQL, on the Npgsql driver.</summary>
public sealed class PostgreSqlProvider : IDatabaseProvider, IConnectionSettingsProvider, IScriptSplitter, IRowCountQueryBuilder, IResultFilterQueryBuilder, IRowLimitedSelect, IConnectionImport,
    IConnectionStringImport, IProviderAppearance, IObjectScripting
{
    /// <summary>The provider identifier stored in connection profiles.</summary>
    public const string ProviderId = "postgresql";

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public string DisplayName => "PostgreSQL";

    /// <inheritdoc />
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Schemas | ProviderCapabilities.MultipleResultSets | ProviderCapabilities.Transactions |
        ProviderCapabilities.ServerSideCancel | ProviderCapabilities.Routines | ProviderCapabilities.Sequences | ProviderCapabilities.UserTypes;

    /// <inheritdoc />
    public string IconPathData => "M4 6c0-1.7 3.6-3 8-3s8 1.3 8 3v12c0 1.7-3.6 3-8 3s-8-1.3-8-3z M12 7.6c-3.3 0-5.8-.8-6.4-1.6.6-.8 3.1-1.6 6.4-1.6s5.8.8 6.4 1.6c-.6.8-3.1 1.6-6.4 1.6z M4 11.4c1 1.3 4.3 2.2 8 2.2s7-.9 8-2.2V13c-1 1.3-4.3 2.2-8 2.2S5 14.3 4 13z";

    /// <inheritdoc />
    public string AccentColor => "#336791";

    /// <inheritdoc />
    public string Description => "PostgreSQL and compatible servers";

    /// <inheritdoc />
    public ParsedConnectionString ParseConnectionString(string connectionString) => PostgreSqlConnectionStringParser.Parse(connectionString);

    /// <inheritdoc />
    public ISqlLanguageService? LanguageService { get; } = new GenericLanguageService(PostgreSqlDialect.Instance);

    /// <inheritdoc />
    public IReadOnlyList<ConnectionSettingDescriptor> ConnectionSettings { get; } =
    [
        new(PostgreSqlConnectionSettings.Host, "Host", ConnectionSettingKind.Text) { IsRequired = true },
        new(PostgreSqlConnectionSettings.Port, "Port", ConnectionSettingKind.Number) { DefaultValue = "5432" },
        new(PostgreSqlConnectionSettings.Database, "Database", ConnectionSettingKind.Text) { DefaultValue = "postgres" },
        new(PostgreSqlConnectionSettings.SslMode, "SSL", ConnectionSettingKind.Choice)
        {
            DefaultValue = "prefer",
            Options =
            [
                new("prefer", "Prefer"), new("disable", "Disable"), new("require", "Require"), new("verify-ca", "Verify CA"),
                new("verify-full", "Verify full"),
            ],
        },
        new(PostgreSqlConnectionSettings.ConnectTimeoutSeconds, "Connect timeout (s)", ConnectionSettingKind.Number) { DefaultValue = "15" },
    ];

    /// <inheritdoc />
    public bool RequiresCredential(IReadOnlyDictionary<string, string> settings) => true;

    /// <inheritdoc />
    public NetworkEndpoint? GetEndpoint(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Several hosts (failover) or a Unix socket directory cannot be probed as one TCP endpoint.
        return settings.TryGetValue(PostgreSqlConnectionSettings.Host, out var host) && !string.IsNullOrWhiteSpace(host) &&
               !host.Contains(',', StringComparison.Ordinal) && !host.TrimStart().StartsWith('/') &&
               PostgreSqlConnectionSettings.TryGetPort(settings, out var port)
            ? new NetworkEndpoint(host.Trim(), port)
            : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<ScriptBatch> SplitBatches(string script) =>
        SqlStatementSplitter.Split(script, PostgreSqlSyntax.Tokenizer.Tokenize(script), StatementBlockRule.BeginAtomic);

    /// <inheritdoc />
    public string? BuildCountQuery(string sql) =>
        CountQueryBuilder.Build(sql, PostgreSqlSyntax.Tokenizer.Tokenize(sql), withAllowedInside: true, orderByNeedsPaging: false);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> ToSettings(ImportedEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (endpoint.Host is { Length: > 0 } host)
        {
            settings[PostgreSqlConnectionSettings.Host] = host;
        }

        settings[PostgreSqlConnectionSettings.Port] = (endpoint.Port ?? 5432).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (endpoint.Database is { Length: > 0 } database)
        {
            settings[PostgreSqlConnectionSettings.Database] = database;
        }

        return settings;
    }

    /// <inheritdoc />
    public string SelectRows(string qualifiedName, IReadOnlyList<string> quotedColumns, int rows)
    {
        ArgumentNullException.ThrowIfNull(qualifiedName);
        ArgumentNullException.ThrowIfNull(quotedColumns);
        return "SELECT " + Columns(quotedColumns) + "\nFROM " + qualifiedName + "\nLIMIT " + rows.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // One column per line once there are several.
        static string Columns(IReadOnlyList<string> columns) => columns.Count == 0 ? "*" : columns.Count <= 3 ? string.Join(", ", columns) : "\n    " + string.Join(",\n    ", columns);
    }

    /// <inheritdoc />
    public string? BuildFilterQuery(string sql, string condition)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(condition);
        return CountQueryBuilder.BuildFilter(sql, PostgreSqlSyntax.Tokenizer.Tokenize(sql), condition, PostgreSqlSyntax.Tokenizer.Tokenize(condition), withAllowedInside: true, orderByNeedsPaging: false);
    }

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier) => PostgreSqlNames.Quote(identifier);


    /// <inheritdoc />
    public string? ScriptDrop(DatabaseObject target)
    {
        ArgumentNullException.ThrowIfNull(target);

        // A routine without its argument list is dropped when the name has one overload (PostgreSQL 10+);
        // a trigger needs its table, which the name does not carry.
        return target.Kind is DatabaseObjectKinds.Table or DatabaseObjectKinds.View or DatabaseObjectKinds.Procedure or DatabaseObjectKinds.Function
            or DatabaseObjectKinds.Sequence or DatabaseObjectKinds.Type && DdlText.DropKeyword(target.Kind) is { } keyword
            ? $"DROP {keyword} {PostgreSqlNames.Quote(target.Name.Schema, target.Name.Name)};"
            : null;
    }

    /// <inheritdoc />
    public string? ScriptAlter(DatabaseObject target, string definition)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(definition);
        return target.Kind switch
        {
            // A materialized view has no OR REPLACE.
            DatabaseObjectKinds.View when DdlText.ReplaceCreate(definition, string.Empty) is { } rest &&
                                          rest.TrimStart().StartsWith("MATERIALIZED", StringComparison.OrdinalIgnoreCase) => null,
            DatabaseObjectKinds.View or DatabaseObjectKinds.Function or DatabaseObjectKinds.Procedure => DdlText.ReplaceCreate(definition, "CREATE OR REPLACE"),
            _ => null,
        };
    }

    /// <inheritdoc />
    public async Task<IDatabaseConnection> OpenConnectionAsync(
        ConnectionProfile profile,
        ConnectionCredential? credential,
        CancellationToken cancellationToken)
    {
        // The connection string holds the password: it lives only in this local and inside the driver.
        // Unmapped types (the user's enums, ranges, composites) are read as text instead of failing; that is
        // opt-in on a data source, so each session connection gets its own small one.
        var dataSource = new NpgsqlDataSourceBuilder(PostgreSqlConnectionSettings.BuildConnectionString(profile, credential))
            .EnableUnmappedTypes()
            .Build();
        try
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return new PostgreSqlDatabaseConnection(profile, credential, dataSource, connection);
        }
        catch
        {
            await dataSource.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// A session connection. A PostgreSQL connection is to one database; switching database (the query window's
/// selector) opens a connection to the other database and replaces this one's.
/// </summary>
internal sealed class PostgreSqlDatabaseConnection : IDatabaseConnection, IDatabaseContext, ITransactionControl
{
    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        if (!await HasOpenTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await ScalarAsync("BEGIN", 30, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Open once it has changed something (it then has a transaction id), as Oracle counts it; a transaction
    /// that failed (25P02) is open until it is rolled back.
    /// </summary>
    public async Task<bool> HasOpenTransactionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ScalarAsync("SELECT txid_current_if_assigned()", 30, cancellationToken).ConfigureAwait(false) is not (null or DBNull);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InFailedSqlTransaction)
        {
            return true;
        }
    }

    /// <summary>COMMIT of a failed transaction rolls it back, as PostgreSQL does.</summary>
    public async Task CommitAsync(CancellationToken cancellationToken) =>
        await ScalarAsync("COMMIT", 0, cancellationToken).ConfigureAwait(false);

    public async Task RollbackAsync(CancellationToken cancellationToken) =>
        await ScalarAsync("ROLLBACK", 0, cancellationToken).ConfigureAwait(false);

    // Short commands of the transaction control: constant text, no parameters.
    private async Task<object?> ScalarAsync(string sql, int timeout, CancellationToken cancellationToken)
    {
        var command = _connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Constant text.
            command.CommandText = sql;
#pragma warning restore CA2100
            command.CommandTimeout = timeout;
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private readonly ConnectionProfile _profile;

    // Kept in memory only, for as long as the connection, to connect to another database.
    private readonly ConnectionCredential? _credential;
    private NpgsqlDataSource _dataSource;
    private NpgsqlConnection _connection;

    public PostgreSqlDatabaseConnection(ConnectionProfile profile, ConnectionCredential? credential, NpgsqlDataSource dataSource, NpgsqlConnection connection)
    {
        _profile = profile;
        _credential = credential;
        _dataSource = dataSource;
        _connection = connection;
        Executor = new PostgreSqlQueryExecutor(() => _connection);
        Metadata = new PostgreSqlMetadataProvider(() => _connection);
    }

    public IQueryExecutor Executor { get; }

    public IMetadataProvider Metadata { get; }

    public DatabaseContextKind Kind => DatabaseContextKind.Database;

    // pg_catalog describes the connected database only.
    public bool MetadataSpansDatabases => false;

    public Task<string?> GetCurrentAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(_connection.Database);

    public async Task<IReadOnlyList<string>> GetAvailableAsync(CancellationToken cancellationToken)
    {
        var command = _connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = """
                SELECT d.datname FROM pg_database d
                WHERE d.datallowconn AND NOT d.datistemplate AND has_database_privilege(d.datname, 'CONNECT')
                ORDER BY d.datname
                """;
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

    public async Task ChangeAsync(string name, CancellationToken cancellationToken)
    {
        if (string.Equals(name, _connection.Database, StringComparison.Ordinal))
        {
            return;
        }

        // The new connection is opened first: if it fails, the window stays where it was.
        var settings = new Dictionary<string, string>(_profile.Settings, StringComparer.OrdinalIgnoreCase) { [PostgreSqlConnectionSettings.Database] = name };
        var dataSource = new NpgsqlDataSourceBuilder(PostgreSqlConnectionSettings.BuildConnectionString(_profile with { Settings = settings }, _credential))
            .EnableUnmappedTypes()
            .Build();
        NpgsqlConnection connection;
        try
        {
            connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await dataSource.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var (oldSource, oldConnection) = (_dataSource, _connection);
        (_dataSource, _connection) = (dataSource, connection);
        await oldConnection.DisposeAsync().ConfigureAwait(false);
        await oldSource.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        await _dataSource.DisposeAsync().ConfigureAwait(false);
    }
}

internal static class PostgreSqlNames
{
    /// <summary>Double-quotes one identifier part, doubling any double quote inside it.</summary>
    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        if (identifier.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A PostgreSQL identifier cannot contain a NUL character.", nameof(identifier));
        }

        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>Quoted <c>"schema"."name"</c>.</summary>
    public static string Quote(string? schema, string name) => string.IsNullOrEmpty(schema) ? Quote(name) : Quote(schema) + "." + Quote(name);

    /// <summary>A string literal, for DDL text the provider writes itself (never for executing values).</summary>
    public static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

/// <summary>
/// Turns a connection profile into an Npgsql connection string. Settings: <c>host</c> (required), <c>port</c>
/// (default 5432), <c>database</c> (default postgres), <c>sslMode</c> (<c>prefer</c>, <c>disable</c>,
/// <c>require</c>, <c>verify-ca</c>, <c>verify-full</c>), <c>connectTimeoutSeconds</c> (default 15).
/// </summary>
internal static class PostgreSqlConnectionSettings
{
    public const string Host = "host";
    public const string Port = "port";
    public const string Database = "database";
    public const string SslMode = "sslMode";
    public const string ConnectTimeoutSeconds = "connectTimeoutSeconds";

    public static bool TryGetPort(IReadOnlyDictionary<string, string> settings, out int port)
    {
        port = 5432;
        return !settings.TryGetValue(Port, out var text) || string.IsNullOrWhiteSpace(text) ||
               (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535);
    }

    /// <summary>Builds the connection string. The result contains the password and must never be logged or stored.</summary>
    /// <exception cref="ArgumentException">A setting is missing or has a value that is not understood.</exception>
    public static string BuildConnectionString(ConnectionProfile profile, ConnectionCredential? credential)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var settings = profile.Settings;
        if (!settings.TryGetValue(Host, out var host) || string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("The connection has no host.", nameof(profile));
        }

        if (!TryGetPort(settings, out var port))
        {
            throw new ArgumentException($"'{Port}' must be a whole number from 1 to 65535.", nameof(profile));
        }

        if (string.IsNullOrEmpty(credential?.UserName))
        {
            throw new ArgumentException("A PostgreSQL connection needs a user name.", nameof(credential));
        }

        var database = settings.GetValueOrDefault(Database)?.Trim();
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host.Trim(),
            Port = port,
            Database = string.IsNullOrEmpty(database) ? "postgres" : database,
            Username = credential.UserName,
            Password = credential.Password ?? string.Empty,
            PersistSecurityInfo = false,
            ApplicationName = "JetQueries",
            Timeout = 15,

            // Disconnecting in the explorer closes the server session instead of parking it in a pool.
            Pooling = false,
        };

        builder.SslMode = settings.GetValueOrDefault(SslMode, "prefer").Trim().ToUpperInvariant() switch
        {
            "" or "PREFER" => Npgsql.SslMode.Prefer,
            "DISABLE" => Npgsql.SslMode.Disable,
            "REQUIRE" => Npgsql.SslMode.Require,
            "VERIFY-CA" => Npgsql.SslMode.VerifyCA,
            "VERIFY-FULL" => Npgsql.SslMode.VerifyFull,
            _ => throw new ArgumentException($"Unknown SSL mode '{settings[SslMode]}'.", nameof(profile)),
        };

        if (settings.TryGetValue(ConnectTimeoutSeconds, out var timeout) && !string.IsNullOrWhiteSpace(timeout))
        {
            if (!int.TryParse(timeout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 600)
            {
                throw new ArgumentException($"'{ConnectTimeoutSeconds}' must be a whole number from 1 to 600.", nameof(profile));
            }

            builder.Timeout = seconds;
        }

        return builder.ConnectionString;
    }
}
