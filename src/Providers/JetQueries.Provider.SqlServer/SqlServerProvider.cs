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
public sealed class SqlServerProvider : IDatabaseProvider
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
    public ISqlLanguageService? LanguageService => null;

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
