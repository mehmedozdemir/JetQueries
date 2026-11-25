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
    /// <summary>What the Export dialog answers (it may change the options first); null declines.</summary>
    public Func<ExportResultViewModel, bool>? Export { get; set; }

    public List<string> Opened { get; } = [];

    public Task<bool> ExportResultAsync(ExportResultViewModel options) => Task.FromResult(Export?.Invoke(options) ?? false);

    /// <summary>What the parameters dialog answers (it fills the values first); null cancels.</summary>
    public Func<QueryParametersViewModel, bool>? Parameters { get; set; }

    public Task<bool> AskQueryParametersAsync(QueryParametersViewModel parameters) => Task.FromResult(Parameters?.Invoke(parameters) ?? false);

    /// <summary>What the Save to library dialog answers (it may change the fields first); null cancels.</summary>
    public Func<SavedQueryEditViewModel, bool>? SaveQuery { get; set; }

    public Task<bool> EditSavedQueryAsync(SavedQueryEditViewModel query) => Task.FromResult(SaveQuery?.Invoke(query) ?? false);

    public void OpenWithShell(string path) => Opened.Add(path);

    public string DocumentsFolder => System.IO.Path.Combine(AppContext.BaseDirectory, "exports");

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

    /// <summary>Answers to text prompts, in order (an empty queue answers Cancel).</summary>
    public Queue<string?> TextAnswers { get; set; } = new();

    public Task<string?> PromptTextAsync(string title, string message, string initial)
    {
        Asked.Add("text:" + title);
        return Task.FromResult(TextAnswers.Count > 0 ? TextAnswers.Dequeue() : null);
    }

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
internal sealed class FakeProvider : IDatabaseProvider, IConnectionSettingsProvider, IRoutineExecutionScripting, IRowCountQueryBuilder, IResultFilterQueryBuilder, IQueryParameterSyntax
{
    public string ParameterReference(string name) => "@" + name;

    public string? OpenError { get; set; }

    public (string? User, string? Password)? LastCredential { get; private set; }

    /// <summary>Every metadata request that reached the "database", for example <c>objects:Cards/dbo:Table</c>.</summary>
    public List<string> MetadataCalls { get; } = [];

    public string? MetadataError { get; set; }

    /// <summary>What the details view reads; null: the catalog has no such object.</summary>
    public Func<DatabaseObject, ObjectDetails?> Details { get; set; } = static _ => null;

    /// <summary>When set, the details are read in steps through this (a progressive provider).</summary>
    public Func<DatabaseObject, IProgress<ObjectDetails>, CancellationToken, Task<ObjectDetails?>>? ProgressiveDetails { get; set; }

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

    /// <summary>The signatures routines have (none by default).</summary>
    public Func<DatabaseObject, IReadOnlyList<SqlSignature>> Signatures { get; set; } = static _ => [];

    /// <summary>"CALL name(p=kind:text, …)": enough to see what the dialog passed.</summary>
    public string? ScriptExecute(DatabaseObject routine, SqlSignature signature, IReadOnlyList<RoutineArgument> arguments) =>
        "CALL " + routine.Name.Name + "(" + string.Join(", ", signature.Parameters.Select(p =>
            arguments.FirstOrDefault(a => a.Name == p.Name) is { } a ? $"{p.Name}={a.Kind}:{a.Text}" : $"{p.Name}=?")) + ")";

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

    /// <summary>What the performance analysis reads; the requests it was asked.</summary>
    public HeavyQueryReport Heavy { get; set; } = FakePerformance.Heavy();

    public IReadOnlyList<MissingIndex> Missing { get; set; } = FakePerformance.Missing();

    public IndexUsageReport Usage { get; set; } = FakePerformance.Usage();

    public IReadOnlyList<IndexFragmentation> Fragmentation { get; set; } = FakePerformance.Fragmentation();

    public List<string> PerformanceLog { get; } = [];

    /// <summary>What the dashboard reads: the overview, and samples whose totals grow with each read.</summary>
    public ServerOverview Overview { get; set; } = FakeDashboard.Overview();

    public int SampleCount { get; private set; }

    internal ServerSample NextSample() => FakeDashboard.Sample(SampleCount++);

    /// <summary>What the activity monitor reads; the sessions it was asked to end.</summary>
    public ActivitySnapshot? Activity { get; set; }

    public List<string> Killed { get; } = [];

    internal Task<ActivitySnapshot> ReadActivity() => Task.FromResult(Activity ?? new ActivitySnapshot(DateTimeOffset.Now, [], null));

    internal Task Kill(string id)
    {
        Killed.Add(id);
        if (Activity is { } activity)
        {
            Activity = activity with { Sessions = [.. activity.Sessions.Where(s => s.Id != id).Select(s => s.BlockedBy == id ? s with { BlockedBy = null, WaitType = null } : s)] };
        }

        return Task.CompletedTask;
    }

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

    private sealed class FakeConnection(FakeProvider owner) : IDatabaseConnection, ITransactionControl, IActivityMonitor, IPerformanceAdvisor, IServerDashboard, IHealthCheck, IDatabaseDetails
    {
        public Task<DatabaseDetail> GetDatabaseDetailAsync(string database, CancellationToken cancellationToken) => Task.FromResult(FakeDatabaseDetail.Of(database));

        public Task<IReadOnlyList<HealthFinding>> CheckAsync(CancellationToken cancellationToken) => FakeHealth.CheckAsync(cancellationToken);

        public Task<ServerOverview> GetOverviewAsync(CancellationToken cancellationToken) => Task.FromResult(OwnerOf().Overview);

        public Task<ServerSample> GetSampleAsync(CancellationToken cancellationToken) => Task.FromResult(OwnerOf().NextSample());

        public Task<HeavyQueryReport> GetHeavyQueriesAsync(HeavyQueryRequest request, CancellationToken cancellationToken)
        {
            OwnerOf().PerformanceLog.Add($"heavy:{request.Order}:{request.Top}:{request.Database}");
            return Task.FromResult(OwnerOf().Heavy);
        }

        public Task<IReadOnlyList<MissingIndex>> GetMissingIndexesAsync(string? database, CancellationToken cancellationToken)
        {
            OwnerOf().PerformanceLog.Add("missing:" + database);
            return Task.FromResult(OwnerOf().Missing);
        }

        public Task<IndexUsageReport> GetIndexUsageAsync(string database, CancellationToken cancellationToken)
        {
            OwnerOf().PerformanceLog.Add("usage:" + database);
            return Task.FromResult(OwnerOf().Usage);
        }

        public Task<IReadOnlyList<IndexFragmentation>> GetFragmentationAsync(string database, long minimumPages, CancellationToken cancellationToken)
        {
            OwnerOf().PerformanceLog.Add($"fragmentation:{database}:{minimumPages}");
            return Task.FromResult(OwnerOf().Fragmentation);
        }

        public Task<ActivitySnapshot> GetActivityAsync(CancellationToken cancellationToken) => OwnerOf().ReadActivity();

        public Task KillSessionAsync(string sessionId, CancellationToken cancellationToken) => OwnerOf().Kill(sessionId);

        public Task BeginAsync(CancellationToken cancellationToken) => owner.Transaction("begin", open: true);

        public Task<bool> HasOpenTransactionAsync(CancellationToken cancellationToken) => Task.FromResult(owner.TransactionOpen);

        public Task CommitAsync(CancellationToken cancellationToken) => owner.Transaction("commit", open: false);

        public Task RollbackAsync(CancellationToken cancellationToken) => owner.Transaction("rollback", open: false);

        private FakeProvider OwnerOf() => owner;

        public IQueryExecutor Executor { get; } = new FakeExecutor(owner, null);

        public IMetadataProvider Metadata { get; } = new FakeMetadata(owner);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A connection with a current database that starts at "Cards" and follows USE.</summary>
    internal sealed class FakeContextConnection : IDatabaseConnection, IDatabaseContext, ITransactionControl, IActivityMonitor, IPerformanceAdvisor, IServerDashboard, IHealthCheck, IDatabaseDetails
    {
        public Task<DatabaseDetail> GetDatabaseDetailAsync(string database, CancellationToken cancellationToken) => Task.FromResult(FakeDatabaseDetail.Of(database));

        public Task<IReadOnlyList<HealthFinding>> CheckAsync(CancellationToken cancellationToken) => FakeHealth.CheckAsync(cancellationToken);

        public Task<ServerOverview> GetOverviewAsync(CancellationToken cancellationToken) => Task.FromResult(OwnerOf().Overview);

        public Task<ServerSample> GetSampleAsync(CancellationToken cancellationToken) => Task.FromResult(OwnerOf().NextSample());

        public Task<HeavyQueryReport> GetHeavyQueriesAsync(HeavyQueryRequest request, CancellationToken cancellationToken)
        {
            OwnerOf().PerformanceLog.Add($"heavy:{request.Order}:{request.Top}:{request.Database}");
            return Task.FromResult(OwnerOf().Heavy);
        }

        public Task<IReadOnlyList<MissingIndex>> GetMissingIndexesAsync(string? database, CancellationToken cancellationToken)
        {
            OwnerOf().PerformanceLog.Add("missing:" + database);
            return Task.FromResult(OwnerOf().Missing);
        }

        public Task<IndexUsageReport> GetIndexUsageAsync(string database, CancellationToken cancellationToken)
        {
            OwnerOf().PerformanceLog.Add("usage:" + database);
            return Task.FromResult(OwnerOf().Usage);
        }

        public Task<IReadOnlyList<IndexFragmentation>> GetFragmentationAsync(string database, long minimumPages, CancellationToken cancellationToken)
        {
            OwnerOf().PerformanceLog.Add($"fragmentation:{database}:{minimumPages}");
            return Task.FromResult(OwnerOf().Fragmentation);
        }

        public Task<ActivitySnapshot> GetActivityAsync(CancellationToken cancellationToken) => OwnerOf().ReadActivity();

        public Task KillSessionAsync(string sessionId, CancellationToken cancellationToken) => OwnerOf().Kill(sessionId);

        private readonly FakeProvider owner;

        public FakeContextConnection(FakeProvider owner)
        {
            this.owner = owner;

            Executor = new FakeExecutor(owner, this);
            Metadata = new FakeMetadata(owner);
        }

        private FakeProvider OwnerOf() => owner;

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
    private sealed class FakeMetadata(FakeProvider owner) : IMetadataProvider, IProgressiveObjectDetailsProvider, IRoutineMetadataProvider
    {
        public Task<IReadOnlyList<SqlSignature>> GetSignaturesAsync(DatabaseObject routine, CancellationToken cancellationToken) =>
            Task.FromResult(owner.Signatures(routine));

        public Task<ObjectDetails?> GetDetailsAsync(DatabaseObject target, CancellationToken cancellationToken)
        {
            owner.MetadataCalls.Add($"details:{target.Name}");
            return Task.FromResult(owner.Details(target));
        }

        public Task<ObjectDetails?> GetDetailsAsync(DatabaseObject target, IProgress<ObjectDetails> progress, CancellationToken cancellationToken) =>
            owner.ProgressiveDetails is { } progressive ? progressive(target, progress, cancellationToken) : GetDetailsAsync(target, cancellationToken);

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

/// <summary>A server's sessions for the activity monitor's tests: a blocking chain 52 → 53 → 54, a running query, idle and system sessions.</summary>
public static class FakeActivity
{
    public static ActivitySnapshot Snapshot() => new(DateTimeOffset.Now,
    [
        new("5") { Login = "sa", Status = "background", Command = "LAZY WRITER", IsUserSession = false },
        new("51") { Login = "app", Host = "PC-1", Program = "JetQueries", Status = "running", Command = "SELECT", Sql = "SELECT * FROM sys.dm_exec_sessions" },
        new("52") { Login = "ali", Host = "PC-2", Program = "SSMS", Database = "Cards", Status = "sleeping", OpenTransactions = 1, Sql = "UPDATE dbo.Cards SET Balance = 0 WHERE Id = 1" },
        new("53") { Login = "ayse", Host = "PC-3", Database = "Cards", Status = "suspended", Command = "SELECT", WaitType = "LCK_M_S", WaitTime = TimeSpan.FromSeconds(12.4), BlockedBy = "52", Sql = "SELECT * FROM dbo.Cards" },
        new("54") { Login = "batch", Host = "SRV", Database = "Cards", Status = "suspended", Command = "UPDATE", WaitType = "LCK_M_X", WaitTime = TimeSpan.FromSeconds(3), BlockedBy = "53", Sql = "UPDATE dbo.Cards SET City = N'İzmir'" },
        new("55") { Login = "report", Host = "PC-4", Database = "Sales", Status = "running", Command = "SELECT", Elapsed = TimeSpan.FromSeconds(75), CpuTime = TimeSpan.FromMilliseconds(850), Reads = 12345, Sql = "SELECT SUM(Total) FROM dbo.Orders" },
        new("56") { Login = "idle", Host = "PC-5", Status = "sleeping" },
    ], OwnSessionId: "51");
}

/// <summary>A server's query and index statistics for the performance analysis' tests.</summary>
public static class FakePerformance
{
    public static HeavyQueryReport Heavy() => new(
    [
        new("SELECT * FROM dbo.Orders WHERE CustomerId = @1") { Database = "Cards", Executions = 1200, TotalCpu = TimeSpan.FromSeconds(42), TotalDuration = TimeSpan.FromSeconds(60), TotalReads = 2_400_000, TotalWrites = 0 },
        new("EXEC dbo.Nightly") { Database = "Cards", ObjectName = "dbo.Nightly", Executions = 1, TotalCpu = TimeSpan.FromSeconds(30), TotalDuration = TimeSpan.FromMinutes(3), TotalReads = 9_000_000, TotalWrites = 50_000 },
    ], new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero));

    public static IReadOnlyList<MissingIndex> Missing() =>
    [
        new("Cards", "dbo.Orders", "CREATE NONCLUSTERED INDEX [IX_Orders_Status] ON [Cards].[dbo].[Orders] ([Status]) INCLUDE ([Total]);")
        {
            EqualityColumns = ["Status"], IncludedColumns = ["Total"], Uses = 340, ImpactPercent = 87.5, Score = 1234.5,
        },
    ];

    public static IndexUsageReport Usage() => new(
    [
        new("dbo.Orders", "PK_Orders") { IsClustered = true, IsUnique = true, IsConstraint = true, KeyColumns = ["Id"], Seeks = 5000 },
        new("dbo.Orders", "IX_Orders_Customer") { KeyColumns = ["CustomerId"], Seeks = 10, Updates = 900, DropScript = "DROP INDEX [IX_Orders_Customer] ON [Cards].[dbo].[Orders];" },
        new("dbo.Orders", "IX_Orders_Customer_Date") { KeyColumns = ["CustomerId", "OrderDate"], IncludedColumns = ["Total"], Seeks = 800, Updates = 900, DropScript = "DROP INDEX [IX_Orders_Customer_Date] ON [Cards].[dbo].[Orders];" },
        new("dbo.Orders", "IX_Orders_Status") { KeyColumns = ["Status"], Updates = 900, SizeKb = 2048, DropScript = "DROP INDEX [IX_Orders_Status] ON [Cards].[dbo].[Orders];" },
        new("dbo.Orders", "IX_Orders_Date") { KeyColumns = ["OrderDate"], Scans = 3, DropScript = "DROP INDEX [IX_Orders_Date] ON [Cards].[dbo].[Orders];" },
        new("dbo.Orders", "IX_Orders_Date2") { KeyColumns = ["OrderDate"], Seeks = 1, DropScript = "DROP INDEX [IX_Orders_Date2] ON [Cards].[dbo].[Orders];" },
    ], new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero));

    public static IReadOnlyList<IndexFragmentation> Fragmentation() =>
    [
        new("dbo.Orders", "IX_Orders_Customer_Date") { FragmentationPercent = 62.5, Pages = 12000, Rebuild = true, MaintenanceScript = "ALTER INDEX [IX_Orders_Customer_Date] ON [Cards].[dbo].[Orders] REBUILD;" },
        new("dbo.Orders", "PK_Orders") { FragmentationPercent = 12.0, Pages = 40000, MaintenanceScript = "ALTER INDEX [PK_Orders] ON [Cards].[dbo].[Orders] REORGANIZE;" },
    ];
}

/// <summary>A server's overview and load for the dashboard's tests: samples 5 s apart, totals that grow.</summary>
public static class FakeDashboard
{
    public static readonly DateTimeOffset Start = new(2026, 11, 25, 9, 0, 0, TimeSpan.FromHours(3));

    public static ServerOverview Overview() => new("SQL Server 2022", "16.0.4135.4 RTM")
    {
        Edition = "Developer Edition (64-bit)",
        Host = "SQL01",
        StartedAt = Start - TimeSpan.FromDays(3) - TimeSpan.FromHours(4),
        CpuCount = 8,
        PhysicalMemoryMb = 32768,
        CpuHistory = [.. Enumerable.Range(0, 30).Select(static i => new CpuPoint(Start - TimeSpan.FromMinutes(30 - i), 20 + (i % 7) * 4, 5 + (i % 3)))],
        Databases =
        [
            new("Cards") { State = "ONLINE", RecoveryModel = "FULL", DataMb = 2048, LogMb = 512, BackupsKnown = true, LastFullBackup = Start - TimeSpan.FromHours(10), LastLogBackup = Start - TimeSpan.FromMinutes(15) },
            new("Sales") { State = "ONLINE", RecoveryModel = "FULL", DataMb = 51200, LogMb = 4096, BackupsKnown = true, LastFullBackup = Start - TimeSpan.FromDays(1) },
            new("Archive") { State = "ONLINE", RecoveryModel = "SIMPLE", DataMb = 120, LogMb = 8, BackupsKnown = true },
            new("master") { State = "ONLINE", RecoveryModel = "SIMPLE", DataMb = 6, LogMb = 2, BackupsKnown = true, LastFullBackup = Start - TimeSpan.FromDays(10) },
            new("tempdb") { State = "ONLINE", RecoveryModel = "SIMPLE", DataMb = 64, LogMb = 8, BackupsKnown = true },
        ],
    };

    public static ServerSample Sample(int n) => new(Start + TimeSpan.FromSeconds(5 * n))
    {
        Cpu = new CpuPoint(Start + TimeSpan.FromMinutes(n / 12), 30 + n % 5, 6),
        MemoryUsedMb = 12_000,
        MemoryTargetMb = 14_000,
        AvailableMemoryMb = 3_200,
        PageLifeExpectancy = 4_200,
        Connections = 142,
        ActiveRequests = 7,
        BlockedRequests = n % 4 == 3 ? 2 : 0,
        BatchRequests = 1_000_000 + n * 5_000 + 600 * (n % 3),
        Compilations = 10_000 + n * 50,
        Transactions = 500_000 + n * 2_000,
        BytesRead = 10_000_000_000 + n * 50L * 1024 * 1024,
        BytesWritten = 2_000_000_000 + n * 10L * 1024 * 1024,
        IoOperations = 1_000_000 + n * 1_000,
        IoStallMs = 3_000_000 + n * 3_500,
        Waits =
        [
            new("PAGEIOLATCH_SH", 9_000_000 + n * 4_000),
            new("LCK_M_X", 2_000_000 + n * 6_000),
            new("CXPACKET", 8_000_000 + n * 1_000),
            new("WRITELOG", 1_000_000 + n * 500),
        ],
    };
}

/// <summary>Findings of every severity and category for the health check's tests.</summary>
public static class FakeHealth
{
    /// <summary>When set, a check waits until cancelled (a slow server).</summary>
    public static bool Slow { get; set; }

    public static async Task<IReadOnlyList<HealthFinding>> CheckAsync(CancellationToken cancellationToken)
    {
        if (Slow)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return Findings();
    }

    public static IReadOnlyList<HealthFinding> Findings() =>
    [
        new("CostThreshold", HealthCategory.Performance, HealthSeverity.Warning, "Cost threshold for parallelism is the default 5")
        {
            Current = "5", Recommended = "50", FixScript = "EXEC sys.sp_configure N'cost threshold for parallelism', 50;\nRECONFIGURE;",
        },
        new("FullBackup", HealthCategory.Backups, HealthSeverity.Critical, "A database has no recent full backup") { Objects = ["Cards (—)", "Sales (2026-11-10 22:00)"] },
        new("AutoShrink", HealthCategory.Databases, HealthSeverity.Warning, "Auto shrink is on")
        {
            Objects = ["Archive"], FixScript = "ALTER DATABASE [Archive] SET AUTO_SHRINK OFF;",
        },
        new("Uptime", HealthCategory.Performance, HealthSeverity.Info, "The server started less than a day ago") { Current = "2026-11-25 08:00" },
        new("SaEnabled", HealthCategory.Security, HealthSeverity.Ok, "The sa login is enabled"),
        new("PageVerify", HealthCategory.Databases, HealthSeverity.Ok, "Page verification is not CHECKSUM"),
        new("NewRule", HealthCategory.Configuration, HealthSeverity.Warning, "A rule the host has no text for"),
    ];
}

/// <summary>A database in depth for the detail screen's tests: a full log, many VLFs, an old full backup, no log backup.</summary>
public static class FakeDatabaseDetail
{
    public static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.FromHours(3));

    public static DatabaseDetail Of(string name) => new(name)
    {
        State = "ONLINE",
        RecoveryModel = "FULL",
        CompatibilityLevel = 160,
        Collation = "Turkish_CI_AS",
        Owner = "sa",
        Created = new DateTimeOffset(2024, 3, 12, 10, 0, 0, TimeSpan.FromHours(3)),
        Options =
        [
            new("pageVerify", "Page verify", "CHECKSUM"), new("autoShrink", "Auto shrink", "OFF"), new("queryStore", "Query Store", "READ_WRITE"),
            new("somethingNew", "Something new", "ON"), new("empty", "Empty", null),
        ],
        DataMb = 2048,
        DataUsedMb = 1536,
        LogMb = 4096,
        LogUsedPercent = 92.4,
        LogReuseWait = "LOG_BACKUP",
        VirtualLogFiles = 450,
        TableCount = 37,
        TotalRows = 1_234_567,
        LastGoodCheckDb = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Files =
        [
            new("Cards") { Type = "ROWS", Filegroup = "PRIMARY", Path = @"D:\Data\Cards.mdf", SizeMb = 2048, UsedMb = 1536, Growth = "512 MB", MaxSize = "∞" },
            new("Cards_log") { Type = "LOG", Path = @"L:\Log\Cards_log.ldf", SizeMb = 4096, UsedMb = 3785, Growth = "10 %", MaxSize = "2048 MB" },
        ],
        Tables =
        [
            new("dbo.Transactions") { Rows = 1_000_000, ReservedMb = 900, DataMb = 600, IndexMb = 280 },
            new("dbo.Cards") { Rows = 200_000, ReservedMb = 240, DataMb = 160, IndexMb = 70 },
            new("audit.Log") { Rows = 34_567, ReservedMb = 12.5, DataMb = 12, IndexMb = 0.4 },
        ],
        Backups =
        [
            new(BackupKind.Differential, Now - TimeSpan.FromDays(2)) { Started = Now - TimeSpan.FromDays(2) - TimeSpan.FromMinutes(3), SizeMb = 300, CompressedMb = 80, Device = @"\\backup\Cards_diff.bak" },
            new(BackupKind.Full, Now - TimeSpan.FromDays(10)) { Started = Now - TimeSpan.FromDays(10) - TimeSpan.FromMinutes(20), SizeMb = 1500, CompressedMb = 400, Device = @"\\backup\Cards_full.bak", CopyOnly = false },
        ],
        BackupsKnown = true,
    };
}
