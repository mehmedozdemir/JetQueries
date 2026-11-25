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
public sealed class SqlServerProvider : IDatabaseProvider, IBulkInsertScripting, IRowEditScripting, IQueryParameterSyntax, IRoutineExecutionScripting, IConnectionSettingsProvider, IScriptSplitter, IRowCountQueryBuilder, IResultFilterQueryBuilder, IRowLimitedSelect, IConnectionImport,
    IConnectionStringImport, IProviderAppearance, IObjectScripting, ISystemObjectClassifier
{
    // The databases every SQL Server has (database_id 1 to 4).
    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase) { "master", "model", "msdb", "tempdb" };

    /// <inheritdoc />
    public bool IsSystemObject(DatabaseObject databaseObject)
    {
        ArgumentNullException.ThrowIfNull(databaseObject);
        return databaseObject.Kind == DatabaseObjectKinds.Database && SystemDatabases.Contains(databaseObject.Name.Name);
    }

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
    public string IconPathData => "M5.5 4h13A1.5 1.5 0 0 1 20 5.5v4A1.5 1.5 0 0 1 18.5 11h-13A1.5 1.5 0 0 1 4 9.5v-4A1.5 1.5 0 0 1 5.5 4z M7 6.5h1.5v2H7z M10.5 6.5h1.5v2h-1.5z M5.5 13h13a1.5 1.5 0 0 1 1.5 1.5v4a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 18.5v-4A1.5 1.5 0 0 1 5.5 13z M7 15.5h1.5v2H7z M10.5 15.5h1.5v2h-1.5z";

    /// <inheritdoc />
    public string AccentColor => "#B4262D";

    /// <inheritdoc />
    public string Description => "SQL Server and Azure SQL";

    /// <inheritdoc />
    public ParsedConnectionString ParseConnectionString(string connectionString) => SqlServerConnectionStringParser.Parse(connectionString);

    /// <inheritdoc />
    public string? ScriptDrop(DatabaseObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind is DatabaseObjectKinds.Package or DatabaseObjectKinds.PackageBody || JetQueries.Providers.Metadata.DdlText.DropKeyword(target.Kind) is not { } keyword)
        {
            return null;
        }

        // The query tab's database is the object's own; the name is schema-qualified only.
        return "DROP " + keyword + " " + SqlServerNames.Quote(target.Name with { Database = null }) + ";\nGO\n";
    }

    /// <inheritdoc />
    public string? ScriptAlter(DatabaseObject target, string definition)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(definition);
        return target.Kind is DatabaseObjectKinds.View or DatabaseObjectKinds.Procedure or DatabaseObjectKinds.Function or DatabaseObjectKinds.Trigger
            ? JetQueries.Providers.Metadata.DdlText.ReplaceCreate(definition, "ALTER")
            : null;
    }

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
    /// <remarks>"host,port" (an instance name is dropped: the tunnel already reaches its port).</remarks>
    public IReadOnlyDictionary<string, string>? WithEndpoint(IReadOnlyDictionary<string, string> settings, NetworkEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(endpoint);
        return new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase)
        {
            [SqlServerConnectionSettings.Server] = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{endpoint.Host},{endpoint.Port}"),
        };
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
    /// <remarks>2100 parameters a statement and 1000 rows in a VALUES list.</remarks>
    public int RowsPerStatement(int columnCount) => Providers.Metadata.BulkInsert.Rows(columnCount, 2000, 1000);

    /// <inheritdoc />
    public string ParameterReference(string name) => "@" + name;

    /// <inheritdoc />
    public string ScriptUpdate(DatabaseObjectName table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Providers.Metadata.RowEdit.Update(SqlServerNames.Quote, '@', RowEditTable(table), columns, keyColumns);
    }

    /// <inheritdoc />
    public string ScriptDelete(DatabaseObjectName table, IReadOnlyList<string> keyColumns)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Providers.Metadata.RowEdit.Delete(SqlServerNames.Quote, '@', RowEditTable(table), keyColumns);
    }

    private static string RowEditTable(DatabaseObjectName table) => SqlServerNames.Quote(table);

    /// <inheritdoc />
    public string ScriptInsert(DatabaseObjectName table, IReadOnlyList<string> columns, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Providers.Metadata.BulkInsert.Values(SqlServerNames.Quote, '@', SqlServerNames.Quote(table), columns, rowCount);
    }

    /// <inheritdoc />
    public string? ScriptExecute(DatabaseObject routine, SqlSignature signature, IReadOnlyList<RoutineArgument> arguments) =>
        SqlServerExecuteScript.Build(routine, signature, arguments);

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

internal sealed class SqlServerConnection(SqlConnection connection) : IDatabaseConnection, IDatabaseContext, ITransactionControl, IActivityMonitor, IPerformanceAdvisor, IServerDashboard
{
    public Task<ServerOverview> GetOverviewAsync(CancellationToken cancellationToken) => SqlServerDashboard.OverviewAsync(connection, cancellationToken);

    public Task<ServerSample> GetSampleAsync(CancellationToken cancellationToken) => SqlServerDashboard.SampleAsync(connection, cancellationToken);

    public Task<HeavyQueryReport> GetHeavyQueriesAsync(HeavyQueryRequest request, CancellationToken cancellationToken) =>
        SqlServerPerformance.HeavyAsync(connection, request, cancellationToken);

    public Task<IReadOnlyList<MissingIndex>> GetMissingIndexesAsync(string? database, CancellationToken cancellationToken) =>
        SqlServerPerformance.MissingAsync(connection, database, cancellationToken);

    public Task<IndexUsageReport> GetIndexUsageAsync(string database, CancellationToken cancellationToken) =>
        SqlServerPerformance.UsageAsync(connection, database, cancellationToken);

    public Task<IReadOnlyList<IndexFragmentation>> GetFragmentationAsync(string database, long minimumPages, CancellationToken cancellationToken) =>
        SqlServerPerformance.FragmentationAsync(connection, database, minimumPages, cancellationToken);

    public Task<ActivitySnapshot> GetActivityAsync(CancellationToken cancellationToken) => SqlServerActivity.ReadAsync(connection, cancellationToken);

    public Task KillSessionAsync(string sessionId, CancellationToken cancellationToken) => SqlServerActivity.KillAsync(connection, sessionId, cancellationToken);

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
