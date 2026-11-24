using JetQueries.Sdk;

namespace JetQueries.Provider.Redis;

/// <summary>
/// The console's editor help: commands coloured as keywords, quoted strings and numbers; completion of command names
/// (with their arguments and what they do) at the start of a line.
/// </summary>
internal sealed class RedisLanguageService : ISqlLanguageService
{
    public SqlLineTokens TokenizeLine(ReadOnlySpan<char> line, int startState)
    {
        var tokens = new List<SqlToken>();
        var i = 0;
        var first = true;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                i++;
                continue;
            }

            var start = i;
            if (first && (line[i] == '#' || (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/')))
            {
                tokens.Add(new SqlToken(new TextSpan(start, line.Length - start), SqlTokenKind.Comment));
                break;
            }

            if (line[i] is '"' or '\'')
            {
                var quote = line[i++];
                while (i < line.Length && line[i] != quote)
                {
                    i += line[i] == '\\' && quote == '"' ? 2 : 1;
                }

                i = Math.Min(i + 1, line.Length);
                tokens.Add(new SqlToken(new TextSpan(start, i - start), SqlTokenKind.StringLiteral));
                first = false;
                continue;
            }

            while (i < line.Length && !char.IsWhiteSpace(line[i]))
            {
                i++;
            }

            var word = line[start..i];
            var kind = first ? SqlTokenKind.Keyword
                : double.TryParse(word, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) ? SqlTokenKind.NumberLiteral
                : IsOption(word) ? SqlTokenKind.Keyword
                : SqlTokenKind.Identifier;
            tokens.Add(new SqlToken(new TextSpan(start, i - start), kind));
            first = false;
        }

        return new SqlLineTokens(tokens, 0);
    }

    public Task<IReadOnlyList<SqlDiagnostic>> GetDiagnosticsAsync(SqlDocument document, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SqlDiagnostic>>([]);

    public Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(SqlDocument document, int offset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = document.Text;
        offset = Math.Clamp(offset, 0, text.Length);
        var lineStart = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        if (offset > 0 && text[offset - 1] == '\n')
        {
            lineStart = offset;
        }

        var before = text[lineStart..offset];
        var words = before.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var typing = before.Length > 0 && !char.IsWhiteSpace(before[^1]);
        var position = typing ? words.Length - 1 : words.Length;
        var replace = typing ? new TextSpan(offset - words[^1].Length, words[^1].Length) : new TextSpan(offset, 0);

        // A command at the start of a line; the second word of CONFIG, CLIENT, OBJECT… after it.
        IEnumerable<RedisCommand> commands = position switch
        {
            <= 0 => RedisCommands.All,
            1 => RedisCommands.All.Where(c => c.Name.StartsWith(words[0] + " ", StringComparison.OrdinalIgnoreCase)),
            _ => [],
        };
        IReadOnlyList<CompletionItem> items = [.. commands.Select(c => new CompletionItem(position == 1 ? c.Name[(c.Name.IndexOf(' ', StringComparison.Ordinal) + 1)..] : c.Name, CompletionItemKind.Keyword, replace)
        {
            Detail = c.Arguments.Length > 0 ? c.Name + " " + c.Arguments : c.Name,
            Documentation = c.Summary,
        })];
        return Task.FromResult(items);
    }

    public Task<SqlDefinition?> GetDefinitionAsync(SqlDocument document, int offset, CancellationToken cancellationToken) => Task.FromResult<SqlDefinition?>(null);

    private static bool IsOption(ReadOnlySpan<char> word) =>
        word is "MATCH" or "COUNT" or "TYPE" or "EX" or "PX" or "NX" or "XX" or "GT" or "LT" or "KEEPTTL" or "GET" or "WITHSCORES" or "WITHVALUES"
            or "LIMIT" or "BYSCORE" or "BYLEX" or "REV" or "STREAMS" or "MAXLEN" or "REPLACE" or "ASYNC" or "SYNC" or "BEFORE" or "AFTER" or "LEFT" or "RIGHT";
}
