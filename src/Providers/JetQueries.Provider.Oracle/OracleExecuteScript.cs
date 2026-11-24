using System.Globalization;
using System.Text;
using JetQueries.Providers.Metadata;
using JetQueries.Sdk;

namespace JetQueries.Provider.Oracle;

/// <summary>
/// The "Execute" script of an Oracle routine (standalone or in a package): an anonymous block that calls it with
/// named arguments, a variable for each OUT parameter and the function's result, and prints them with
/// DBMS_OUTPUT (a ref cursor is returned as a result set with DBMS_SQL.RETURN_RESULT).
/// </summary>
internal static class OracleExecuteScript
{
    private static readonly HashSet<string> NumericTypes = new(StringComparer.Ordinal)
    {
        "NUMBER", "INTEGER", "FLOAT", "BINARY_FLOAT", "BINARY_DOUBLE", "BINARY_INTEGER", "PLS_INTEGER", "NATURAL", "POSITIVE", "SIMPLE_INTEGER",
    };

    private static readonly HashSet<string> Printable = new(StringComparer.Ordinal)
    {
        "NUMBER", "INTEGER", "FLOAT", "BINARY_FLOAT", "BINARY_DOUBLE", "BINARY_INTEGER", "PLS_INTEGER", "VARCHAR2", "NVARCHAR2", "CHAR",
        "NCHAR", "DATE", "CLOB", "NCLOB", "LONG",
    };

    public static string? Build(DatabaseObject routine, SqlSignature signature, IReadOnlyList<RoutineArgument> arguments)
    {
        ArgumentNullException.ThrowIfNull(routine);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(arguments);
        var name = string.Join('.', new[] { routine.Name.Schema, routine.Name.Container, routine.Name.Name }.OfType<string>().Select(OracleNames.Quote));
        var isFunction = routine.Kind == DatabaseObjectKinds.Function || signature.ReturnType is not null;
        var outputs = signature.Parameters.Where(RoutineScriptText.IsOutput).ToList();

        var text = new StringBuilder("DECLARE\n");
        if (isFunction)
        {
            text.Append("    v_result ").Append(Declared(signature.ReturnType)).Append(";\n");
        }

        foreach (var parameter in outputs)
        {
            var start = Literal(parameter, RoutineScriptText.ArgumentFor(arguments, parameter));
            text.Append("    ").Append(Variable(parameter)).Append(' ').Append(Declared(parameter.DataType))
                .Append(start is null or "NULL" ? string.Empty : " := " + start).Append(";\n");
        }

        text.Append("BEGIN\n    ").Append(isFunction ? "v_result := " : string.Empty).Append(name);
        var passed = new List<string>();
        foreach (var parameter in signature.Parameters)
        {
            var argument = RoutineScriptText.ArgumentFor(arguments, parameter);
            if (RoutineScriptText.IsOutput(parameter))
            {
                passed.Add($"{parameter.Name} => {Variable(parameter)}");
            }
            else if (Literal(parameter, argument) is { } value)
            {
                passed.Add($"{parameter.Name} => {value}");
            }
        }

        if (passed.Count > 0)
        {
            text.Append("(\n        ").AppendJoin(",\n        ", passed).Append("\n    )");
        }

        text.Append(";\n");
        if (isFunction)
        {
            text.Append(Show("Result", "v_result", signature.ReturnType));
        }

        foreach (var parameter in outputs)
        {
            text.Append(Show(parameter.Name, Variable(parameter), parameter.DataType));
        }

        return text.Append("END;\n/\n").ToString();
    }

    private static string Variable(SqlParameterInfo parameter) => "v_" + RoutineScriptText.Identifier(parameter.Name).ToLowerInvariant();

    // A variable's type: lengths for the types that need one; a ref cursor as SYS_REFCURSOR.
    private static string Declared(string? dataType)
    {
        var type = RoutineScriptText.BaseType(dataType);
        return type switch
        {
            "" => "VARCHAR2(32767)",
            "VARCHAR2" or "NVARCHAR2" => type + "(32767)",
            "CHAR" or "NCHAR" => type + "(2000)",
            "RAW" => "RAW(2000)",
            "REF CURSOR" => "SYS_REFCURSOR",
            "PL/SQL BOOLEAN" => "BOOLEAN",
            _ => dataType!.Trim(),
        };
    }

    // How a variable is shown after the call: printed, returned as a result set (a cursor) or only mentioned.
    private static string Show(string label, string variable, string? dataType)
    {
        var type = RoutineScriptText.BaseType(dataType);
        var caption = RoutineScriptText.Quote(label + " = ");
        if (type is "REF CURSOR" or "SYS_REFCURSOR")
        {
            return $"    DBMS_SQL.RETURN_RESULT({variable}); -- {label}: shown as a result set\n";
        }

        if (type is "BOOLEAN" or "PL/SQL BOOLEAN")
        {
            return $"    DBMS_OUTPUT.PUT_LINE({caption} || CASE WHEN {variable} THEN 'TRUE' WHEN NOT {variable} THEN 'FALSE' ELSE 'NULL' END);\n";
        }

        return Printable.Contains(type) || type.StartsWith("TIMESTAMP", StringComparison.Ordinal)
            ? $"    DBMS_OUTPUT.PUT_LINE({caption} || {variable});\n"
            : $"    -- {label} ({dataType}) is in {variable}\n";
    }

    // The argument as PL/SQL; null for "use the default" (the named argument is left out).
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

        if (type is "BOOLEAN" or "PL/SQL BOOLEAN" && bool.TryParse(value, out var flag))
        {
            return flag ? "TRUE" : "FALSE";
        }

        // ISO dates and times are written as typed literals; anything else is left to the session's formats.
        if (type == "DATE" && DateTime.TryParseExact(value, ["yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return "TO_DATE(" + RoutineScriptText.Quote(date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) + ", 'YYYY-MM-DD HH24:MI:SS')";
        }

        if (type.StartsWith("TIMESTAMP", StringComparison.Ordinal) &&
            DateTime.TryParseExact(value, ["yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
        {
            return "TIMESTAMP " + RoutineScriptText.Quote(stamp.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'));
        }

        return RoutineScriptText.Quote(text, type is "NVARCHAR2" or "NCHAR" or "NCLOB" ? "N" : string.Empty);
    }
}
