namespace JetQueries.Sdk;

/// <summary>
/// Optional for an <see cref="IDatabaseProvider"/> with stored procedures and functions: writes the script that
/// runs one with the values the user gave (the "Execute" dialog of the explorer and the details view), in the
/// provider's own form (SQL Server's <c>EXEC … OUTPUT</c>, an Oracle anonymous block, PostgreSQL's <c>CALL</c>).
/// Output parameters and a function's result are shown after the call.
/// </summary>
/// <remarks>Preview contract: may change until SDK 1.0. Pure: no I/O.</remarks>
public interface IRoutineExecutionScripting
{
    /// <summary>The script that runs <paramref name="routine"/>.</summary>
    /// <param name="routine">A procedure or function (an Oracle package member carries its package in <see cref="DatabaseObjectName.Container"/>).</param>
    /// <param name="signature">The overload to call, as <see cref="IRoutineMetadataProvider"/> gave it.</param>
    /// <param name="arguments">What to pass, by parameter name; a parameter missing here is passed as NULL.</param>
    /// <returns>The script, with the provider's statement terminators; <see langword="null"/> when the routine cannot be called this way.</returns>
    string? ScriptExecute(DatabaseObject routine, SqlSignature signature, IReadOnlyList<RoutineArgument> arguments);
}

/// <summary>What is passed for one parameter.</summary>
/// <param name="Name">The parameter's name, as in the signature.</param>
/// <param name="Kind">How <paramref name="Text"/> is used.</param>
/// <param name="Text">The value as the user typed it (a literal is made of it by the parameter's type), or an expression.</param>
/// <remarks>Preview.</remarks>
public sealed record RoutineArgument(string Name, RoutineArgumentKind Kind, string? Text = null);

/// <summary>How an argument is given.</summary>
/// <remarks>Preview.</remarks>
public enum RoutineArgumentKind
{
    /// <summary>A value: the provider writes it as a literal of the parameter's type (quoted text, a number…).</summary>
    Value,

    /// <summary>An SQL expression, written as it is (<c>GETDATE()</c>, <c>SYSDATE - 1</c>).</summary>
    Expression,

    /// <summary>NULL.</summary>
    Null,

    /// <summary>The parameter's default: the argument is left out (or written DEFAULT where the syntax needs a place).</summary>
    Default,
}
