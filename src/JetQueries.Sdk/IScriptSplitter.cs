namespace JetQueries.Sdk;

/// <summary>
/// Optional interface of an <see cref="IDatabaseProvider"/> whose tools split a script into batches on the
/// client (SQL Server's <c>GO</c>, SQL*Plus's <c>/</c>). The host sends each batch to the
/// <see cref="IQueryExecutor"/> separately, in order.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0. The method is pure and must not do I/O.</remarks>
public interface IScriptSplitter
{
    /// <summary>Splits a script into the batches to send. Separator lines are not part of any batch.</summary>
    /// <param name="script">The whole text the user asked to run.</param>
    /// <returns>The batches in order; batches that are empty or only white space are left out.</returns>
    IReadOnlyList<ScriptBatch> SplitBatches(string script);
}

/// <summary>One batch of a script.</summary>
/// <param name="Text">The text to send.</param>
/// <param name="StartLine">Zero-based line of the script on which <paramref name="Text"/> starts, for mapping message lines back.</param>
/// <param name="RepeatCount">How many times to run the batch (<c>GO 5</c>); at least 1.</param>
public sealed record ScriptBatch(string Text, int StartLine, int RepeatCount = 1);
