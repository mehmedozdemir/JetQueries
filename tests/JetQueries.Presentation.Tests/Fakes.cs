using JetQueries.Core.Connections;
using JetQueries.Core.Extensions;
using System.Runtime.CompilerServices;
using JetQueries.Core.Jobs;
using JetQueries.Core.Metadata;
using JetQueries.Core.Queries;
using JetQueries.Presentation.Services;
using JetQueries.Presentation.ViewModels;
using JetQueries.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Presentation.Tests;

// Also compiled into the Avalonia and WPF test projects.

/// <summary>Real Core services around a scripted provider: everything the connection view models talk to.</summary>
internal sealed class TestEnvironment : IAsyncDisposable
{
    public TestEnvironment()
    {
        Manager = new ConnectionManager(new FakeExtensions(Provider), Credentials, Probe);
    }

    public FakeProvider Provider { get; } = new();

    public FakeProbe Probe { get; } = new();

    public FakeDialogs Dialogs { get; } = new();

    public JsonConnectionStore Store { get; } = new(new ConnectionStoreOptions(), NullLogger<JsonConnectionStore>.Instance);

    public SessionCredentialStore Credentials { get; } = new();

    public ConnectionManager Manager { get; }

    public JobQueue Jobs { get; } = new();

    public SqliteMetadataCache MetadataCache { get; } =
        new(new MetadataCacheOptions(), TimeProvider.System, NullLogger<SqliteMetadataCache>.Instance);

    public MetadataService Metadata => new(MetadataCache, TimeProvider.System);

    public ConnectionEditorViewModel Editor(ConnectionDefinition? existing = null) =>
        new(existing, [Provider], Store, Credentials, Manager, Jobs);

    public ImmediateDispatcher Ui { get; } = new();

    public SqliteQueryHistory History { get; } = new(new QueryHistoryOptions(), NullLogger<SqliteQueryHistory>.Instance);

    public DocumentsViewModel Documents() => new(Manager, Metadata, History, Jobs, Dialogs, Ui);

    public HistoryViewModel HistoryPanel(DocumentsViewModel documents) => new(History, Jobs, Dialogs, Ui, documents);

    public QueryDocumentViewModel Query() => new("Query 1", Manager, Metadata, History, Jobs, Dialogs, Ui);

    /// <summary>Opens a session for a saved connection, as connecting from the explorer would.</summary>
    public async Task<ConnectionDefinition> OpenAsync(ConnectionDefinition definition)
    {
        var attempt = await Manager.OpenAsync(definition, "x", CancellationToken.None);
        Xunit.Assert.Equal(ConnectionOutcome.Succeeded, attempt.Outcome);
        return definition;
    }

    public ConnectionsViewModel Connections() => new(Store, Manager, Credentials, Metadata, Jobs, Dialogs);

    /// <summary>Waits for a node's background load, which the tree starts by itself when a node is expanded.</summary>
    public static async Task LoadedAsync(LazyNodeViewModel node)
    {
        for (var i = 0; i < 500 && (node.IsLoading || node.Children is [MessageNodeViewModel { IsError: false, Name: "Loading…" or "Yükleniyor…" }]); i++)
        {
            await Task.Delay(10);
        }
    }

    public static async Task<(ConnectionsViewModel Connections, ConnectionNodeViewModel Node)> ConnectAsync(TestEnvironment env)
    {
        var definition = TestEnvironment.Definition("Cards");
        await env.Store.SaveAsync(definition, CancellationToken.None);
        await env.Credentials.SetPasswordAsync(definition.Id, "stored", CancellationToken.None);
        var connections = env.Connections();
        await connections.LoadAsync();
        var node = (ConnectionNodeViewModel)connections.Roots[0];
        connections.Selected = node;
        await connections.ToggleConnectionCommand.ExecuteAsync(null);
        await LoadedAsync(node);
        Assert.True(node.IsConnected, node.Error);
        return (connections, node);
    }

    public static async Task ExpandAsync(LazyNodeViewModel node)
    {
        node.IsExpanded = true;
        await LoadedAsync(node);
    }

    public static QueryHistoryEntry HistoryEntry(string sql) => new(
        0, DateTimeOffset.UtcNow, Guid.NewGuid(), "Cards", "fake", sql, TimeSpan.FromMilliseconds(42), QueryOutcome.Succeeded, 3);

    public static ConnectionDefinition Definition(string name, string folder = "", ConnectionEnvironment environment = ConnectionEnvironment.Unspecified) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = "fake",
        Settings = new Dictionary<string, string> { ["server"] = "sql01" },
        Folder = folder,
        Environment = environment,
        UserName = "app",
        SavePassword = true,
    };

    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync();
        await Jobs.DisposeAsync();
        await MetadataCache.DisposeAsync();
        await History.DisposeAsync();
        Store.Dispose();
    }
}

/// <summary>Runs posted work at once, on the calling thread: there is no UI thread in these tests.</summary>
internal sealed class ImmediateDispatcher : IUiDispatcher
{
    private readonly Lock _gate = new();

    public void Post(Action action)
    {
        // The real dispatcher runs one action at a time; keep that guarantee.
        lock (_gate)
        {
            action();
        }
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public bool EditResult { get; set; }

    public string? Password { get; set; }

    public bool Confirm { get; set; }

    public List<string> Asked { get; } = [];

    public Task<bool> EditConnectionAsync(ConnectionDefinition? existing)
    {
        Asked.Add("edit:" + (existing?.Name ?? "new"));
        return Task.FromResult(EditResult);
    }

    public Task<string?> PromptPasswordAsync(string connectionName)
    {
        Asked.Add("password:" + connectionName);
        return Task.FromResult(Password);
    }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        Asked.Add("confirm:" + message);
        return Task.FromResult(Confirm);
    }

    public string? OpenPath { get; set; }

    public string? SavePath { get; set; }

    public SaveChangesChoice SaveChoice { get; set; } = SaveChangesChoice.Cancel;

    public Task<string?> PickFileToOpenAsync()
    {
        Asked.Add("open");
        return Task.FromResult(OpenPath);
    }

    public Task<string?> PickFileToSaveAsync(string suggestedName, string? currentPath)
    {
        Asked.Add("save:" + suggestedName);
        return Task.FromResult(SavePath);
    }

    public Task<SaveChangesChoice> AskSaveChangesAsync(string documentName)
    {
        Asked.Add("save-changes:" + documentName);
        return Task.FromResult(SaveChoice);
    }

    public TransactionChoice TransactionChoice { get; set; } = TransactionChoice.Cancel;

    public Task<bool> ImportConnectionsAsync()
    {
        Asked.Add("import");
        return Task.FromResult(true);
    }

    public Task<TransactionChoice> AskOpenTransactionAsync(string documentNames)
    {
        Asked.Add("transaction:" + documentNames);
        return Task.FromResult(TransactionChoice);
    }
}

internal sealed class FakeProbe : IReachabilityProbe
{
    public string? Answer { get; set; }

    public Task<string?> ProbeAsync(NetworkEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(Answer);
}

internal sealed class FakeExtensions(IDatabaseProvider provider) : IExtensionHost
{
    public IReadOnlyList<ExtensionLoadResult> Extensions => [];

    public IReadOnlyCollection<IDatabaseProvider> DatabaseProviders => [provider];

    public Task<IReadOnlyList<ExtensionLoadResult>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Extensions);

    public IDatabaseProvider? FindDatabaseProvider(string id) => id == provider.Id ? provider : null;
}

/// <summary>A provider with SQL Server-like settings that connects to nothing.</summary>
internal sealed class FakeProvider : IDatabaseProvider, IConnectionSettingsProvider, IRowCountQueryBuilder, IResultFilterQueryBuilder
{
    public string? OpenError { get; set; }

    public (string? User, string? Password)? LastCredential { get; private set; }

    /// <summary>Every metadata request that reached the "database", for example <c>objects:Cards/dbo:Table</c>.</summary>
    public List<string> MetadataCalls { get; } = [];

    public string? MetadataError { get; set; }

    /// <summary>Every request that reached the "database".</summary>
    public List<QueryRequest> Executed { get; } = [];

    /// <summary>How many driver connections were opened.</summary>
    public int OpenCount => _openCount;

    private int _openCount;

    /// <summary>Whether connections can switch database (SQL Server-like) or not (SQLite-like).</summary>
    public bool SupportsDatabaseContext { get; set; } = true;

    /// <summary>What running SQL does; by default one result set with three rows and a message.</summary>
    public Func<QueryRequest, CancellationToken, IAsyncEnumerable<QueryEvent>> Execute { get; set; } = DefaultResult;

    public string Id => "fake";

    public string DisplayName => "SQL Server";

    public ProviderCapabilities Capabilities { get; set; } =
        ProviderCapabilities.MultipleDatabases | ProviderCapabilities.Schemas | ProviderCapabilities.Routines | ProviderCapabilities.Sequences |
        ProviderCapabilities.Synonyms | ProviderCapabilities.UserTypes;

    public ISqlLanguageService? LanguageService { get; set; }

    public IReadOnlyList<ConnectionSettingDescriptor> ConnectionSettings { get; } =
    [
        new("server", "Server", ConnectionSettingKind.Text) { IsRequired = true, Hint = "host, host,port or host\\instance" },
        new("database", "Database", ConnectionSettingKind.Text),
        new("authentication", "Authentication", ConnectionSettingKind.Choice)
        {
            DefaultValue = "sql",
            Options = [new("sql", "SQL Server login"), new("windows", "Windows (integrated)")],
        },
        new("trustServerCertificate", "Trust server certificate", ConnectionSettingKind.Boolean) { DefaultValue = "false", Hint = "needed for self-signed certificates" },
        new("connectTimeoutSeconds", "Connect timeout (s)", ConnectionSettingKind.Number) { DefaultValue = "15" },
    ];

    public string QuoteIdentifier(string identifier) => identifier;

    /// <summary>"COUNT(" + the query + ")" for a SELECT; null otherwise.</summary>
    public string? BuildCountQuery(string sql) => sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ? "COUNT(" + sql + ")" : null;

    public string? BuildFilterQuery(string sql, string condition) =>
        BuildCountQuery(sql) is null || condition.Contains(';', StringComparison.Ordinal) ? null : "SELECT * FILTER(" + sql + " WHERE " + condition + ")";

    /// <summary>How many result enumerations were disposed before they ended (open cursors closed early).</summary>
    public int ClosedEarly => _closedEarly;

    private int _closedEarly;

    /// <summary>
    /// Makes queries return <paramref name="rows"/> rows of one column (1, 2, …) in the request's batch size, and
    /// COUNT(…) return <paramref name="rows"/>.
    /// </summary>
    public void ReturnRows(int rows) => Execute = (request, token) => Rows(request, rows, token);

    private async IAsyncEnumerable<QueryEvent> Rows(QueryRequest request, int rows, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (request.Sql.StartsWith("COUNT(", StringComparison.Ordinal))
        {
            yield return new ResultSetStarted([new ResultColumn("count", "bigint", typeof(long))]);
            yield return new RowBatch([[(long)rows]]);
            yield return new ResultSetCompleted(1, false);
            yield break;
        }

        var completed = false;
        try
        {
            yield return new ResultSetStarted([new ResultColumn("n", "int", typeof(int))]);
            for (var i = 0; i < rows; i += request.BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new RowBatch([.. Enumerable.Range(i + 1, Math.Min(request.BatchSize, rows - i)).Select(static n => new object?[] { n })]);
            }

            yield return new ResultSetCompleted(rows, false);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                Interlocked.Increment(ref _closedEarly);
            }
        }
    }

    public bool RequiresCredential(IReadOnlyDictionary<string, string> settings) => settings.GetValueOrDefault("authentication", "sql") != "windows";

    /// <summary>Begin / commit / rollback calls, in order.</summary>
    public List<string> TransactionLog { get; } = [];

    /// <summary>Whether the fake server holds an open transaction; the error a commit fails with.</summary>
    public bool TransactionOpen { get; set; }

    public string? CommitError { get; set; }

    internal Task Transaction(string what, bool open)
    {
        TransactionLog.Add(what);
        if (what == "commit" && CommitError is not null)
        {
            return Task.FromException(new InvalidOperationException(CommitError));
        }

        TransactionOpen = open;
        return Task.CompletedTask;
    }

    public NetworkEndpoint? GetEndpoint(IReadOnlyDictionary<string, string> settings) => new("sql01", 1433);

    public Task<IDatabaseConnection> OpenConnectionAsync(ConnectionProfile profile, ConnectionCredential? credential, CancellationToken cancellationToken)
    {
        LastCredential = credential is null ? null : (credential.UserName, credential.Password);
        if (OpenError is not null)
        {
            return Task.FromException<IDatabaseConnection>(new InvalidOperationException(OpenError));
        }

        Interlocked.Increment(ref _openCount);
        return Task.FromResult<IDatabaseConnection>(SupportsDatabaseContext ? new FakeContextConnection(this) : new FakeConnection(this));
    }

    private sealed class FakeConnection(FakeProvider owner) : IDatabaseConnection, ITransactionControl
    {
        public Task BeginAsync(CancellationToken cancellationToken) => owner.Transaction("begin", open: true);

        public Task<bool> HasOpenTransactionAsync(CancellationToken cancellationToken) => Task.FromResult(owner.TransactionOpen);

        public Task CommitAsync(CancellationToken cancellationToken) => owner.Transaction("commit", open: false);

        public Task RollbackAsync(CancellationToken cancellationToken) => owner.Transaction("rollback", open: false);

        public IQueryExecutor Executor { get; } = new FakeExecutor(owner, null);

        public IMetadataProvider Metadata { get; } = new FakeMetadata(owner);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A connection with a current database that starts at "Cards" and follows USE.</summary>
    internal sealed class FakeContextConnection : IDatabaseConnection, IDatabaseContext, ITransactionControl
    {
        private readonly FakeProvider owner;

        public FakeContextConnection(FakeProvider owner)
        {
            this.owner = owner;

            Executor = new FakeExecutor(owner, this);
            Metadata = new FakeMetadata(owner);
        }

        public string Current { get; set; } = "Cards";

        public Task BeginAsync(CancellationToken cancellationToken) => owner.Transaction("begin", open: true);

        public Task<bool> HasOpenTransactionAsync(CancellationToken cancellationToken) => Task.FromResult(owner.TransactionOpen);

        public Task CommitAsync(CancellationToken cancellationToken) => owner.Transaction("commit", open: false);

        public Task RollbackAsync(CancellationToken cancellationToken) => owner.Transaction("rollback", open: false);

        public IQueryExecutor Executor { get; }

        public IMetadataProvider Metadata { get; }

        public DatabaseContextKind Kind => DatabaseContextKind.Database;

        public Task<string?> GetCurrentAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(Current);

        public Task<IReadOnlyList<string>> GetAvailableAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["Cards", "master"]);

        public Task ChangeAsync(string name, CancellationToken cancellationToken)
        {
            if (name is not ("Cards" or "master"))
            {
                throw new InvalidOperationException($"Database '{name}' does not exist.");
            }

            Current = name;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async IAsyncEnumerable<QueryEvent> DefaultResult(QueryRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new QueryMessage("starting", QueryMessageSeverity.Info);
        yield return new ResultSetStarted(
        [
            new ResultColumn("Id", "bigint", typeof(long)),
            new ResultColumn("CardNo", "varchar", typeof(string)),
            new ResultColumn("Balance", "decimal", typeof(decimal)),
            new ResultColumn("IssuedAt", "datetime2", typeof(DateTime)),
        ]);
        yield return new RowBatch(
        [
            [1L, "5200 0000 0000 0001", 1250.50m, new DateTime(2026, 1, 15)],
            [2L, "5200 0000 0000 0002", null, new DateTime(2026, 2, 3, 14, 30, 5, 120)],
        ]);
        yield return new RowBatch([[3L, "Müşteri İşlemleri", 0m, null]]);
        yield return new ResultSetCompleted(3, false);
    }

    private sealed class FakeExecutor(FakeProvider owner, FakeContextConnection? context) : IQueryExecutor
    {
        public IAsyncEnumerable<QueryEvent> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken)
        {
            owner.Executed.Add(request);
            if (context is not null && request.Sql.StartsWith("USE ", StringComparison.OrdinalIgnoreCase))
            {
                context.Current = request.Sql[4..].Trim().Trim(';', '[', ']');
            }

            return owner.Execute(request, cancellationToken);
        }
    }

    /// <summary>A small fixed catalog: databases Cards and master, schemas dbo and kart, a few objects in dbo.</summary>
    private sealed class FakeMetadata(FakeProvider owner) : IMetadataProvider
    {
        public async IAsyncEnumerable<DatabaseObject> GetObjectsAsync(
            DatabaseObject? parent, DatabaseObjectKinds kinds, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            owner.MetadataCalls.Add($"objects:{parent?.Name.ToString().Replace('.', '/') ?? "(root)"}:{kinds}");
            if (owner.MetadataError is not null)
            {
                throw new InvalidOperationException(owner.MetadataError);
            }

            if (parent is null && kinds == DatabaseObjectKinds.Database)
            {
                yield return new(new(null, null, "Cards"), DatabaseObjectKinds.Database);
                yield return new(new(null, null, "master"), DatabaseObjectKinds.Database);
            }
            else if (kinds == DatabaseObjectKinds.Schema)
            {
                yield return new(new(parent?.Name.Name, null, "dbo"), DatabaseObjectKinds.Schema);
                yield return new(new(parent?.Name.Name, null, "kart"), DatabaseObjectKinds.Schema);
            }
            else if (parent?.Name.Name is "dbo" or null)
            {
                var names = kinds switch
                {
                    DatabaseObjectKinds.Table => new[] { "Cards", "Müşteri İşlemleri", "Transactions" },
                    DatabaseObjectKinds.View => ["CardSummary"],
                    DatabaseObjectKinds.Procedure => ["PurgeOldLogs"],
                    _ => [],
                };
                foreach (var name in names)
                {
                    yield return new(new(parent?.Name.Database, parent?.Name.Name, name), kinds);
                }
            }
        }

        public Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DatabaseObjectName tableOrView, CancellationToken cancellationToken)
        {
            owner.MetadataCalls.Add($"columns:{tableOrView.Name}");
            return Task.FromResult<IReadOnlyList<ColumnInfo>>(
            [
                new("Id", "bigint", false, 1, true),
                new("CardNo", "varchar(19)", false, 2),
                new("Balance", "decimal(12,2)", true, 3),
            ]);
        }

        public Task<string?> GetDefinitionAsync(DatabaseObject target, CancellationToken cancellationToken) =>
            Task.FromResult(target.Kind == DatabaseObjectKinds.View ? $"CREATE VIEW {target.Name} AS SELECT 1 AS n;" : null);
    }
}
