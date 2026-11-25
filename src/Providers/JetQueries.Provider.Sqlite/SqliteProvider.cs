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
public sealed class SqliteProvider : IDatabaseProvider, IBulkInsertScripting, IRowEditScripting, IQueryParameterSyntax, IConnectionSettingsProvider, IScriptSplitter, IRowCountQueryBuilder, IResultFilterQueryBuilder, IRowLimitedSelect, IConnectionImport,
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

    /// <summary>The product's logo: Simple Icons 9.0.0 (CC0-1.0), for recognition only; the trademark is its owner's.</summary>
    public string IconPathData => "M21.678.521c-1.032-.92-2.28-.55-3.513.544a8.71 8.71 0 0 0-.547.535c-2.109 2.237-4.066 6.38-4.674 9.544.237.48.422 1.093.544 1.561a13.044 13.044 0 0 1 .164.703s-.019-.071-.096-.296l-.05-.146a1.689 1.689 0 0 0-.033-.08c-.138-.32-.518-.995-.686-1.289-.143.423-.27.818-.376 1.176.484.884.778 2.4.778 2.4s-.025-.099-.147-.442c-.107-.303-.644-1.244-.772-1.464-.217.804-.304 1.346-.226 1.478.152.256.296.698.422 1.186.286 1.1.485 2.44.485 2.44l.017.224a22.41 22.41 0 0 0 .056 2.748c.095 1.146.273 2.13.5 2.657l.155-.084c-.334-1.038-.47-2.399-.41-3.967.09-2.398.642-5.29 1.661-8.304 1.723-4.55 4.113-8.201 6.3-9.945-1.993 1.8-4.692 7.63-5.5 9.788-.904 2.416-1.545 4.684-1.931 6.857.666-2.037 2.821-2.912 2.821-2.912s1.057-1.304 2.292-3.166c-.74.169-1.955.458-2.362.629-.6.251-.762.337-.762.337s1.945-1.184 3.613-1.72C21.695 7.9 24.195 2.767 21.678.521m-18.573.543A1.842 1.842 0 0 0 1.27 2.9v16.608a1.84 1.84 0 0 0 1.835 1.834h9.418a22.953 22.953 0 0 1-.052-2.707c-.006-.062-.011-.141-.016-.2a27.01 27.01 0 0 0-.473-2.378c-.121-.47-.275-.898-.369-1.057-.116-.197-.098-.31-.097-.432 0-.12.015-.245.037-.386a9.98 9.98 0 0 1 .234-1.045l.217-.028c-.017-.035-.014-.065-.031-.097l-.041-.381a32.8 32.8 0 0 1 .382-1.194l.2-.019c-.008-.016-.01-.038-.018-.053l-.043-.316c.63-3.28 2.587-7.443 4.8-9.791.066-.069.133-.128.198-.194Z";

    /// <inheritdoc />
    public string AccentColor => "#003B57";

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
    public string ParameterReference(string name) => "@" + name;

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
