using JetQueries.Core.Sql;
using JetQueries.Sdk;

namespace JetQueries.Core.Queries;

/// <summary>How a recorded run ended.</summary>
public enum QueryOutcome
{
    /// <summary>It ran to the end.</summary>
    Succeeded,

    /// <summary>The database reported an error.</summary>
    Failed,

    /// <summary>The user canceled it.</summary>
    Canceled,
}

/// <summary>One run of a query, as kept in the history.</summary>
/// <param name="Id">Identity in the store; 0 for an entry that has not been saved yet.</param>
/// <param name="ExecutedAt">When the run started.</param>
/// <param name="ConnectionId">The saved connection it ran on.</param>
/// <param name="ConnectionName">The connection's name at the time, kept so that the entry still reads well after the connection is renamed or deleted.</param>
/// <param name="ProviderId">The database type, for choosing the language when the text is opened again.</param>
/// <param name="Sql">The text that was sent, with password literals removed (see <see cref="SecretRedactor"/>).</param>
/// <param name="Duration">How long it ran.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="RowCount">Rows returned, if it returned any result set.</param>
/// <param name="Error">The database's message when it failed.</param>
public sealed record QueryHistoryEntry(
    long Id,
    DateTimeOffset ExecutedAt,
    Guid ConnectionId,
    string ConnectionName,
    string ProviderId,
    string Sql,
    TimeSpan Duration,
    QueryOutcome Outcome,
    long? RowCount = null,
    string? Error = null);

/// <summary>The queries the user has run, newest first. Stored on this machine only.</summary>
public interface IQueryHistory
{
    /// <summary>Raised, on an arbitrary thread, after entries were added or removed.</summary>
    event EventHandler? Changed;

    /// <summary>Records a run. Password literals in the text are replaced before it is stored.</summary>
    /// <param name="entry">The run; its <see cref="QueryHistoryEntry.Id"/> is ignored.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task AddAsync(QueryHistoryEntry entry, CancellationToken cancellationToken);

    /// <summary>Returns entries, newest first.</summary>
    /// <param name="search">Text that must occur in the SQL or the connection name (case-insensitive); <see langword="null"/> or empty for all.</param>
    /// <param name="limit">Most entries to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<QueryHistoryEntry>> SearchAsync(string? search, int limit, CancellationToken cancellationToken);

    /// <summary>Removes one entry.</summary>
    /// <param name="id">The entry's identity.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task DeleteAsync(long id, CancellationToken cancellationToken);

    /// <summary>Removes every entry.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>Where the query history is kept and how much of it.</summary>
public sealed class QueryHistoryOptions
{
    /// <summary>Path of the SQLite file; <see langword="null"/> keeps the history in memory only.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>Most entries kept; the oldest are dropped beyond this.</summary>
    public int MaxEntries { get; set; } = 10_000;

    /// <summary>Longest text kept per entry, in characters; a longer script is cut and marked.</summary>
    public int MaxSqlLength { get; set; } = 100_000;
}

/// <summary>
/// Removes password literals from SQL before it is written to disk: <c>IDENTIFIED BY x</c>,
/// <c>IDENTIFIED BY VALUES 'x'</c>, <c>PASSWORD = 'x'</c>, <c>WITH PASSWORD = 'x'</c>, <c>OLD_PASSWORD = 'x'</c>.
/// It recognizes these forms and nothing else: a secret passed any other way (as an ordinary column value, a
/// procedure argument) is not detected, so the history file must still be treated as private.
/// </summary>
public static class SecretRedactor
{
    private const string Mask = "'***'";

    /// <summary>Returns <paramref name="sql"/> with recognized password literals replaced.</summary>
    /// <param name="sql">The SQL text.</param>
    public static string Redact(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        // Cheap exit for the overwhelming majority of statements.
        if (!sql.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) && !sql.Contains("IDENTIFIED", StringComparison.OrdinalIgnoreCase))
        {
            return sql;
        }

        var tokens = new List<(SqlTokenKind Kind, int Start, int Length)>();
        var state = BasicSqlLanguageService.StateNormal;
        var lineStart = 0;
        while (lineStart <= sql.Length)
        {
            var newLine = sql.IndexOf('\n', lineStart);
            var lineEnd = newLine < 0 ? sql.Length : newLine;
            var result = BasicSqlLanguageService.Instance.TokenizeLine(sql.AsSpan(lineStart, lineEnd - lineStart), state);
            state = result.EndState;
            foreach (var token in result.Tokens)
            {
                if (token.Kind != SqlTokenKind.Comment)
                {
                    tokens.Add((token.Kind, lineStart + token.Span.Start, token.Span.Length));
                }
            }

            if (newLine < 0)
            {
                break;
            }

            lineStart = newLine + 1;
        }

        var cuts = new List<(int Start, int Length)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var secret = -1;
            if (Is(i, "IDENTIFIED") && Is(i + 1, "BY"))
            {
                secret = Is(i + 2, "VALUES") ? i + 3 : i + 2;
            }
            else if (IsPasswordWord(i))
            {
                secret = Is(i + 1, "=") ? i + 2 : i + 1;

                // Only a literal after PASSWORD is a secret; "password_hash" in a column list is not.
                if (secret < tokens.Count && tokens[secret].Kind != SqlTokenKind.StringLiteral)
                {
                    secret = -1;
                }
            }

            if (secret >= 0 && secret < tokens.Count && tokens[secret].Kind is SqlTokenKind.StringLiteral or SqlTokenKind.Identifier or SqlTokenKind.NumberLiteral)
            {
                cuts.Add((tokens[secret].Start, tokens[secret].Length));
                i = secret;
            }
        }

        if (cuts.Count == 0)
        {
            return sql;
        }

        var redacted = new System.Text.StringBuilder(sql.Length);
        var position = 0;
        foreach (var (start, length) in cuts)
        {
            redacted.Append(sql, position, start - position).Append(Mask);
            position = start + length;
        }

        return redacted.Append(sql, position, sql.Length - position).ToString();

        bool Is(int index, string word) =>
            index >= 0 && index < tokens.Count && sql.AsSpan(tokens[index].Start, tokens[index].Length).Equals(word, StringComparison.OrdinalIgnoreCase);

        bool IsPasswordWord(int index) => Is(index, "PASSWORD") || Is(index, "OLD_PASSWORD") || Is(index, "PWD");
    }
}
