using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using JetQueries.Core.Extensions;
using JetQueries.Provider.PostgreSql.Language;
using JetQueries.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace JetQueries.Provider.PostgreSql.Tests;

public sealed class PostgreSqlProviderTests
{
    private static readonly int?[] Integers = [1, 2, null];
    private static readonly decimal[] Decimals = [1.5m, 2.25m];
    private static readonly bool[] Bits = [true, false, true];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_connection_string_follows_the_settings_and_never_pools()
    {
        var text = PostgreSqlConnectionSettings.BuildConnectionString(
            Profile(new() { ["host"] = " db01 ", ["port"] = "6432", ["database"] = "cards", ["sslMode"] = "verify-full", ["connectTimeoutSeconds"] = "5" }),
            new ConnectionCredential("app", "p;w'd"));
        var builder = new NpgsqlConnectionStringBuilder(text);

        Assert.Equal("db01", builder.Host);
        Assert.Equal(6432, builder.Port);
        Assert.Equal("cards", builder.Database);
        Assert.Equal("app", builder.Username);
        Assert.Equal("p;w'd", builder.Password);
        Assert.Equal(SslMode.VerifyFull, builder.SslMode);
        Assert.Equal(5, builder.Timeout);
        Assert.False(builder.Pooling);
        Assert.False(builder.PersistSecurityInfo);

        var defaults = new NpgsqlConnectionStringBuilder(PostgreSqlConnectionSettings.BuildConnectionString(Profile(new() { ["host"] = "db01" }), new ConnectionCredential("app", null)));
        Assert.Equal(5432, defaults.Port);
        Assert.Equal("postgres", defaults.Database);
        Assert.Equal(SslMode.Prefer, defaults.SslMode);
    }

    [Theory]
    [InlineData("host", "")]
    [InlineData("port", "70000")]
    [InlineData("sslMode", "sometimes")]
    [InlineData("connectTimeoutSeconds", "0")]
    public void Bad_settings_are_refused(string key, string value)
    {
        var settings = new Dictionary<string, string> { ["host"] = "db01", [key] = value };
        Assert.Throws<ArgumentException>(() => PostgreSqlConnectionSettings.BuildConnectionString(Profile(settings), new ConnectionCredential("app", "x")));
    }

    [Fact]
    public void A_user_name_is_required()
    {
        Assert.Throws<ArgumentException>(() => PostgreSqlConnectionSettings.BuildConnectionString(Profile(new() { ["host"] = "db01" }), null));
        Assert.True(new PostgreSqlProvider().RequiresCredential(new Dictionary<string, string>()));
    }

    [Fact]
    public void Only_a_single_tcp_host_is_probed()
    {
        var provider = new PostgreSqlProvider();
        Assert.Equal(new NetworkEndpoint("db01", 5432), provider.GetEndpoint(new Dictionary<string, string> { ["host"] = "db01" }));
        Assert.Equal(new NetworkEndpoint("db01", 6432), provider.GetEndpoint(new Dictionary<string, string> { ["host"] = "db01", ["port"] = "6432" }));
        Assert.Null(provider.GetEndpoint(new Dictionary<string, string> { ["host"] = "db01,db02" }));
        Assert.Null(provider.GetEndpoint(new Dictionary<string, string> { ["host"] = "/var/run/postgresql" }));
    }

    [Fact]
    public void Identifiers_are_double_quoted_with_quotes_doubled()
    {
        Assert.Equal("\"Müşteri \"\"İşlemleri\"\"\"", new PostgreSqlProvider().QuoteIdentifier("Müşteri \"İşlemleri\""));
        Assert.Equal("\"kart\".\"cards\"", PostgreSqlNames.Quote("kart", "cards"));
        Assert.Equal("'it''s'", PostgreSqlNames.Literal("it's"));
        Assert.Throws<ArgumentException>(() => PostgreSqlNames.Quote("a\0b"));
    }

    [Fact]
    public void Scripts_split_at_statement_ends_but_not_inside_bodies_strings_or_comments()
    {
        const string Script = """
            CREATE FUNCTION add_one(i int) RETURNS int LANGUAGE plpgsql AS $$
            BEGIN
                RAISE NOTICE 'adding; one';
                RETURN i + 1;
            END;
            $$;
            CREATE FUNCTION two() RETURNS int LANGUAGE sql
            BEGIN ATOMIC
                SELECT CASE WHEN true THEN 1 ELSE 0 END;
                SELECT 2;
            END;
            /* a /* nested ; */ comment */ SELECT E'it\'s; fine', $tag$ $$ ; $tag$, 'x''y;';
            BEGIN;
            DO $body$ BEGIN PERFORM 1; END $body$
            """;

        var batches = new PostgreSqlProvider().SplitBatches(Script);

        Assert.Equal(5, batches.Count);
        Assert.StartsWith("CREATE FUNCTION add_one", batches[0].Text, StringComparison.Ordinal);
        Assert.EndsWith("$$", batches[0].Text, StringComparison.Ordinal);
        Assert.Equal(0, batches[0].StartLine);
        Assert.StartsWith("CREATE FUNCTION two()", batches[1].Text, StringComparison.Ordinal);
        Assert.EndsWith("END", batches[1].Text, StringComparison.Ordinal);
        Assert.Equal(6, batches[1].StartLine);
        Assert.Equal("SELECT E'it\\'s; fine', $tag$ $$ ; $tag$, 'x''y;'", batches[2].Text);
        Assert.Equal(11, batches[2].StartLine);
        Assert.Equal("BEGIN", batches[3].Text);
        Assert.Equal("DO $body$ BEGIN PERFORM 1; END $body$", batches[4].Text);
    }

    [Fact]
    public void Colouring_knows_dollar_quotes_casts_and_parameters_across_lines()
    {
        var service = new PostgreSqlProvider().LanguageService!;
        const string First = "SELECT $1::text, :psql_var, x::int, $fn$ body";
        var first = service.TokenizeLine(First, 0);
        var tokens = first.Tokens.Select(t => (First.Substring(t.Span.Start, t.Span.Length), t.Kind)).ToList();

        Assert.Contains(("$1", SqlTokenKind.Parameter), tokens);
        Assert.Contains((":psql_var", SqlTokenKind.Parameter), tokens);
        Assert.Contains(("::", SqlTokenKind.Operator), tokens);
        Assert.Contains(("int", SqlTokenKind.DataType), tokens);
        Assert.Contains(("$fn$ body", SqlTokenKind.StringLiteral), tokens);
        Assert.NotEqual(0, first.EndState);

        // Inside the body "$$" does not end it; "$fn$" does.
        const string Second = "  still $$ inside $fn$ AS done /* a /* b */";
        var second = service.TokenizeLine(Second, first.EndState);
        var rest = second.Tokens.Select(t => (Second.Substring(t.Span.Start, t.Span.Length), t.Kind)).ToList();
        Assert.Equal(("  still $$ inside $fn$", SqlTokenKind.StringLiteral), rest[0]);
        Assert.Contains(("AS", SqlTokenKind.Keyword), rest);
        Assert.Equal(SqlTokenKind.Comment, rest[^1].Kind);

        // The nested comment is still open after one "*/".
        var third = service.TokenizeLine("c */ SELECT", second.EndState);
        Assert.Equal(SqlTokenKind.Comment, third.Tokens[0].Kind);
        Assert.Equal(SqlTokenKind.Keyword, third.Tokens[1].Kind);
        Assert.Equal(0, third.EndState);
    }

    [Fact]
    public async Task An_unclosed_dollar_quote_or_nested_comment_is_an_error()
    {
        var service = new PostgreSqlProvider().LanguageService!;
        Assert.Empty(await service.GetDiagnosticsAsync(new SqlDocument("DO $$ BEGIN END $$; /* a /* b */ c */ SELECT 1"), Token));

        var body = Assert.Single(await service.GetDiagnosticsAsync(new SqlDocument("SELECT 1;\nDO $x$ BEGIN\nEND $$;"), Token));
        Assert.Equal(13, body.Span.Start);

        Assert.Single(await service.GetDiagnosticsAsync(new SqlDocument("/* a /* b */ still open"), Token));
    }

    [Theory]
    [InlineData("cards", "cards")]
    [InlineData("CARDS", "cards")]
    [InlineData("\"Cards\"", "Cards")]
    [InlineData("\"a\"\"b\"", "a\"b")]
    [InlineData("İşlem", "İşlem")]
    [InlineData("KartNo", "kartno")]
    public void Unquoted_names_fold_to_lower_case_ascii_only(string token, string name) =>
        Assert.Equal(name, PostgreSqlDialect.Instance.NameOf(token));

    [Theory]
    [InlineData("cards", "cards")]
    [InlineData("card_no$2", "card_no$2")]
    [InlineData("Cards", "\"Cards\"")]
    [InlineData("müşteri işlemleri", "\"müşteri işlemleri\"")]
    [InlineData("müşteri", "müşteri")]
    [InlineData("user", "\"user\"")]
    [InlineData("name", "name")]
    public void Names_are_quoted_only_when_they_must_be(string name, string expected) =>
        Assert.Equal(expected, PostgreSqlDialect.Instance.QuoteIfNeeded(name));

    [Fact]
    public void Values_without_a_grid_form_are_written_the_postgresql_way()
    {
        var utc = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTimeOffset(utc), PostgreSqlQueryExecutor.Simplify(utc, "timestamp with time zone"));
        Assert.Equal(utc, PostgreSqlQueryExecutor.Simplify(utc, "timestamp without time zone"));
        Assert.Equal("{1,2,NULL}", PostgreSqlQueryExecutor.Simplify(Integers, "integer[]"));
        Assert.Equal("{1.5,2.25}", PostgreSqlQueryExecutor.Simplify(Decimals, "numeric[]"));
        Assert.Equal("101", PostgreSqlQueryExecutor.Simplify(new BitArray(Bits), "bit(3)"));
        Assert.Equal("\"a\"=>\"1\", \"b\"=>NULL", PostgreSqlQueryExecutor.Simplify(new Dictionary<string, string?> { ["a"] = "1", ["b"] = null }, "hstore"));
        Assert.Equal(new byte[] { 1 }, PostgreSqlQueryExecutor.Simplify(new byte[] { 1 }, "bytea"));
    }

    [Fact]
    public async Task Completion_offers_tables_columns_and_schema_members()
    {
        var service = new PostgreSqlProvider().LanguageService!;

        var tables = await CompleteAsync(service, "SELECT * FROM |");
        Assert.Contains(tables, static i => i.Label == "cards" && i.Kind == CompletionItemKind.Table && i.InsertText is null or "cards");
        Assert.Contains(tables, static i => i.Label == "Müşteri İşlemleri" && i.InsertText == "\"Müşteri İşlemleri\"");

        var columns = await CompleteAsync(service, "SELECT c.| FROM Cards AS c");
        Assert.Equal(["id", "card_no", "Balance"], columns.Where(static i => i.Kind == CompletionItemKind.Column).Select(static i => i.Label));
        Assert.Equal("\"Balance\"", columns.Single(static i => i.Label == "Balance").InsertText);

        var members = await CompleteAsync(service, "SELECT * FROM report.|");
        Assert.Equal(["daily_totals"], members.Where(static i => i.Kind == CompletionItemKind.Table).Select(static i => i.Label));

        var procedures = await CompleteAsync(service, "CALL |");
        Assert.Contains(procedures, static i => i.Label == "purge_old_logs");
    }

    [Fact]
    public async Task The_provider_loads_as_an_extension_with_its_own_driver()
    {
        var providerDirectory = typeof(PostgreSqlProviderTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(static a => a.Key == "ProviderDirectory").Value!;
        var options = new ExtensionHostOptions();
        options.Directories.Add(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(providerDirectory)))!);
        var host = new ExtensionHost(options, NullLogger<ExtensionHost>.Instance);

        var results = await host.LoadAsync(Token);

        var loaded = Assert.Single(results, static r => r.Manifest?.Id == "jetqueries.postgresql");
        Assert.True(loaded.State == ExtensionState.Loaded, loaded.Error);
        var provider = host.FindDatabaseProvider(PostgreSqlProvider.ProviderId)!;
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(provider.GetType().Assembly));

        // Nothing listens on port 1: the attempt fails inside the driver, which proves it was resolved from the extension.
        var error = await Assert.ThrowsAnyAsync<Exception>(() => provider.OpenConnectionAsync(
            Profile(new() { ["host"] = "127.0.0.1", ["port"] = "1", ["connectTimeoutSeconds"] = "2" }), new ConnectionCredential("app", "x"), Token));
        Assert.Contains("Npgsql", error.GetType().FullName, StringComparison.Ordinal);
    }

    internal static ConnectionProfile Profile(Dictionary<string, string> settings) =>
        new(Guid.NewGuid(), "test", PostgreSqlProvider.ProviderId, settings);

    private static async Task<List<CompletionItem>> CompleteAsync(ISqlLanguageService service, string sqlWithCaret)
    {
        var offset = sqlWithCaret.IndexOf('|', StringComparison.Ordinal);
        return [.. await service.GetCompletionsAsync(new SqlDocument(sqlWithCaret.Remove(offset, 1), new PostgreSqlCatalog()), offset, Token)];
    }
}

/// <summary>Current schema public: cards, "Müşteri İşlemleri", procedure purge_old_logs; schema report: daily_totals.</summary>
internal sealed class PostgreSqlCatalog : IMetadataProvider
{
    public async IAsyncEnumerable<DatabaseObject> GetObjectsAsync(
        DatabaseObject? parent, DatabaseObjectKinds kinds, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        if (parent is null && kinds.HasFlag(DatabaseObjectKinds.Schema))
        {
            yield return new(new(null, null, "public"), DatabaseObjectKinds.Schema);
            yield return new(new(null, null, "report"), DatabaseObjectKinds.Schema);
        }

        var schema = parent is null ? "public" : parent.Name.Name;
        (string Name, DatabaseObjectKinds Kind)[] objects = schema switch
        {
            "public" => [("cards", DatabaseObjectKinds.Table), ("Müşteri İşlemleri", DatabaseObjectKinds.Table), ("purge_old_logs", DatabaseObjectKinds.Procedure)],
            "report" => [("daily_totals", DatabaseObjectKinds.Table)],
            _ => [],
        };
        foreach (var (name, kind) in objects)
        {
            if (kinds.HasFlag(kind))
            {
                yield return new(new(null, schema, name), kind);
            }
        }
    }

    public Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DatabaseObjectName tableOrView, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ColumnInfo>>(tableOrView.Name switch
        {
            "cards" => [new("id", "bigint", false, 1, true), new("card_no", "character varying(19)", false, 2), new("Balance", "numeric(12,2)", true, 3)],
            _ => [],
        });

    public Task<string?> GetDefinitionAsync(DatabaseObject target, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
