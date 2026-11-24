using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using JetQueries.Sdk;
using StackExchange.Redis;

namespace JetQueries.Provider.Redis;

/// <summary>
/// A Redis connection: one multiplexer, the logical database this window works in (<c>SELECT n</c> in the console,
/// the window's database selector), the console (<see cref="Executor"/>) and the key browser (<see cref="Metadata"/>).
/// </summary>
internal sealed class RedisConnection : IDatabaseConnection, IDatabaseContext
{
    private readonly ConnectionMultiplexer _multiplexer;
    private int _database;

    public RedisConnection(ConnectionMultiplexer multiplexer, int database)
    {
        _multiplexer = multiplexer;
        _database = database;
        Executor = new RedisConsole(this);
        Metadata = new RedisKeyStore(this);
    }

    public IQueryExecutor Executor { get; }

    public IMetadataProvider Metadata { get; }

    public DatabaseContextKind Kind => DatabaseContextKind.Database;

    /// <summary>The logical database of the window.</summary>
    public int Database
    {
        get => Volatile.Read(ref _database);
        set => Volatile.Write(ref _database, value);
    }

    public IDatabase Db(int database) => _multiplexer.GetDatabase(database);

    public IServer Server => _multiplexer.GetServers().FirstOrDefault(static s => s.IsConnected) ?? _multiplexer.GetServers()[0];

    public Task<string?> GetCurrentAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(Name(Database));

    public async Task<IReadOnlyList<string>> GetAvailableAsync(CancellationToken cancellationToken)
    {
        var count = await DatabaseCountAsync(cancellationToken).ConfigureAwait(false);
        return [.. Enumerable.Range(0, count).Select(Name)];
    }

    public Task ChangeAsync(string name, CancellationToken cancellationToken)
    {
        Database = Parse(name) ?? throw new ArgumentException($"Not a Redis database: {name}", nameof(name));
        return Task.CompletedTask;
    }

    /// <summary>How many logical databases the server has (16 unless configured otherwise, or when CONFIG is not allowed).</summary>
    public async Task<int> DatabaseCountAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = await Server.ConfigGetAsync("databases").WaitAsync(cancellationToken).ConfigureAwait(false);
            if (config is [var pair, ..] && int.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0)
            {
                return Math.Min(count, 256);
            }
        }
        catch (RedisServerException)
        {
            // CONFIG may be renamed or forbidden (managed services).
        }

        return 16;
    }

    /// <summary>"db0".</summary>
    public static string Name(int database) => "db" + database.ToString(CultureInfo.InvariantCulture);

    /// <summary>The number of "db3" or "3"; null for anything else.</summary>
    public static int? Parse(string name)
    {
        var digits = name.StartsWith("db", StringComparison.OrdinalIgnoreCase) ? name[2..] : name;
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    public async ValueTask DisposeAsync() => await _multiplexer.DisposeAsync().ConfigureAwait(false);
}

/// <summary>
/// The console: each line of the text is a command, run in order; each answer is a result set (a list as numbered
/// rows, a hash as field and value, INFO as section, key and value). Blocking and endless commands are refused;
/// <c>SELECT n</c> switches the window's database.
/// </summary>
internal sealed class RedisConsole(RedisConnection connection) : IQueryExecutor
{
    public async IAsyncEnumerable<QueryEvent> ExecuteAsync(QueryRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Parameters.Count > 0)
        {
            throw new NotSupportedException("Redis commands take no parameters; write the values in the command.");
        }

        foreach (var command in RedisCommandParser.Parse(request.Sql))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var words = command.Words;
            var name = RedisCommands.NameOf(words);
            if (RedisCommands.EffectOf(words) == RedisCommandEffect.Refused)
            {
                var hint = RedisCommands.Find(words)?.Summary ?? "Not available in the console.";
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Line {command.Line}: {name} — {hint}"));
            }

            if (words[0].Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                if (words.Count != 2 || RedisConnection.Parse(words[1]) is not { } database)
                {
                    throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"Line {command.Line}: SELECT takes a database number."));
                }

                var count = await connection.DatabaseCountAsync(cancellationToken).ConfigureAwait(false);
                if (database >= count)
                {
                    throw new ArgumentOutOfRangeException(nameof(request), string.Create(CultureInfo.InvariantCulture, $"Line {command.Line}: the server has databases 0–{count - 1}."));
                }

                connection.Database = database;
                yield return new QueryMessage("OK — " + RedisConnection.Name(database), QueryMessageSeverity.Info, command.Line);
                continue;
            }

            RedisResult result;
            try
            {
                result = await connection.Db(connection.Database).ExecuteAsync(words[0], [.. command.Values.Skip(1)]).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (RedisServerException ex)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Line {command.Line}: {ex.Message}"), ex);
            }

            foreach (var item in RedisResults.Events(words, result, request.MaxRows))
            {
                yield return item;
            }
        }
    }
}

/// <summary>A command's answer as result sets.</summary>
internal static class RedisResults
{
    private static readonly ResultColumn Value = new("value", "string", typeof(string));

    public static IEnumerable<QueryEvent> Events(IReadOnlyList<string> words, RedisResult result, int? maxRows)
    {
        var name = RedisCommands.NameOf(words);
        if (result.IsNull)
        {
            return Table([Value], [[null]], maxRows);
        }

        switch (result.Resp2Type)
        {
            case ResultType.Integer:
                return Table([new("integer", "integer", typeof(long))], [[(long)result]], maxRows);
            case ResultType.SimpleString or ResultType.BulkString when name == "INFO":
                return Table(
                    [new("section", "string", typeof(string)), new("name", "string", typeof(string)), new("value", "string", typeof(string))],
                    Info(result.ToString()),
                    maxRows);
            case ResultType.SimpleString or ResultType.BulkString:
                return Table([Value], [[Text(result)]], maxRows);
            case ResultType.Array:
                var items = (RedisResult[])result!;

                // SCAN, HSCAN, SSCAN, ZSCAN: [cursor, [items]].
                if (name.EndsWith("SCAN", StringComparison.Ordinal) && items is [var cursor, var page] && page.Resp2Type == ResultType.Array)
                {
                    var found = (RedisResult[])page!;
                    var rows = Rows(name == "SCAN" || name == "SSCAN" ? 1 : 2, found);
                    return [new QueryMessage("cursor " + cursor, QueryMessageSeverity.Info), .. Table(PairColumns(name, rows), rows, maxRows)];
                }

                if (Pairs(name, words) && items.Length % 2 == 0)
                {
                    var rows = Rows(2, items);
                    return Table(PairColumns(name, rows), rows, maxRows);
                }

                return Table(
                    [new("#", "integer", typeof(long)), Value],
                    [.. items.Select(static (item, i) => new object?[] { (long)i + 1, item.IsNull ? null : Nested(item) })],
                    maxRows);
            default:
                return Table([Value], [[result.ToString()]], maxRows);
        }
    }

    // Answers that are field/value (or member/score) pairs.
    private static bool Pairs(string name, IReadOnlyList<string> words) =>
        name is "HGETALL" or "CONFIG GET" ||
        (name is "HRANDFIELD" && words.Any(static w => w.Equals("WITHVALUES", StringComparison.OrdinalIgnoreCase))) ||
        (name.StartsWith('Z') && words.Any(static w => w.Equals("WITHSCORES", StringComparison.OrdinalIgnoreCase)));

    private static List<ResultColumn> PairColumns(string name, List<object?[]> rows) =>
        rows.Count > 0 && rows[0].Length == 1 ? [name == "SCAN" ? new("key", "string", typeof(string)) : new("member", "string", typeof(string))]
        : name.StartsWith('Z') ? [new("member", "string", typeof(string)), new("score", "string", typeof(string))]
        : [new("field", "string", typeof(string)), new("value", "string", typeof(string))];

    private static List<object?[]> Rows(int width, RedisResult[] items)
    {
        var rows = new List<object?[]>(items.Length / width + 1);
        for (var i = 0; i + width - 1 < items.Length; i += width)
        {
            rows.Add([.. items.Skip(i).Take(width).Select(static v => v.IsNull ? null : (object?)Nested(v))]);
        }

        return rows;
    }

    private static List<object?[]> Info(string text)
    {
        var rows = new List<object?[]>();
        var section = string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                section = line[2..].Trim();
            }
            else if (line.IndexOf(':', StringComparison.Ordinal) is var colon and > 0)
            {
                rows.Add([section, line[..colon], line[(colon + 1)..]]);
            }
        }

        return rows;
    }

    // A nested answer (an XRANGE entry, a SLOWLOG row) as redis-cli shows it, on one cell.
    private static string Nested(RedisResult result)
    {
        if (result.Resp2Type != ResultType.Array)
        {
            return result.IsNull ? "(nil)" : Text(result);
        }

        var items = (RedisResult[])result!;
        return "[" + string.Join(", ", items.Select(static i => i.Resp2Type == ResultType.Array ? Nested(i) : i.IsNull ? "(nil)" : Text(i))) + "]";
    }

    private static string Text(RedisResult result) => RedisText.Of((RedisValue)result);

    private static IEnumerable<QueryEvent> Table(IReadOnlyList<ResultColumn> columns, List<object?[]> rows, int? maxRows)
    {
        var shown = maxRows is { } max && rows.Count > max ? rows.Take(max).ToList() : rows;
        return
        [
            new ResultSetStarted(columns),
            new RowBatch(shown),
            new ResultSetCompleted(shown.Count, shown.Count < rows.Count),
        ];
    }
}

/// <summary>A value as text: UTF-8 when it is, else the bytes in hexadecimal (<c>0x…</c>).</summary>
internal static class RedisText
{
    private const int MaxHexBytes = 4096;

    public static string Of(RedisValue value)
    {
        if (value.IsNull)
        {
            return string.Empty;
        }

        if (value.IsInteger || value.IsNullOrEmpty)
        {
            return value.ToString();
        }

        var bytes = (byte[]?)value ?? [];
        if (System.Text.Unicode.Utf8.IsValid(bytes))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        return "0x" + Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, MaxHexBytes)) + (bytes.Length > MaxHexBytes ? "…" : string.Empty);
    }
}
