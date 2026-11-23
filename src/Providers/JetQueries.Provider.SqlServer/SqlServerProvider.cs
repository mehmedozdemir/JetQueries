using JetQueries.Provider.SqlServer.Language;
using JetQueries.Providers.Language;
using JetQueries.Sdk;
using Microsoft.Data.SqlClient;

namespace JetQueries.Provider.SqlServer;

/// <summary>Entry point of the SQL Server extension.</summary>
public sealed class SqlServerExtension : IExtension
{
    /// <inheritdoc />
    public void Initialize(IExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterDatabaseProvider(new SqlServerProvider());
    }
}

/// <summary>Database provider for Microsoft SQL Server and Azure SQL, on Microsoft.Data.SqlClient.</summary>
public sealed class SqlServerProvider : IDatabaseProvider, IConnectionSettingsProvider, IScriptSplitter, IRowCountQueryBuilder, IResultFilterQueryBuilder, IRowLimitedSelect, IConnectionImport
{
    /// <summary>The provider identifier stored in connection profiles.</summary>
    public const string ProviderId = "sqlserver";

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public string DisplayName => "SQL Server";

    /// <inheritdoc />
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.MultipleDatabases | ProviderCapabilities.Schemas | ProviderCapabilities.MultipleResultSets |
        ProviderCapabilities.Transactions | ProviderCapabilities.ServerSideCancel | ProviderCapabilities.Routines |
        ProviderCapabilities.Sequences | ProviderCapabilities.Synonyms | ProviderCapabilities.UserTypes;

    /// <inheritdoc />
    public ISqlLanguageService? LanguageService { get; } = new TSqlLanguageService();

    /// <inheritdoc />
    public IReadOnlyList<ScriptBatch> SplitBatches(string script) => TSqlBatchSplitter.Split(script);

    /// <inheritdoc />
    public IReadOnlyList<ConnectionSettingDescriptor> ConnectionSettings { get; } =
    [
        new(SqlServerConnectionSettings.Server, "Server", ConnectionSettingKind.Text) { IsRequired = true, Hint = "host, host,port or host\\instance" },
        new(SqlServerConnectionSettings.Database, "Database", ConnectionSettingKind.Text) { Hint = "empty: the login's default database" },
        new(SqlServerConnectionSettings.Authentication, "Authentication", ConnectionSettingKind.Choice)
        {
            DefaultValue = "sql",
            Options = [new("sql", "SQL Server login"), new("windows", "Windows (integrated)")],
        },
        new(SqlServerConnectionSettings.Encrypt, "Encrypt", ConnectionSettingKind.Choice)
        {
            DefaultValue = "true",
            Options = [new("true", "Yes"), new("false", "No"), new("strict", "Strict (TDS 8)")],
        },
        new(SqlServerConnectionSettings.TrustServerCertificate, "Trust server certificate", ConnectionSettingKind.Boolean)
        {
            DefaultValue = "false",
            Hint = "needed for self-signed certificates",
        },
        new(SqlServerConnectionSettings.ReadOnlyIntent, "Read-only routing", ConnectionSettingKind.Boolean) { DefaultValue = "false" },
        new(SqlServerConnectionSettings.ConnectTimeoutSeconds, "Connect timeout (s)", ConnectionSettingKind.Number) { DefaultValue = "15" },
    ];

    /// <inheritdoc />
    public bool RequiresCredential(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return !string.Equals(settings.GetValueOrDefault(SqlServerConnectionSettings.Authentication, "sql").Trim(), "windows", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public NetworkEndpoint? GetEndpoint(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SqlServerConnectionSettings.ParseEndpoint(settings.GetValueOrDefault(SqlServerConnectionSettings.Server));
    }

    /// <inheritdoc />
    /// <remarks>SQL Server cannot nest a WITH query or a plain ORDER BY in a derived table; the ORDER BY is left out.</remarks>
    public string? BuildCountQuery(string sql) =>
        CountQueryBuilder.Build(sql, Language.TSqlLexer.Tokenize(sql), withAllowedInside: false, orderByNeedsPaging: true);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> ToSettings(ImportedEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (endpoint.Host is { Length: > 0 } host)
        {
            settings[SqlServerConnectionSettings.Server] = endpoint.Port is { } port && !host.Contains(',', StringComparison.Ordinal)
                ? host + "," + port.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : host;
        }

        if (endpoint.Database is { Length: > 0 } database)
        {
            settings[SqlServerConnectionSettings.Database] = database;
        }

        settings[SqlServerConnectionSettings.Authentication] = endpoint.IntegratedSecurity == true ? "windows" : "sql";
        if (endpoint.Encrypt is { } encrypt)
        {
            settings[SqlServerConnectionSettings.Encrypt] = encrypt ? "true" : "false";
        }

        if (endpoint.TrustServerCertificate is { } trust)
        {
            settings[SqlServerConnectionSettings.TrustServerCertificate] = trust ? "true" : "false";
        }

        return settings;
    }

    /// <inheritdoc />
    public string SelectRows(string qualifiedName, IReadOnlyList<string> quotedColumns, int rows)
    {
        ArgumentNullException.ThrowIfNull(qualifiedName);
        ArgumentNullException.ThrowIfNull(quotedColumns);
        return "SELECT TOP (" + rows.ToString(System.Globalization.CultureInfo.InvariantCulture) + ") " + Columns(quotedColumns) + "\nFROM " + qualifiedName;

        // One column per line once there are several.
        static string Columns(IReadOnlyList<string> columns) => columns.Count == 0 ? "*" : columns.Count <= 3 ? string.Join(", ", columns) : "\n    " + string.Join(",\n    ", columns);
    }

    /// <inheritdoc />
    public string? BuildFilterQuery(string sql, string condition)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(condition);
        return CountQueryBuilder.BuildFilter(sql, Language.TSqlLexer.Tokenize(sql), condition, Language.TSqlLexer.Tokenize(condition), withAllowedInside: false, orderByNeedsPaging: true);
    }

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier) => SqlServerNames.Quote(identifier);

    /// <inheritdoc />
    public async Task<IDatabaseConnection> OpenConnectionAsync(
        ConnectionProfile profile,
        ConnectionCredential? credential,
        CancellationToken cancellationToken)
    {
        // The connection string holds the password: it lives only in this local and inside SqlClient.
        var connection = new SqlConnection(SqlServerConnectionSettings.BuildConnectionString(profile, credential));
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new SqlServerConnection(connection);
    }
}

internal sealed class SqlServerConnection(SqlConnection connection) : IDatabaseConnection, IDatabaseContext, ITransactionControl
{
    /// <summary>Started in T-SQL, so the user's own BEGIN TRAN / COMMIT in the editor work alongside.</summary>
    public async Task BeginAsync(CancellationToken cancellationToken) =>
        await ScalarAsync("IF @@TRANCOUNT = 0 BEGIN TRANSACTION", 30, cancellationToken).ConfigureAwait(false);

    public async Task<bool> HasOpenTransactionAsync(CancellationToken cancellationToken) =>
        Convert.ToInt32(await ScalarAsync("SELECT @@TRANCOUNT", 30, cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) > 0;

    /// <summary>Nested BEGIN TRANs are committed all the way down.</summary>
    public async Task CommitAsync(CancellationToken cancellationToken) =>
        await ScalarAsync("WHILE @@TRANCOUNT > 0 COMMIT TRANSACTION", 0, cancellationToken).ConfigureAwait(false);

    public async Task RollbackAsync(CancellationToken cancellationToken) =>
        await ScalarAsync("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", 0, cancellationToken).ConfigureAwait(false);

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

    public IQueryExecutor Executor { get; } = new SqlServerQueryExecutor(connection);

    public IMetadataProvider Metadata { get; } = new SqlServerMetadataProvider(connection);

    public DatabaseContextKind Kind => DatabaseContextKind.Database;

    /// <summary>SqlClient follows the server's database changes, including a <c>USE</c> in a script.</summary>
    public Task<string?> GetCurrentAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(connection.Database);

    public async Task<IReadOnlyList<string>> GetAvailableAsync(CancellationToken cancellationToken)
    {
        // Online databases the login may enter.
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = "SELECT d.name FROM sys.databases d WHERE d.state = 0 AND HAS_DBACCESS(d.name) = 1 ORDER BY d.name";
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

    public Task ChangeAsync(string name, CancellationToken cancellationToken) => connection.ChangeDatabaseAsync(name, cancellationToken);

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

internal static class SqlServerNames
{
    /// <summary>Brackets one identifier part, doubling any closing bracket inside it.</summary>
    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    /// <summary>Quoted multi-part name, for use as a value of <c>OBJECT_ID(@name)</c> or in SQL text.</summary>
    public static string Quote(DatabaseObjectName name) =>
        string.Join('.', new[] { name.Database, name.Schema, name.Name }.Where(static part => !string.IsNullOrEmpty(part)).Select(static part => Quote(part!)));

    /// <summary><c>[database].</c> in front of <c>sys.…</c> catalog views, or nothing for the connection's current database.</summary>
    public static string CatalogPrefix(string? database) => string.IsNullOrEmpty(database) ? string.Empty : Quote(database) + ".";
}
