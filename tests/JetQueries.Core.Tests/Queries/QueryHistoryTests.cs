using JetQueries.Core.Queries;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Core.Tests.Queries;

public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("ALTER USER app IDENTIFIED BY Sup3rSecret;", "ALTER USER app IDENTIFIED BY '***';")]
    [InlineData("create user x identified by \"P@ss word\" default tablespace users", "create user x identified by '***' default tablespace users")]
    [InlineData("ALTER USER app IDENTIFIED BY VALUES 'S:ABCDEF'", "ALTER USER app IDENTIFIED BY VALUES '***'")]
    [InlineData("CREATE LOGIN app WITH PASSWORD = N'Sup3r''Secret', CHECK_POLICY = OFF", "CREATE LOGIN app WITH PASSWORD = '***', CHECK_POLICY = OFF")]
    [InlineData("ALTER LOGIN app WITH PASSWORD='new' OLD_PASSWORD='old'", "ALTER LOGIN app WITH PASSWORD='***' OLD_PASSWORD='***'")]
    [InlineData("ALTER ROLE app PASSWORD 'secret'", "ALTER ROLE app PASSWORD '***'")]
    [InlineData("CREATE DATABASE LINK l CONNECT TO u IDENTIFIED BY pw USING 'tns'", "CREATE DATABASE LINK l CONNECT TO u IDENTIFIED BY '***' USING 'tns'")]
    [InlineData("UPDATE users SET password = 'plain' WHERE id = 1", "UPDATE users SET password = '***' WHERE id = 1")]
    public void Password_literals_are_replaced(string sql, string expected) =>
        Assert.Equal(expected, SecretRedactor.Redact(sql));

    [Theory]
    [InlineData("SELECT id, password_hash FROM users WHERE name = 'x'")]
    [InlineData("SELECT password FROM users")]
    [InlineData("SELECT 'IDENTIFIED BY nothing' AS text FROM dual")]
    [InlineData("-- ALTER USER app IDENTIFIED BY secret\nSELECT 1")]
    [InlineData("SELECT * FROM cards WHERE note = 'my password = ''abc'''")]
    [InlineData("")]
    public void Other_text_is_left_exactly_as_it_is(string sql) =>
        Assert.Equal(sql, SecretRedactor.Redact(sql));

    [Fact]
    public void Several_secrets_in_a_script_are_all_replaced()
    {
        const string Script = "CREATE USER a IDENTIFIED BY one;\nCREATE USER b IDENTIFIED BY two;\nSELECT 1 FROM dual;";

        var redacted = SecretRedactor.Redact(Script);

        Assert.DoesNotContain("one", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("two", redacted, StringComparison.Ordinal);
        Assert.EndsWith("SELECT 1 FROM dual;", redacted, StringComparison.Ordinal);
    }
}

public sealed class SqliteQueryHistoryTests
{
    private static readonly Guid Cards = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Entries_round_trip_through_the_file_newest_first()
    {
        var path = NewPath();
        var failed = Entry("SELECT * FROM cardz", minutes: 2) with { Outcome = QueryOutcome.Failed, Error = "Invalid object name 'cardz'.", RowCount = null };
        await using (var history = Create(path))
        {
            await history.AddAsync(Entry("SELECT * FROM cards -- Müşteri", minutes: 1), Token);
            await history.AddAsync(failed, Token);
        }

        await using var reopened = Create(path);
        var entries = await reopened.SearchAsync(null, 50, Token);

        Assert.Equal(2, entries.Count);
        Assert.Equal(failed with { Id = entries[0].Id }, entries[0]);
        Assert.Equal("SELECT * FROM cards -- Müşteri", entries[1].Sql);
        Assert.Equal(3, entries[1].RowCount);
        Assert.Equal(TimeSpan.FromMilliseconds(120), entries[1].Duration);
        Assert.True(entries[0].Id > entries[1].Id);
    }

    [Fact]
    public async Task Search_finds_text_in_the_sql_or_the_connection_name_and_treats_wildcards_literally()
    {
        await using var history = Create(NewPath());
        await history.AddAsync(Entry("SELECT * FROM cards"), Token);
        await history.AddAsync(Entry("SELECT 100% AS rate, a_b FROM t"), Token);
        await history.AddAsync(Entry("SELECT 1") with { ConnectionName = "Logs PROD" }, Token);

        Assert.Single(await history.SearchAsync("from CARDS", 50, Token));

        // "Cards TEST" is the connection name of two of the three.
        Assert.Equal(2, (await history.SearchAsync("cards test", 50, Token)).Count);
        Assert.Single(await history.SearchAsync("logs prod", 50, Token));
        Assert.Single(await history.SearchAsync("100%", 50, Token));
        Assert.Single(await history.SearchAsync("a_b", 50, Token));
        Assert.Empty(await history.SearchAsync("axb", 50, Token));
        Assert.Equal(3, (await history.SearchAsync("  ", 50, Token)).Count);
        Assert.Equal(2, (await history.SearchAsync(null, 2, Token)).Count);
    }

    [Fact]
    public async Task Secrets_never_reach_the_file()
    {
        var path = NewPath();
        await using (var history = Create(path))
        {
            await history.AddAsync(Entry("ALTER USER app IDENTIFIED BY Sup3rSecretValue"), Token);
        }

        var bytes = await File.ReadAllBytesAsync(path, Token);
        Assert.False(System.Text.Encoding.UTF8.GetString(bytes).Contains("Sup3rSecretValue", StringComparison.Ordinal));
        if (File.Exists(path + "-wal"))
        {
            Assert.False(System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path + "-wal", Token)).Contains("Sup3rSecretValue", StringComparison.Ordinal));
        }

        await using var reopened = Create(path);
        Assert.Equal("ALTER USER app IDENTIFIED BY '***'", Assert.Single(await reopened.SearchAsync(null, 10, Token)).Sql);
    }

    [Fact]
    public async Task Only_the_newest_entries_are_kept_and_long_scripts_are_cut()
    {
        await using var history = Create(NewPath(), maxEntries: 3, maxLength: 20);
        for (var i = 1; i <= 5; i++)
        {
            await history.AddAsync(Entry($"SELECT {i}"), Token);
        }

        await history.AddAsync(Entry("SELECT '" + new string('x', 100) + "'"), Token);

        var entries = await history.SearchAsync(null, 50, Token);
        Assert.Equal(3, entries.Count);
        Assert.StartsWith("SELECT 'xxxxxxxxxxxx\n-- … (cut", entries[0].Sql, StringComparison.Ordinal);
        Assert.Equal(["SELECT 5", "SELECT 4"], entries.Skip(1).Select(static e => e.Sql));
    }

    [Fact]
    public async Task Delete_and_clear_remove_entries_and_raise_changed()
    {
        var path = NewPath();
        await using var history = Create(path);
        var changes = 0;
        history.Changed += (_, _) => changes++;
        await history.AddAsync(Entry("SELECT 1"), Token);
        await history.AddAsync(Entry("SELECT 2 -- UniqueMarkerText"), Token);
        var entries = await history.SearchAsync(null, 10, Token);

        await history.DeleteAsync(entries[1].Id, Token);
        Assert.Equal("SELECT 2 -- UniqueMarkerText", Assert.Single(await history.SearchAsync(null, 10, Token)).Sql);

        await history.ClearAsync(Token);
        Assert.Empty(await history.SearchAsync(null, 10, Token));
        Assert.Equal(4, changes);
    }

    [Fact]
    public async Task An_unreadable_file_is_kept_aside_and_a_new_history_starts()
    {
        var path = NewPath();
        await File.WriteAllTextAsync(path, new string('x', 4096), Token);

        await using (var history = Create(path))
        {
            await history.AddAsync(Entry("SELECT 1"), Token);
            Assert.Single(await history.SearchAsync(null, 10, Token));
        }

        var aside = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".unreadable-*");
        Assert.Equal(new string('x', 4096), await File.ReadAllTextAsync(Assert.Single(aside), Token));
    }

    [Fact]
    public async Task Without_a_usable_path_the_history_lives_in_memory()
    {
        await using var none = Create(null);
        await none.AddAsync(Entry("SELECT 1"), Token);
        await none.AddAsync(Entry("ALTER USER a IDENTIFIED BY secret"), Token);

        var entries = await none.SearchAsync("identified", 10, Token);
        Assert.Equal("ALTER USER a IDENTIFIED BY '***'", Assert.Single(entries).Sql);
        await none.DeleteAsync(entries[0].Id, Token);
        Assert.Equal("SELECT 1", Assert.Single(await none.SearchAsync(null, 10, Token)).Sql);

        // A directory where the file should be: it can be neither opened nor moved out of the way usefully.
        var directory = Path.Combine(AppContext.BaseDirectory, "history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "child"));
        await using var unusable = Create(directory);
        await unusable.AddAsync(Entry("SELECT 2"), Token);
        Assert.Single(await unusable.SearchAsync(null, 10, Token));
    }

    private static QueryHistoryEntry Entry(string sql, int minutes = 0) => new(
        0,
        DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000 + (minutes * 60_000L)),
        Cards,
        "Cards TEST",
        "sqlserver",
        sql,
        TimeSpan.FromMilliseconds(120),
        QueryOutcome.Succeeded,
        3);

    private static SqliteQueryHistory Create(string? path, int maxEntries = 10_000, int maxLength = 100_000) =>
        new(new QueryHistoryOptions { DatabasePath = path, MaxEntries = maxEntries, MaxSqlLength = maxLength }, NullLogger<SqliteQueryHistory>.Instance);

    private static string NewPath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "history.db");
    }
}
