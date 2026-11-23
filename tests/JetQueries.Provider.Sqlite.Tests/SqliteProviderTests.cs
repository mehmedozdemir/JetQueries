using System.Reflection;
using System.Runtime.Loader;
using JetQueries.Core.Extensions;
using JetQueries.Provider.Sqlite.Language;
using JetQueries.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Provider.Sqlite.Tests;

public sealed class SqliteProviderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void A_file_is_required_and_in_memory_databases_are_refused()
    {
        Assert.Throws<ArgumentException>(() => SqliteConnectionSettings.BuildConnectionString(Profile([]), out _, out _));
        Assert.Throws<ArgumentException>(() => SqliteConnectionSettings.BuildConnectionString(Profile(new() { ["file"] = ":memory:" }), out _, out _));
        Assert.Throws<ArgumentException>(() => SqliteConnectionSettings.BuildConnectionString(Profile(new() { ["file"] = "file:x.db?mode=memory" }), out _, out _));
        Assert.Throws<ArgumentException>(() => SqliteConnectionSettings.BuildConnectionString(Profile(new() { ["file"] = "a.db", ["mode"] = "append" }), out _, out _));
    }

    [Fact]
    public void The_connection_string_follows_the_settings()
    {
        Environment.SetEnvironmentVariable("JETQUERIES_TEST_DIR", Path.GetTempPath());
        var text = SqliteConnectionSettings.BuildConnectionString(
            Profile(new() { ["file"] = "\"%JETQUERIES_TEST_DIR%cards.db\"", ["mode"] = "readOnly", ["foreignKeys"] = "false" }), out var path, out var mustExist);

        Assert.Equal(Path.Combine(Path.GetTempPath(), "cards.db"), path);
        Assert.True(mustExist);
        Assert.Contains("Mode=ReadOnly", text, StringComparison.Ordinal);
        Assert.Contains("Foreign Keys=False", text, StringComparison.Ordinal);
        Assert.Contains("Pooling=False", text, StringComparison.Ordinal);

        SqliteConnectionSettings.BuildConnectionString(Profile(new() { ["file"] = "new.db", ["mode"] = "create" }), out _, out mustExist);
        Assert.False(mustExist);
    }

    [Fact]
    public async Task A_missing_file_is_reported_instead_of_being_created()
    {
        var path = Path.Combine(NewDirectory(), "missing.db");
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => new SqliteProvider().OpenConnectionAsync(Profile(new() { ["file"] = path }), null, Token));
        Assert.Contains(path, error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void No_credential_and_no_network_are_involved()
    {
        var provider = new SqliteProvider();
        Assert.False(provider.RequiresCredential(new Dictionary<string, string>()));
        Assert.Null(provider.GetEndpoint(new Dictionary<string, string> { ["file"] = "a.db" }));
        Assert.Equal("\"Müşteri \"\"İşlemleri\"\"\"", provider.QuoteIdentifier("Müşteri \"İşlemleri\""));
        Assert.Equal(ConnectionSettingKind.FilePath, provider.ConnectionSettings[0].Kind);
    }

    [Fact]
    public void Scripts_split_at_statement_ends_but_not_inside_strings_comments_or_trigger_bodies()
    {
        const string Script = """
            -- cards
            CREATE TABLE cards (id INTEGER PRIMARY KEY, note TEXT DEFAULT 'a; b');
            /* ; */ INSERT INTO cards (note) VALUES ('x;y'), ("z");

            CREATE TRIGGER trg AFTER INSERT ON cards
            BEGIN
                UPDATE cards SET note = CASE WHEN new.note IS NULL THEN 'none' ELSE new.note END WHERE id = new.id;
                SELECT RAISE(ABORT, 'no;') WHERE new.id < 0;
            END;
            BEGIN TRANSACTION;
            SELECT [a;b], `c;d` FROM cards
            """;

        var batches = new SqliteProvider().SplitBatches(Script);

        Assert.Equal(5, batches.Count);
        Assert.Equal("CREATE TABLE cards (id INTEGER PRIMARY KEY, note TEXT DEFAULT 'a; b')", batches[0].Text);
        Assert.Equal(1, batches[0].StartLine);
        Assert.Equal("INSERT INTO cards (note) VALUES ('x;y'), (\"z\")", batches[1].Text);
        Assert.Equal(2, batches[1].StartLine);
        Assert.StartsWith("CREATE TRIGGER trg", batches[2].Text, StringComparison.Ordinal);
        Assert.EndsWith("END", batches[2].Text, StringComparison.Ordinal);
        Assert.Equal(4, batches[2].StartLine);
        Assert.Equal("BEGIN TRANSACTION", batches[3].Text);
        Assert.Equal("SELECT [a;b], `c;d` FROM cards", batches[4].Text);
        Assert.Equal(10, batches[4].StartLine);

        Assert.Empty(new SqliteProvider().SplitBatches("-- nothing\n;;\n/* here */"));
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)", true)]
    [InlineData("-- note\nupdate t set a = 1", true)]
    [InlineData("REPLACE INTO t VALUES (1)", true)]
    [InlineData("CREATE TABLE t (a)", false)]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x", false)]
    public void Only_data_changes_report_a_row_count(string sql, bool isDml) => Assert.Equal(isDml, SqliteQueryExecutor.IsDml(sql));

    [Fact]
    public void Colouring_knows_sqlite_quotes_parameters_and_blobs()
    {
        var service = new SqliteProvider().LanguageService!;
        const string Line = "SELECT [Kart No], `x`, X'0A0B', ?1, :name, @p, $q FROM \"Müşteri\" -- c";

        var tokens = service.TokenizeLine(Line, 0).Tokens.Select(t => (Line.Substring(t.Span.Start, t.Span.Length), t.Kind)).ToList();

        Assert.Contains(("[Kart No]", SqlTokenKind.Identifier), tokens);
        Assert.Contains(("`x`", SqlTokenKind.Identifier), tokens);
        Assert.Contains(("X'0A0B'", SqlTokenKind.StringLiteral), tokens);
        Assert.Contains(("?1", SqlTokenKind.Parameter), tokens);
        Assert.Contains((":name", SqlTokenKind.Parameter), tokens);
        Assert.Contains(("@p", SqlTokenKind.Parameter), tokens);
        Assert.Contains(("$q", SqlTokenKind.Parameter), tokens);
        Assert.Contains(("\"Müşteri\"", SqlTokenKind.Identifier), tokens);
        Assert.Contains(("-- c", SqlTokenKind.Comment), tokens);
        Assert.Contains(("SELECT", SqlTokenKind.Keyword), tokens);
    }

    [Fact]
    public async Task A_string_or_comment_that_never_ends_is_an_error()
    {
        var service = new SqliteProvider().LanguageService!;

        Assert.Empty(await service.GetDiagnosticsAsync(new SqlDocument("SELECT 'a''b' /* x */ FROM t"), Token));

        var open = await service.GetDiagnosticsAsync(new SqlDocument("SELECT 1;\nSELECT 'abc\nFROM t"), Token);
        var diagnostic = Assert.Single(open);
        Assert.Equal(17, diagnostic.Span.Start);
        Assert.Equal("This string is never closed.", diagnostic.Message);

        var comment = Assert.Single(await service.GetDiagnosticsAsync(new SqlDocument("SELECT 1 /* a\n b\n c"), Token));
        Assert.Equal(9, comment.Span.Start);
    }

    [Theory]
    [InlineData("cards", "cards")]
    [InlineData("Cards", "Cards")]
    [InlineData("Müşteri İşlemleri", "\"Müşteri İşlemleri\"")]
    [InlineData("order", "\"order\"")]
    [InlineData("2fa", "\"2fa\"")]
    public void Names_are_quoted_only_when_they_must_be(string name, string expected) =>
        Assert.Equal(expected, SqliteDialect.Instance.QuoteIfNeeded(name));

    [Fact]
    public async Task The_provider_loads_as_an_extension_with_its_own_native_library()
    {
        var providerDirectory = typeof(SqliteProviderTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(static a => a.Key == "ProviderDirectory").Value!;
        var options = new ExtensionHostOptions();
        options.Directories.Add(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(providerDirectory)))!);
        var host = new ExtensionHost(options, NullLogger<ExtensionHost>.Instance);

        var results = await host.LoadAsync(Token);

        var loaded = Assert.Single(results, static r => r.Manifest?.Id == "jetqueries.sqlite");
        Assert.True(loaded.State == ExtensionState.Loaded, loaded.Error);
        var provider = host.FindDatabaseProvider(SqliteProvider.ProviderId)!;
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(provider.GetType().Assembly));

        // A real database through the extension's own Microsoft.Data.Sqlite and e_sqlite3.
        var path = Path.Combine(NewDirectory(), "ext.db");
        await using var connection = await provider.OpenConnectionAsync(Profile(new() { ["file"] = path, ["mode"] = "create" }), null, Token);
        var events = await Collect(connection.Executor.ExecuteAsync(new QueryRequest("SELECT sqlite_version() AS v"), Token));
        Assert.Single(events.OfType<ResultSetStarted>());
        Assert.StartsWith("3.", (string)events.OfType<RowBatch>().Single().Rows[0][0]!, StringComparison.Ordinal);
    }

    internal static ConnectionProfile Profile(Dictionary<string, string> settings) =>
        new(Guid.NewGuid(), "test", SqliteProvider.ProviderId, settings);

    internal static string NewDirectory()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "sqlite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static async Task<List<QueryEvent>> Collect(IAsyncEnumerable<QueryEvent> events)
    {
        var list = new List<QueryEvent>();
        await foreach (var item in events)
        {
            list.Add(item);
        }

        return list;
    }
}
