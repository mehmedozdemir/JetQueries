using System.Globalization;
using System.Text;

namespace JetQueries.Provider.Redis;

/// <summary>What a command does to the data.</summary>
internal enum RedisCommandEffect
{
    /// <summary>Only reads.</summary>
    Reads,

    /// <summary>Changes data or settings.</summary>
    Writes,

    /// <summary>Deletes keys or whole databases: confirmed on a production connection.</summary>
    Destroys,

    /// <summary>Refused in the console: blocks the server or never returns (KEYS, MONITOR, SUBSCRIBE…).</summary>
    Refused,
}

/// <summary>A command of the table (CLAUDE.md: Redis → command table, no parser).</summary>
/// <param name="Name">The command, upper case (two words for container commands: <c>CONFIG GET</c>).</param>
/// <param name="Arguments">Its arguments as the documentation writes them.</param>
/// <param name="Summary">What it does.</param>
/// <param name="Effect">Read, write, destroy or refused.</param>
internal sealed record RedisCommand(string Name, string Arguments, string Summary, RedisCommandEffect Effect);

/// <summary>The commands the console knows: completion, colouring and what may run where.</summary>
internal static class RedisCommands
{
    private const RedisCommandEffect R = RedisCommandEffect.Reads;
    private const RedisCommandEffect W = RedisCommandEffect.Writes;
    private const RedisCommandEffect D = RedisCommandEffect.Destroys;
    private const RedisCommandEffect X = RedisCommandEffect.Refused;

    public static IReadOnlyList<RedisCommand> All { get; } =
    [
        // Keys
        new("DEL", "key [key ...]", "Deletes keys.", D),
        new("UNLINK", "key [key ...]", "Deletes keys in the background.", D),
        new("EXISTS", "key [key ...]", "How many of the keys exist.", R),
        new("TYPE", "key", "The type of a key's value.", R),
        new("TTL", "key", "Seconds until the key expires (-1: never, -2: no key).", R),
        new("PTTL", "key", "Milliseconds until the key expires.", R),
        new("EXPIRE", "key seconds [NX | XX | GT | LT]", "Sets a key's time to live.", W),
        new("PEXPIRE", "key milliseconds", "Sets a key's time to live in milliseconds.", W),
        new("EXPIREAT", "key unix-time-seconds", "Expires a key at a time.", W),
        new("PERSIST", "key", "Removes a key's time to live.", W),
        new("RENAME", "key newkey", "Renames a key (overwrites newkey).", W),
        new("RENAMENX", "key newkey", "Renames a key unless newkey exists.", W),
        new("COPY", "source destination [DB db] [REPLACE]", "Copies a key.", W),
        new("SCAN", "cursor [MATCH pattern] [COUNT count] [TYPE type]", "Lists keys a step at a time.", R),
        new("KEYS", "pattern", "Refused: blocks the server on a large database. Use SCAN.", X),
        new("RANDOMKEY", string.Empty, "A random key.", R),
        new("DUMP", "key", "A serialized copy of the value.", R),
        new("OBJECT ENCODING", "key", "How the value is stored.", R),
        new("OBJECT FREQ", "key", "The key's access frequency.", R),
        new("OBJECT IDLETIME", "key", "Seconds since the key was used.", R),
        new("TOUCH", "key [key ...]", "Marks keys as used.", W),
        new("MEMORY USAGE", "key [SAMPLES count]", "Bytes the key takes.", R),

        // Strings
        new("GET", "key", "A string's value.", R),
        new("SET", "key value [NX | XX] [GET] [EX seconds | PX milliseconds | KEEPTTL]", "Sets a string.", W),
        new("SETNX", "key value", "Sets a string unless the key exists.", W),
        new("SETEX", "key seconds value", "Sets a string with a time to live.", W),
        new("GETDEL", "key", "Gets a string and deletes the key.", D),
        new("GETEX", "key [EX seconds | PX milliseconds | PERSIST]", "Gets a string and sets its time to live.", W),
        new("GETSET", "key value", "Sets a string and returns the old value.", W),
        new("MGET", "key [key ...]", "Several strings' values.", R),
        new("MSET", "key value [key value ...]", "Sets several strings.", W),
        new("APPEND", "key value", "Appends to a string.", W),
        new("STRLEN", "key", "A string's length.", R),
        new("GETRANGE", "key start end", "Part of a string.", R),
        new("SETRANGE", "key offset value", "Overwrites part of a string.", W),
        new("INCR", "key", "Adds 1 to a number.", W),
        new("INCRBY", "key increment", "Adds to a number.", W),
        new("INCRBYFLOAT", "key increment", "Adds to a decimal number.", W),
        new("DECR", "key", "Subtracts 1 from a number.", W),
        new("DECRBY", "key decrement", "Subtracts from a number.", W),

        // Hashes
        new("HGET", "key field", "A hash field's value.", R),
        new("HGETALL", "key", "Every field and value of a hash.", R),
        new("HMGET", "key field [field ...]", "Several fields' values.", R),
        new("HSET", "key field value [field value ...]", "Sets hash fields.", W),
        new("HSETNX", "key field value", "Sets a field unless it exists.", W),
        new("HDEL", "key field [field ...]", "Deletes hash fields.", W),
        new("HEXISTS", "key field", "Whether a field exists.", R),
        new("HKEYS", "key", "A hash's fields.", R),
        new("HVALS", "key", "A hash's values.", R),
        new("HLEN", "key", "How many fields a hash has.", R),
        new("HINCRBY", "key field increment", "Adds to a field's number.", W),
        new("HSCAN", "key cursor [MATCH pattern] [COUNT count]", "Lists fields a step at a time.", R),
        new("HRANDFIELD", "key [count [WITHVALUES]]", "Random fields.", R),

        // Lists
        new("LRANGE", "key start stop", "A range of a list.", R),
        new("LLEN", "key", "A list's length.", R),
        new("LINDEX", "key index", "An element by index.", R),
        new("LPUSH", "key element [element ...]", "Adds to the head of a list.", W),
        new("RPUSH", "key element [element ...]", "Adds to the tail of a list.", W),
        new("LPOP", "key [count]", "Removes from the head of a list.", W),
        new("RPOP", "key [count]", "Removes from the tail of a list.", W),
        new("LSET", "key index element", "Sets an element by index.", W),
        new("LREM", "key count element", "Removes elements by value.", W),
        new("LINSERT", "key BEFORE | AFTER pivot element", "Inserts an element.", W),
        new("LTRIM", "key start stop", "Keeps only a range of a list.", W),
        new("LPOS", "key element [RANK rank] [COUNT num-matches]", "Where an element is.", R),
        new("LMOVE", "source destination LEFT | RIGHT LEFT | RIGHT", "Moves an element between lists.", W),
        new("BLPOP", "key [key ...] timeout", "Refused: waits for an element.", X),
        new("BRPOP", "key [key ...] timeout", "Refused: waits for an element.", X),
        new("BLMOVE", "source destination LEFT | RIGHT LEFT | RIGHT timeout", "Refused: waits for an element.", X),

        // Sets
        new("SMEMBERS", "key", "A set's members.", R),
        new("SADD", "key member [member ...]", "Adds set members.", W),
        new("SREM", "key member [member ...]", "Removes set members.", W),
        new("SISMEMBER", "key member", "Whether a member is in a set.", R),
        new("SMISMEMBER", "key member [member ...]", "Whether members are in a set.", R),
        new("SCARD", "key", "How many members a set has.", R),
        new("SRANDMEMBER", "key [count]", "Random members.", R),
        new("SPOP", "key [count]", "Removes random members.", W),
        new("SMOVE", "source destination member", "Moves a member between sets.", W),
        new("SINTER", "key [key ...]", "Members in every set.", R),
        new("SUNION", "key [key ...]", "Members in any set.", R),
        new("SDIFF", "key [key ...]", "Members of the first set only.", R),
        new("SSCAN", "key cursor [MATCH pattern] [COUNT count]", "Lists members a step at a time.", R),

        // Sorted sets
        new("ZRANGE", "key start stop [BYSCORE | BYLEX] [REV] [LIMIT offset count] [WITHSCORES]", "A range of a sorted set.", R),
        new("ZREVRANGE", "key start stop [WITHSCORES]", "A range, highest first.", R),
        new("ZRANGEBYSCORE", "key min max [WITHSCORES] [LIMIT offset count]", "Members by score.", R),
        new("ZADD", "key [NX | XX] [GT | LT] [CH] [INCR] score member [score member ...]", "Adds members with scores.", W),
        new("ZREM", "key member [member ...]", "Removes members.", W),
        new("ZSCORE", "key member", "A member's score.", R),
        new("ZINCRBY", "key increment member", "Adds to a member's score.", W),
        new("ZCARD", "key", "How many members a sorted set has.", R),
        new("ZCOUNT", "key min max", "Members with scores in a range.", R),
        new("ZRANK", "key member [WITHSCORE]", "A member's rank.", R),
        new("ZREVRANK", "key member [WITHSCORE]", "A member's rank, highest first.", R),
        new("ZPOPMIN", "key [count]", "Removes the lowest members.", W),
        new("ZPOPMAX", "key [count]", "Removes the highest members.", W),
        new("ZREMRANGEBYSCORE", "key min max", "Removes members by score.", W),
        new("ZSCAN", "key cursor [MATCH pattern] [COUNT count]", "Lists members a step at a time.", R),

        // Streams
        new("XADD", "key [MAXLEN [= | ~] threshold] * | id field value [field value ...]", "Adds an entry.", W),
        new("XRANGE", "key start end [COUNT count]", "Entries by id.", R),
        new("XREVRANGE", "key end start [COUNT count]", "Entries by id, newest first.", R),
        new("XLEN", "key", "How many entries a stream has.", R),
        new("XDEL", "key id [id ...]", "Deletes entries.", W),
        new("XTRIM", "key MAXLEN | MINID [= | ~] threshold", "Trims a stream.", W),
        new("XINFO STREAM", "key [FULL]", "About a stream.", R),
        new("XINFO GROUPS", "key", "A stream's consumer groups.", R),
        new("XREAD", "[COUNT count] STREAMS key [key ...] id [id ...]", "Reads entries (without BLOCK).", R),

        // Server
        new("PING", "[message]", "Checks the connection.", R),
        new("ECHO", "message", "Returns the message.", R),
        new("INFO", "[section [section ...]]", "About the server.", R),
        new("DBSIZE", string.Empty, "How many keys the database has.", R),
        new("SELECT", "index", "Switches this window to a database.", R),
        new("TIME", string.Empty, "The server's time.", R),
        new("LASTSAVE", string.Empty, "When the database was last saved.", R),
        new("ROLE", string.Empty, "The server's replication role.", R),
        new("CLIENT LIST", string.Empty, "The connected clients.", R),
        new("CLIENT INFO", string.Empty, "This connection.", R),
        new("CLIENT KILL", "ID client-id", "Closes a client's connection.", W),
        new("CONFIG GET", "parameter [parameter ...]", "Server settings.", R),
        new("CONFIG SET", "parameter value", "Changes a server setting.", W),
        new("SLOWLOG GET", "[count]", "The slowest recent commands.", R),
        new("SLOWLOG LEN", string.Empty, "How many entries the slow log has.", R),
        new("SLOWLOG RESET", string.Empty, "Empties the slow log.", W),
        new("MEMORY STATS", string.Empty, "Memory use of the server.", R),
        new("LATENCY LATEST", string.Empty, "Latest latency spikes.", R),
        new("COMMAND COUNT", string.Empty, "How many commands the server has.", R),
        new("COMMAND DOCS", "[command-name ...]", "Documentation of commands.", R),
        new("FLUSHDB", "[ASYNC | SYNC]", "Deletes every key of the database.", D),
        new("FLUSHALL", "[ASYNC | SYNC]", "Deletes every key of every database.", D),
        new("SAVE", string.Empty, "Saves the data (blocks the server).", W),
        new("BGSAVE", string.Empty, "Saves the data in the background.", W),
        new("PUBLISH", "channel message", "Sends a message to a channel.", W),
        new("EVAL", "script numkeys [key ...] [arg ...]", "Runs a Lua script.", W),
        new("EVALSHA", "sha1 numkeys [key ...] [arg ...]", "Runs a cached Lua script.", W),
        new("MONITOR", string.Empty, "Refused: streams every command for ever.", X),
        new("SUBSCRIBE", "channel [channel ...]", "Refused: waits for messages.", X),
        new("PSUBSCRIBE", "pattern [pattern ...]", "Refused: waits for messages.", X),
        new("SSUBSCRIBE", "shardchannel [shardchannel ...]", "Refused: waits for messages.", X),
        new("SHUTDOWN", "[NOSAVE | SAVE]", "Refused: stops the server.", X),
        new("DEBUG", "subcommand", "Refused: can crash or block the server.", X),
        new("MULTI", string.Empty, "Refused in the console: each line runs on its own.", X),
        new("WAIT", "numreplicas timeout", "Refused: waits for replicas.", X),
        new("QUIT", string.Empty, "Refused: closes the connection.", X),
    ];

    private static readonly Dictionary<string, RedisCommand> ByName = All.ToDictionary(static c => c.Name, StringComparer.OrdinalIgnoreCase);

    // Container commands whose second word is part of the name.
    private static readonly HashSet<string> Containers = new(All.Where(static c => c.Name.Contains(' ', StringComparison.Ordinal)).Select(static c => c.Name.Split(' ')[0]), StringComparer.OrdinalIgnoreCase);

    /// <summary>The command a line starts with: one word, or two for CONFIG GET, CLIENT LIST…; null when not in the table.</summary>
    public static RedisCommand? Find(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return null;
        }

        if (words.Count > 1 && Containers.Contains(words[0]) && ByName.TryGetValue(words[0] + " " + words[1], out var two))
        {
            return two;
        }

        return ByName.GetValueOrDefault(words[0]);
    }

    /// <summary>What a command line does; unknown commands count as writing (the guard errs on the side of caution).</summary>
    public static RedisCommandEffect EffectOf(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return RedisCommandEffect.Reads;
        }

        // XREAD … BLOCK waits; SET … GET reads too but still writes.
        if (words[0].Equals("XREAD", StringComparison.OrdinalIgnoreCase) && words.Any(static w => w.Equals("BLOCK", StringComparison.OrdinalIgnoreCase)))
        {
            return RedisCommandEffect.Refused;
        }

        return Find(words)?.Effect ?? (Containers.Contains(words[0]) && words[0] is not ("CONFIG" or "CLIENT") ? RedisCommandEffect.Reads : RedisCommandEffect.Writes);
    }

    /// <summary>The name of a command as written in messages (upper case, its container word too).</summary>
    public static string NameOf(IReadOnlyList<string> words) => Find(words)?.Name ?? (words.Count > 0 ? words[0].ToUpperInvariant() : string.Empty);
}

/// <summary>One command of the console text: its words, what is sent, and its line.</summary>
/// <param name="Words">The command and its arguments, unquoted, as text (for the command table and messages).</param>
/// <param name="Values">What is sent: the text, or bytes when a word has <c>\xHH</c> escapes.</param>
/// <param name="Line">One-based line.</param>
internal sealed record RedisCommandLine(IReadOnlyList<string> Words, IReadOnlyList<object> Values, int Line);

/// <summary>
/// Reads console text as redis-cli does: one command per line, words separated by spaces, <c>"…"</c> with
/// backslash escapes (<c>\n</c>, <c>\t</c>, <c>\"</c>, <c>\xHH</c> for a raw byte) and <c>'…'</c> taken as written;
/// lines starting with <c>#</c> or <c>//</c> are comments.
/// </summary>
internal static class RedisCommandParser
{
    public static IReadOnlyList<RedisCommandLine> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var commands = new List<RedisCommandLine>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var words = Words(line, i + 1);
            if (words.Count > 0)
            {
                commands.Add(new RedisCommandLine([.. words.Select(static w => w.Text)], [.. words.Select(static w => w.Value)], i + 1));
            }
        }

        return commands;
    }

    /// <summary>The words of one line: as text, and as sent (bytes when there were <c>\xHH</c> escapes).</summary>
    public static List<(string Text, object Value)> Words(string line, int lineNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(line);
        var words = new List<(string, object)>();
        var i = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                i++;
                continue;
            }

            var bytes = new List<byte>();
            var raw = false;
            void Add(string part) => bytes.AddRange(Encoding.UTF8.GetBytes(part));
            while (i < line.Length && !char.IsWhiteSpace(line[i]))
            {
                var c = line[i];
                if (c == '"')
                {
                    i++;
                    var closed = false;
                    while (i < line.Length)
                    {
                        var q = line[i];
                        if (q == '"')
                        {
                            closed = true;
                            i++;
                            break;
                        }

                        if (q == '\\' && i + 1 < line.Length)
                        {
                            var e = line[i + 1];
                            if (e == 'x' && i + 3 < line.Length && byte.TryParse(line.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                            {
                                bytes.Add(b);
                                raw = true;
                                i += 4;
                                continue;
                            }

                            Add((e switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'a' => '\a', _ => e }).ToString());
                            i += 2;
                            continue;
                        }

                        // A surrogate pair stays together.
                        var length = char.IsHighSurrogate(q) && i + 1 < line.Length ? 2 : 1;
                        Add(line.Substring(i, length));
                        i += length;
                    }

                    if (!closed)
                    {
                        throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"Line {lineNumber}: a quote is not closed."));
                    }
                }
                else if (c == '\'')
                {
                    var close = line.IndexOf('\'', i + 1);
                    if (close < 0)
                    {
                        throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"Line {lineNumber}: a quote is not closed."));
                    }

                    Add(line[(i + 1)..close]);
                    i = close + 1;
                }
                else
                {
                    var start = i;
                    while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] is not ('"' or '\''))
                    {
                        i++;
                    }

                    Add(line[start..i]);
                }
            }

            var array = bytes.ToArray();
            var text = Encoding.UTF8.GetString(array);
            words.Add((text, raw ? array : text));
        }

        return words;
    }
}
