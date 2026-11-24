using JetQueries.Provider.Sqlite.Language;
using JetQueries.Providers.Language;
using JetQueries.Providers.Metadata;
using JetQueries.Sdk;
using Microsoft.Data.Sqlite;

namespace JetQueries.Provider.Sqlite;

/// <summary>Entry point of the SQLite extension.</summary>
public sealed class SqliteExtension : IExtension
{
    /// <inheritdoc />
    public void Initialize(IExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterDatabaseProvider(new SqliteProvider());
    }
}

/// <summary>Database provider for SQLite database files, on Microsoft.Data.Sqlite.</summary>
public sealed class SqliteProvider : IDatabaseProvider, IBulkInsertScripting, IRowEditScripting, IConnectionSettingsProvider, IScriptSplitter, IRowCountQueryBuilder, IResultFilterQueryBuilder, IRowLimitedSelect, IConnectionImport,
    IConnectionStringImport, IProviderAppearance, IObjectScripting
{
    /// <summary>The provider identifier stored in connection profiles.</summary>
    public const string ProviderId = "sqlite";

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public string DisplayName => "SQLite";

    /// <inheritdoc />
    public ProviderCapabilities Capabilities => ProviderCapabilities.MultipleResultSets | ProviderCapabilities.Transactions | ProviderCapabilities.ServerSideCancel;

    /// <inheritdoc />
    public string IconPathData => "M6.5 2H14l5 5v13.5a1.5 1.5 0 0 1-1.5 1.5h-11A1.5 1.5 0 0 1 5 20.5v-17A1.5 1.5 0 0 1 6.5 2z M13.5 3.5V8H18z M8 12h8v1.5H8z M8 15.5h8V17H8z";

    /// <inheritdoc />
    public string AccentColor => "#0E8A8A";

    /// <inheritdoc />
    public string Description => "A local database file";

    /// <inheritdoc />
    public ParsedConnectionString ParseConnectionString(string connectionString) => SqliteConnectionStringParser.Parse(connectionString);

    /// <inheritdoc />
    public ISqlLanguageService? LanguageService { get; } = new GenericLanguageService(SqliteDialect.Instance);

    /// <inheritdoc />
    public IReadOnlyList<ConnectionSettingDescriptor> ConnectionSettings { get; } =
    [
        new(SqliteConnectionSettings.File, "Database file", ConnectionSettingKind.FilePath) { IsRequired = true, Hint = "a .db, .sqlite or .sqlite3 file" },
        new(SqliteConnectionSettings.Mode, "Open", ConnectionSettingKind.Choice)
        {
            DefaultValue = "readWrite",
            Options = [new("readWrite", "Read and write"), new("readOnly", "Read only"), new("create", "Create the file if it does not exist")],
        },
        new(SqliteConnectionSettings.ForeignKeys, "Enforce foreign keys", ConnectionSettingKind.Boolean) { DefaultValue = "true" },
    ];

    /// <inheritdoc />
    public bool RequiresCredential(IReadOnlyDictionary<string, string> settings) => false;

    /// <inheritdoc />
    public NetworkEndpoint? GetEndpoint(IReadOnlyDictionary<string, string> settings) => null;

    /// <inheritdoc />
    public IReadOnlyList<ScriptBatch> SplitBatches(string script) =>
        SqlStatementSplitter.Split(script, SqliteSyntax.Tokenizer.Tokenize(script), StatementBlockRule.TriggerBody);

    /// <inheritdoc />
    public string? BuildCountQuery(string sql) =>
        CountQueryBuilder.Build(sql, SqliteSyntax.Tokenizer.Tokenize(sql), withAllowedInside: true, orderByNeedsPaging: false);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> ToSettings(ImportedEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if ((endpoint.File ?? endpoint.Database) is { Length: > 0 } file)
        {
            settings[SqliteConnectionSettings.File] = file;
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
        return CountQueryBuilder.BuildFilter(sql, SqliteSyntax.Tokenizer.Tokenize(sql), condition, SqliteSyntax.Tokenizer.Tokenize(condition), withAllowedInside: true, orderByNeedsPaging: false);
    }

    /// <inheritdoc />
    public string QuoteIdentifier(string identifier) => SqliteNames.Quote(identifier);

    /// <inheritdoc />
    public int RowsPerStatement(int columnCount) => Providers.Metadata.BulkInsert.Rows(columnCount, 30000, 500);

    /// <inheritdoc />
    public string ScriptUpdate(DatabaseObjectName table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Providers.Metadata.RowEdit.Update(QuoteIdentifier, '@', RowEditTable(table), columns, keyColumns);
    }

    /// <inheritdoc />
    public string ScriptDelete(DatabaseObjectName table, IReadOnlyList<string> keyColumns)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Providers.Metadata.RowEdit.Delete(QuoteIdentifier, '@', RowEditTable(table), keyColumns);
    }

    private string RowEditTable(DatabaseObjectName table) => (table.Schema is { Length: > 0 } schema && schema != "main" ? QuoteIdentifier(schema) + "." : string.Empty) + QuoteIdentifier(table.Name);

    /// <inheritdoc />
    public string ScriptInsert(DatabaseObjectName table, IReadOnlyList<string> columns, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(table);
        var name = (table.Schema is { Length: > 0 } schema && schema != "main" ? QuoteIdentifier(schema) + "." : string.Empty) + QuoteIdentifier(table.Name);
        return Providers.Metadata.BulkInsert.Values(QuoteIdentifier, '@', name, columns, rowCount);
    }


    /// <inheritdoc />
    public string? ScriptDrop(DatabaseObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind is not (DatabaseObjectKinds.Table or DatabaseObjectKinds.View or DatabaseObjectKinds.Trigger) || DdlText.DropKeyword(target.Kind) is not { } keyword)
        {
            return null;
        }

        // main is what an unqualified name means; temp and attached databases are named.
        var name = target.Name.Schema is { Length: > 0 } schema && !schema.Equals("main", StringComparison.OrdinalIgnoreCase)
            ? SqliteNames.Quote(schema) + "." + SqliteNames.Quote(target.Name.Name)
            : SqliteNames.Quote(target.Name.Name);
        return $"DROP {keyword} {name};";
    }

    /// <summary>SQLite cannot change a view or trigger in place: always null.</summary>
    /// <param name="target">The object.</param>
    /// <param name="definition">Its definition.</param>
    public string? ScriptAlter(DatabaseObject target, string definition) => null;

    /// <inheritdoc />
    public async Task<IDatabaseConnection> OpenConnectionAsync(
        ConnectionProfile profile,
        ConnectionCredential? credential,
        CancellationToken cancellationToken)
    {
        var connectionString = SqliteConnectionSettings.BuildConnectionString(profile, out var path, out var mustExist);
        if (mustExist && !File.Exists(path))
        {
            throw new FileNotFoundException($"The database file '{path}' does not exist.", path);
        }

        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new SqliteDatabaseConnection(connection);
    }
}

internal sealed class SqliteDatabaseConnection(SqliteConnection connection) : IDatabaseConnection, ITransactionControl
{
    /// <summary>SQLite says itself whether it is outside a transaction (its autocommit mode).</summary>
    private bool InTransaction => SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle) == 0;

    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        if (!InTransaction)
        {
            await ScalarAsync("BEGIN", 30, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<bool> HasOpenTransactionAsync(CancellationToken cancellationToken) => Task.FromResult(InTransaction);

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (InTransaction)
        {
            await ScalarAsync("COMMIT", 0, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (InTransaction)
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

    public IQueryExecutor Executor { get; } = new SqliteQueryExecutor(connection);

    public IMetadataProvider Metadata { get; } = new SqliteMetadataProvider(connection);

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

internal static class SqliteNames
{
    /// <summary>Double-quotes one identifier part, doubling any double quote inside it.</summary>
    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        if (identifier.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("An SQLite identifier cannot contain a NUL character.", nameof(identifier));
        }

        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

/// <summary>
/// Turns a connection profile into a Microsoft.Data.Sqlite connection string. Settings: <c>file</c> (required;
/// environment variables are expanded), <c>mode</c> (<c>readWrite</c>, <c>readOnly</c>, <c>create</c>),
/// <c>foreignKeys</c> (default true).
/// </summary>
internal static class SqliteConnectionSettings
{
    public const string File = "file";
    public const string Mode = "mode";
    public const string ForeignKeys = "foreignKeys";

    /// <exception cref="ArgumentException">A setting is missing or has a value that is not understood.</exception>
    public static string BuildConnectionString(ConnectionProfile profile, out string path, out bool mustExist)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var settings = profile.Settings;
        if (!settings.TryGetValue(File, out var file) || string.IsNullOrWhiteSpace(file))
        {
            throw new ArgumentException("The connection has no database file.", nameof(profile));
        }

        file = file.Trim().Trim('"');
        if (file.Equals(":memory:", StringComparison.OrdinalIgnoreCase) || file.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            // Each connection of a session (queries, metadata) would get its own empty database.
            throw new ArgumentException("Give the path of a database file; in-memory databases and URIs are not supported.", nameof(profile));
        }

        path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(file));
        var mode = settings.GetValueOrDefault(Mode, "readWrite").Trim().ToUpperInvariant() switch
        {
            "" or "READWRITE" => SqliteOpenMode.ReadWrite,
            "READONLY" => SqliteOpenMode.ReadOnly,
            "CREATE" => SqliteOpenMode.ReadWriteCreate,
            _ => throw new ArgumentException($"Unknown mode '{settings[Mode]}'. Use 'readWrite', 'readOnly' or 'create'.", nameof(profile)),
        };
        mustExist = mode != SqliteOpenMode.ReadWriteCreate;

        var foreignKeys = settings.GetValueOrDefault(ForeignKeys, "true").Trim();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            ForeignKeys = !foreignKeys.Equals("false", StringComparison.OrdinalIgnoreCase),

            // A pooled connection keeps the file open after a disconnect, so it could not be moved or deleted.
            Pooling = false,

            // Seconds to wait for a lock held by another connection (another program, or this session's
            // other connection inside an open transaction) before reporting "database is locked".
            DefaultTimeout = 10,
        };
        return builder.ConnectionString;
    }
}
