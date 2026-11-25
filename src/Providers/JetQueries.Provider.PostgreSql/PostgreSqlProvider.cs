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
public sealed class PostgreSqlProvider : IDatabaseProvider, IBulkInsertScripting, IRowEditScripting, IQueryParameterSyntax, IRoutineExecutionScripting, IConnectionSettingsProvider, IScriptSplitter, IRowCountQueryBuilder, IResultFilterQueryBuilder, IRowLimitedSelect, IConnectionImport,
    IConnectionStringImport, IProviderAppearance, IObjectScripting
{
    /// <summary>The provider identifier stored in connection profiles.</summary>
    public const string ProviderId = "postgresql";

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public string DisplayName => "PostgreSQL";

    /// <inheritdoc />
    // Several databases per server; the host reads each on a connection to it (MetadataSpansDatabases is false).
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.MultipleDatabases | ProviderCapabilities.Schemas | ProviderCapabilities.MultipleResultSets | ProviderCapabilities.Transactions |
        ProviderCapabilities.ServerSideCancel | ProviderCapabilities.Routines | ProviderCapabilities.Sequences | ProviderCapabilities.UserTypes;

    /// <summary>The product's logo: Simple Icons 9.0.0 (CC0-1.0), for recognition only; the trademark is its owner's.</summary>
    public string IconPathData => "M23.5594 14.7228a.5269.5269 0 0 0-.0563-.1191c-.139-.2632-.4768-.3418-1.0074-.2321-1.6533.3411-2.2935.1312-2.5256-.0191 1.342-2.0482 2.445-4.522 3.0411-6.8297.2714-1.0507.7982-3.5237.1222-4.7316a1.5641 1.5641 0 0 0-.1509-.235C21.6931.9086 19.8007.0248 17.5099.0005c-1.4947-.0158-2.7705.3461-3.1161.4794a9.449 9.449 0 0 0-.5159-.0816 8.044 8.044 0 0 0-1.3114-.1278c-1.1822-.0184-2.2038.2642-3.0498.8406-.8573-.3211-4.7888-1.645-7.2219.0788C.9359 2.1526.3086 3.8733.4302 6.3043c.0409.818.5069 3.334 1.2423 5.7436.4598 1.5065.9387 2.7019 1.4334 3.582.553.9942 1.1259 1.5933 1.7143 1.7895.4474.1491 1.1327.1441 1.8581-.7279.8012-.9635 1.5903-1.8258 1.9446-2.2069.4351.2355.9064.3625 1.39.3772a.0569.0569 0 0 0 .0004.0041 11.0312 11.0312 0 0 0-.2472.3054c-.3389.4302-.4094.5197-1.5002.7443-.3102.064-1.1344.2339-1.1464.8115-.0025.1224.0329.2309.0919.3268.2269.4231.9216.6097 1.015.6331 1.3345.3335 2.5044.092 3.3714-.6787-.017 2.231.0775 4.4174.3454 5.0874.2212.5529.7618 1.9045 2.4692 1.9043.2505 0 .5263-.0291.8296-.0941 1.7819-.3821 2.5557-1.1696 2.855-2.9059.1503-.8707.4016-2.8753.5388-4.1012.0169-.0703.0357-.1207.057-.1362.0007-.0005.0697-.0471.4272.0307a.3673.3673 0 0 0 .0443.0068l.2539.0223.0149.001c.8468.0384 1.9114-.1426 2.5312-.4308.6438-.2988 1.8057-1.0323 1.5951-1.6698zM2.371 11.8765c-.7435-2.4358-1.1779-4.8851-1.2123-5.5719-.1086-2.1714.4171-3.6829 1.5623-4.4927 1.8367-1.2986 4.8398-.5408 6.108-.13-.0032.0032-.0066.0061-.0098.0094-2.0238 2.044-1.9758 5.536-1.9708 5.7495-.0002.0823.0066.1989.0162.3593.0348.5873.0996 1.6804-.0735 2.9184-.1609 1.1504.1937 2.2764.9728 3.0892.0806.0841.1648.1631.2518.2374-.3468.3714-1.1004 1.1926-1.9025 2.1576-.5677.6825-.9597.5517-1.0886.5087-.3919-.1307-.813-.5871-1.2381-1.3223-.4796-.839-.9635-2.0317-1.4155-3.5126zm6.0072 5.0871c-.1711-.0428-.3271-.1132-.4322-.1772.0889-.0394.2374-.0902.4833-.1409 1.2833-.2641 1.4815-.4506 1.9143-1.0002.0992-.126.2116-.2687.3673-.4426a.3549.3549 0 0 0 .0737-.1298c.1708-.1513.2724-.1099.4369-.0417.156.0646.3078.26.3695.4752.0291.1016.0619.2945-.0452.4444-.9043 1.2658-2.2216 1.2494-3.1676 1.0128zm2.094-3.988-.0525.141c-.133.3566-.2567.6881-.3334 1.003-.6674-.0021-1.3168-.2872-1.8105-.8024-.6279-.6551-.9131-1.5664-.7825-2.5004.1828-1.3079.1153-2.4468.079-3.0586-.005-.0857-.0095-.1607-.0122-.2199.2957-.2621 1.6659-.9962 2.6429-.7724.4459.1022.7176.4057.8305.928.5846 2.7038.0774 3.8307-.3302 4.7363-.084.1866-.1633.3629-.2311.5454zm7.3637 4.5725c-.0169.1768-.0358.376-.0618.5959l-.146.4383a.3547.3547 0 0 0-.0182.1077c-.0059.4747-.054.6489-.115.8693-.0634.2292-.1353.4891-.1794 1.0575-.11 1.4143-.8782 2.2267-2.4172 2.5565-1.5155.3251-1.7843-.4968-2.0212-1.2217a6.5824 6.5824 0 0 0-.0769-.2266c-.2154-.5858-.1911-1.4119-.1574-2.5551.0165-.5612-.0249-1.9013-.3302-2.6462.0044-.2932.0106-.5909.019-.8918a.3529.3529 0 0 0-.0153-.1126 1.4927 1.4927 0 0 0-.0439-.208c-.1226-.4283-.4213-.7866-.7797-.9351-.1424-.059-.4038-.1672-.7178-.0869.067-.276.1831-.5875.309-.9249l.0529-.142c.0595-.16.134-.3257.213-.5012.4265-.9476 1.0106-2.2453.3766-5.1772-.2374-1.0981-1.0304-1.6343-2.2324-1.5098-.7207.0746-1.3799.3654-1.7088.5321a5.6716 5.6716 0 0 0-.1958.1041c.0918-1.1064.4386-3.1741 1.7357-4.4823a4.0306 4.0306 0 0 1 .3033-.276.3532.3532 0 0 0 .1447-.0644c.7524-.5706 1.6945-.8506 2.802-.8325.4091.0067.8017.0339 1.1742.081 1.939.3544 3.2439 1.4468 4.0359 2.3827.8143.9623 1.2552 1.9315 1.4312 2.4543-1.3232-.1346-2.2234.1268-2.6797.779-.9926 1.4189.543 4.1729 1.2811 5.4964.1353.2426.2522.4522.2889.5413.2403.5825.5515.9713.7787 1.2552.0696.087.1372.1714.1885.245-.4008.1155-1.1208.3825-1.0552 1.717-.0123.1563-.0423.4469-.0834.8148-.0461.2077-.0702.4603-.0994.7662zm.8905-1.6211c-.0405-.8316.2691-.9185.5967-1.0105a2.8566 2.8566 0 0 0 .135-.0406 1.202 1.202 0 0 0 .1342.103c.5703.3765 1.5823.4213 3.0068.1344-.2016.1769-.5189.3994-.9533.6011-.4098.1903-1.0957.333-1.7473.3636-.7197.0336-1.0859-.0807-1.1721-.151zm.5695-9.2712c-.0059.3508-.0542.6692-.1054 1.0017-.055.3576-.112.7274-.1264 1.1762-.0142.4368.0404.8909.0932 1.3301.1066.887.216 1.8003-.2075 2.7014a3.5272 3.5272 0 0 1-.1876-.3856c-.0527-.1276-.1669-.3326-.3251-.6162-.6156-1.1041-2.0574-3.6896-1.3193-4.7446.3795-.5427 1.3408-.5661 2.1781-.463zm.2284 7.0137a12.3762 12.3762 0 0 0-.0853-.1074l-.0355-.0444c.7262-1.1995.5842-2.3862.4578-3.4385-.0519-.4318-.1009-.8396-.0885-1.2226.0129-.4061.0666-.7543.1185-1.0911.0639-.415.1288-.8443.1109-1.3505.0134-.0531.0188-.1158.0118-.1902-.0457-.4855-.5999-1.938-1.7294-3.253-.6076-.7073-1.4896-1.4972-2.6889-2.0395.5251-.1066 1.2328-.2035 2.0244-.1859 2.0515.0456 3.6746.8135 4.8242 2.2824a.908.908 0 0 1 .0667.1002c.7231 1.3556-.2762 6.2751-2.9867 10.5405zm-8.8166-6.1162c-.025.1794-.3089.4225-.6211.4225a.5821.5821 0 0 1-.0809-.0056c-.1873-.026-.3765-.144-.5059-.3156-.0458-.0605-.1203-.178-.1055-.2844.0055-.0401.0261-.0985.0925-.1488.1182-.0894.3518-.1226.6096-.0867.3163.0441.6426.1938.6113.4186zm7.9305-.4114c.0111.0792-.049.201-.1531.3102-.0683.0717-.212.1961-.4079.2232a.5456.5456 0 0 1-.075.0052c-.2935 0-.5414-.2344-.5607-.3717-.024-.1765.2641-.3106.5611-.352.297-.0414.6111.0088.6356.1851z";

    /// <inheritdoc />
    public string AccentColor => "#4169E1";

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
    public IReadOnlyDictionary<string, string>? WithEndpoint(IReadOnlyDictionary<string, string> settings, NetworkEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(endpoint);
        return new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase)
        {
            [PostgreSqlConnectionSettings.Host] = endpoint.Host,
            [PostgreSqlConnectionSettings.Port] = endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
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
    public int RowsPerStatement(int columnCount) => Providers.Metadata.BulkInsert.Rows(columnCount, 30000, 1000);

    /// <inheritdoc />
    public string ParameterReference(string name) => "@" + name;

    /// <inheritdoc />
    public string ScriptUpdate(DatabaseObjectName table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Providers.Metadata.RowEdit.Update(PostgreSqlNames.Quote, '@', RowEditTable(table), columns, keyColumns);
    }

    /// <inheritdoc />
    public string ScriptDelete(DatabaseObjectName table, IReadOnlyList<string> keyColumns)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Providers.Metadata.RowEdit.Delete(PostgreSqlNames.Quote, '@', RowEditTable(table), keyColumns);
    }

    private static string RowEditTable(DatabaseObjectName table) => (table.Schema is { Length: > 0 } schema ? PostgreSqlNames.Quote(schema) + "." : string.Empty) + PostgreSqlNames.Quote(table.Name);

    /// <inheritdoc />
    public string ScriptInsert(DatabaseObjectName table, IReadOnlyList<string> columns, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(table);
        var name = (table.Schema is { Length: > 0 } schema ? PostgreSqlNames.Quote(schema) + "." : string.Empty) + PostgreSqlNames.Quote(table.Name);
        return Providers.Metadata.BulkInsert.Values(PostgreSqlNames.Quote, '@', name, columns, rowCount);
    }

    /// <inheritdoc />
    public string? ScriptExecute(DatabaseObject routine, SqlSignature signature, IReadOnlyList<RoutineArgument> arguments) =>
        PostgreSqlExecuteScript.Build(routine, signature, arguments);


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
internal sealed class PostgreSqlDatabaseConnection : IDatabaseConnection, IDatabaseContext, ITransactionControl,
    IActivityMonitor, IPerformanceAdvisor, IServerDashboard, IHealthCheck, IDatabaseDetails
{
    public Task<ActivitySnapshot> GetActivityAsync(CancellationToken cancellationToken) => PostgreSqlDba.ActivityAsync(_connection, cancellationToken);

    public Task KillSessionAsync(string sessionId, CancellationToken cancellationToken) => PostgreSqlDba.KillAsync(_connection, sessionId, cancellationToken);

    public Task<HeavyQueryReport> GetHeavyQueriesAsync(HeavyQueryRequest request, CancellationToken cancellationToken) =>
        PostgreSqlDba.HeavyAsync(_connection, request, cancellationToken);

    public Task<IReadOnlyList<MissingIndex>> GetMissingIndexesAsync(string? database, CancellationToken cancellationToken) =>
        InDatabaseAsync(database, connection => PostgreSqlDba.ScansAsync(connection, cancellationToken), cancellationToken);

    public Task<IndexUsageReport> GetIndexUsageAsync(string database, CancellationToken cancellationToken) =>
        InDatabaseAsync(database, connection => PostgreSqlDba.UsageAsync(connection, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<IndexFragmentation>> GetFragmentationAsync(string database, long minimumPages, CancellationToken cancellationToken) =>
        InDatabaseAsync(database, connection => PostgreSqlDba.FragmentationAsync(connection, minimumPages, cancellationToken), cancellationToken);

    public Task<ServerOverview> GetOverviewAsync(CancellationToken cancellationToken) => PostgreSqlDba.OverviewAsync(_connection, cancellationToken);

    public Task<ServerSample> GetSampleAsync(CancellationToken cancellationToken) => PostgreSqlDba.SampleAsync(_connection, cancellationToken);

    public Task<IReadOnlyList<HealthFinding>> CheckAsync(CancellationToken cancellationToken) => PostgreSqlDba.HealthAsync(_connection, cancellationToken);

    public Task<DatabaseDetail> GetDatabaseDetailAsync(string database, CancellationToken cancellationToken) =>
        InDatabaseAsync(database, connection => PostgreSqlDba.DetailAsync(connection, cancellationToken), cancellationToken);

    // A database's own statistics are only visible from inside it: another database is read on a short-lived
    // connection, so the window's connection stays where it is.
    private async Task<T> InDatabaseAsync<T>(string? database, Func<NpgsqlConnection, Task<T>> work, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(database) || string.Equals(database, _connection.Database, StringComparison.Ordinal))
        {
            return await work(_connection).ConfigureAwait(false);
        }

        var settings = new Dictionary<string, string>(_profile.Settings, StringComparer.OrdinalIgnoreCase) { [PostgreSqlConnectionSettings.Database] = database };
        var dataSource = new NpgsqlDataSourceBuilder(PostgreSqlConnectionSettings.BuildConnectionString(_profile with { Settings = settings }, _credential)).Build();
        await using (dataSource.ConfigureAwait(false))
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                return await work(connection).ConfigureAwait(false);
            }
        }
    }

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
