namespace JetQueries.Sdk;

/// <summary>
/// Optional for an <see cref="IDatabaseProvider"/> whose language is not SQL (Redis commands): tells the host what a
/// text would do, so that read-only connections refuse writes and production connections confirm destructive
/// commands. Without it the host reads the text as SQL.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0. Pure: no I/O.</remarks>
public interface IStatementRiskAnalyzer
{
    /// <summary>Classifies the text about to be sent.</summary>
    /// <param name="text">The text; may hold several commands.</param>
    StatementRiskInfo AnalyzeRisk(string text);
}

/// <summary>What a text would do.</summary>
/// <param name="WritesData">It can change data, structure or settings.</param>
/// <param name="DestructiveActions">What a production connection confirms first (<c>FLUSHALL</c>, <c>DEL</c>…), each once.</param>
public sealed record StatementRiskInfo(bool WritesData, IReadOnlyList<string> DestructiveActions);
