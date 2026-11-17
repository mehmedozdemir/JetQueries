using JetQueries.Provider.SqlServer.Language;
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
public sealed class SqlServerProvider : IDatabaseProvider, IConnectionSettingsProvider, IScriptSplitter
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
        ProviderCapabilities.Sequences;

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

internal sealed class SqlServerConnection(SqlConnection connection) : IDatabaseConnection
{
    public IQueryExecutor Executor { get; } = new SqlServerQueryExecutor(connection);

    public IMetadataProvider Metadata { get; } = new SqlServerMetadataProvider(connection);

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
