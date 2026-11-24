using System.Text;
using JetQueries.Providers.Metadata;
using JetQueries.Sdk;

namespace JetQueries.Provider.SqlServer;

/// <summary>
/// The "Execute" script of a SQL Server routine, as SSMS writes it: a procedure through
/// <c>EXEC @return_value = …</c> with a variable per OUTPUT parameter, selected afterwards; a scalar function in a
/// SELECT; a table-valued function in a FROM.
/// </summary>
internal static class SqlServerExecuteScript
{
    private static readonly HashSet<string> NumericTypes = new(StringComparer.Ordinal)
    {
        "TINYINT", "SMALLINT", "INT", "BIGINT", "DECIMAL", "NUMERIC", "MONEY", "SMALLMONEY", "FLOAT", "REAL", "BIT",
    };

    private static readonly HashSet<string> UnicodeTypes = new(StringComparer.Ordinal) { "NCHAR", "NVARCHAR", "NTEXT", "XML", "SYSNAME" };

    public static string? Build(DatabaseObject routine, SqlSignature signature, IReadOnlyList<RoutineArgument> arguments)
    {
        ArgumentNullException.ThrowIfNull(routine);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(arguments);
        var name = (routine.Name.Schema is { Length: > 0 } schema ? SqlServerNames.Quote(schema) + "." : string.Empty) + SqlServerNames.Quote(routine.Name.Name);

        if (routine.Kind == DatabaseObjectKinds.Function)
        {
            // Functions take their arguments by position; DEFAULT keeps a parameter's default.
            var values = signature.Parameters.Select(p => Literal(p, RoutineScriptText.ArgumentFor(arguments, p)) ?? "DEFAULT");
            var call = name + "(" + string.Join(", ", values) + ")";
            return string.Equals(signature.ReturnType, "table", StringComparison.OrdinalIgnoreCase)
                ? "SELECT *\nFROM " + call + ";\n"
                : "SELECT " + call + " AS [Result];\n";
        }

        var text = new StringBuilder("DECLARE @return_value int;\n");
        var outputs = signature.Parameters.Where(RoutineScriptText.IsOutput).ToList();
        foreach (var parameter in outputs)
        {
            var start = Literal(parameter, RoutineScriptText.ArgumentFor(arguments, parameter));
            text.Append("DECLARE ").Append(Variable(parameter)).Append(' ').Append(parameter.DataType ?? "sql_variant")
                .Append(start is null or "NULL" ? string.Empty : " = " + start).Append(";\n");
        }

        text.Append("\nEXEC @return_value = ").Append(name);
        var passed = new List<string>();
        foreach (var parameter in signature.Parameters)
        {
            var argument = RoutineScriptText.ArgumentFor(arguments, parameter);
            var at = "@" + RoutineScriptText.Plain(parameter.Name);
            if (RoutineScriptText.IsOutput(parameter))
            {
                passed.Add($"{at} = {Variable(parameter)} OUTPUT");
            }
            else if (Literal(parameter, argument) is { } value)
            {
                passed.Add($"{at} = {value}");
            }
        }

        if (passed.Count > 0)
        {
            text.Append("\n    ").AppendJoin(",\n    ", passed);
        }

        text.Append(";\n\nSELECT @return_value AS [Return Value]");
        foreach (var parameter in outputs)
        {
            text.Append(", ").Append(Variable(parameter)).Append(" AS ").Append(SqlServerNames.Quote("@" + RoutineScriptText.Plain(parameter.Name)));
        }

        return text.Append(";\n").ToString();
    }

    private static string Variable(SqlParameterInfo parameter) => "@" + RoutineScriptText.Identifier(parameter.Name);

    // The argument as T-SQL; null for "use the default" (the argument is left out).
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
        var type = RoutineScriptText.BaseType(parameter.DataType);
        if (type == "BIT" && bool.TryParse(text.Trim(), out var flag))
        {
            return flag ? "1" : "0";
        }

        if (NumericTypes.Contains(type))
        {
            return RoutineScriptText.IsNumber(text) ? text.Trim() : text.Trim().Length == 0 ? "NULL" : RoutineScriptText.Quote(text);
        }

        if (type is "BINARY" or "VARBINARY" or "IMAGE" && text.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return text.Trim();
        }

        return RoutineScriptText.Quote(text, UnicodeTypes.Contains(type) ? "N" : string.Empty);
    }
}
