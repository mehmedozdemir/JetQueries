using System.Text;
using JetQueries.Providers.Metadata;
using JetQueries.Sdk;

namespace JetQueries.Provider.PostgreSql;

/// <summary>
/// The "Execute" script of a PostgreSQL routine: <c>CALL</c> for a procedure (its OUT parameters come back as a
/// row), <c>SELECT * FROM</c> for a function (OUT parameters are its result columns, not arguments). Arguments are
/// named (<c>p =&gt; value</c>) so a default can be left out; unnamed parameters are passed by position.
/// </summary>
internal static class PostgreSqlExecuteScript
{
    private static readonly HashSet<string> NumericTypes = new(StringComparer.Ordinal)
    {
        "SMALLINT", "INTEGER", "INT", "INT2", "INT4", "INT8", "BIGINT", "NUMERIC", "DECIMAL", "REAL", "DOUBLE PRECISION", "FLOAT4", "FLOAT8", "MONEY",
    };

    public static string? Build(DatabaseObject routine, SqlSignature signature, IReadOnlyList<RoutineArgument> arguments)
    {
        ArgumentNullException.ThrowIfNull(routine);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(arguments);
        var name = (routine.Name.Schema is { Length: > 0 } schema ? PostgreSqlNames.Quote(schema) + "." : string.Empty) + PostgreSqlNames.Quote(routine.Name.Name);
        var isProcedure = routine.Kind == DatabaseObjectKinds.Procedure;

        // A function's OUT parameters are not passed; a procedure's are, as NULL (PostgreSQL 14 and later).
        var passed = signature.Parameters.Where(p => isProcedure || p.Direction != "OUT").ToList();
        var named = passed.All(static p => !string.IsNullOrEmpty(p.Name));
        var values = new List<string>();
        foreach (var parameter in passed)
        {
            var argument = parameter.Direction == "OUT" ? new RoutineArgument(parameter.Name, RoutineArgumentKind.Null) : RoutineScriptText.ArgumentFor(arguments, parameter);
            var value = Literal(parameter, argument);
            if (value is null && named)
            {
                continue;
            }

            values.Add(named ? $"{PostgreSqlNames.Quote(parameter.Name)} => {value ?? "NULL"}" : value ?? "NULL");
        }

        // By position, trailing defaults can still be left out.
        if (!named)
        {
            for (var i = passed.Count - 1; i >= 0 && Literal(passed[i], RoutineScriptText.ArgumentFor(arguments, passed[i])) is null; i--)
            {
                values.RemoveAt(values.Count - 1);
            }
        }

        var call = new StringBuilder(name).Append('(');
        if (values.Count > 0)
        {
            call.Append("\n    ").AppendJoin(",\n    ", values).Append('\n');
        }

        call.Append(')');
        return isProcedure ? "CALL " + call + ";\n" : "SELECT *\nFROM " + call + ";\n";
    }

    // The argument as SQL; null for "use the default".
    private static string? Literal(SqlParameterInfo parameter, RoutineArgument argument)
    {
        switch (argument.Kind)
        {
            case RoutineArgumentKind.Default:
                return null;
            case RoutineArgumentKind.Null:
                return "NULL";
            case RoutineArgumentKind.Expression:
                return string.IsNullOrWhiteSpace(argument.Text) ? "NULL" : argument.Text.Trim();
        }

        var text = argument.Text ?? string.Empty;
        var value = text.Trim();
        var type = RoutineScriptText.BaseType(parameter.DataType);
        if (NumericTypes.Contains(type))
        {
            return RoutineScriptText.IsNumber(value) ? value : value.Length == 0 ? "NULL" : RoutineScriptText.Quote(text);
        }

        if (type is "BOOLEAN" or "BOOL" && bool.TryParse(value, out var flag))
        {
            return flag ? "true" : "false";
        }

        return RoutineScriptText.Quote(text);
    }
}
