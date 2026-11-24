using JetQueries.Sdk;

namespace JetQueries.Provider.SqlServer.Tests;

/// <summary>The Execute dialog's script for SQL Server routines.</summary>
public sealed class SqlServerExecuteScriptTests
{
    private static readonly SqlServerProvider Scripting = new();

    private static readonly SqlSignature Transfer = new(
        "Transfer",
        [
            new SqlParameterInfo("@from", "int"),
            new SqlParameterInfo("@note", "nvarchar(100)", IsOptional: true),
            new SqlParameterInfo("@when", "datetime2(7)"),
            new SqlParameterInfo("@done", "bit"),
            new SqlParameterInfo("@balance", "decimal(12,2)") { Direction = "OUT" },
        ],
        null);

    [Fact]
    public void A_procedure_is_run_with_exec_and_its_outputs_are_selected()
    {
        var script = Scripting.ScriptExecute(
            new DatabaseObject(new DatabaseObjectName("Cards", "dbo", "Transfer"), DatabaseObjectKinds.Procedure),
            Transfer,
            [
                new RoutineArgument("@from", RoutineArgumentKind.Value, "42"),
                new RoutineArgument("@note", RoutineArgumentKind.Default),
                new RoutineArgument("when", RoutineArgumentKind.Expression, "SYSDATETIME()"),
                new RoutineArgument("@done", RoutineArgumentKind.Value, "true"),
            ]);

        Assert.Equal(
            """
            DECLARE @return_value int;
            DECLARE @balance decimal(12,2);

            EXEC @return_value = [dbo].[Transfer]
                @from = 42,
                @when = SYSDATETIME(),
                @done = 1,
                @balance = @balance OUTPUT;

            SELECT @return_value AS [Return Value], @balance AS [@balance];

            """.ReplaceLineEndings("\n"),
            script);
    }

    [Theory]
    [InlineData("nvarchar(50)", "O'Neil", "N'O''Neil'")]
    [InlineData("varchar(50)", "abc", "'abc'")]
    [InlineData("int", "12x", "'12x'")]
    [InlineData("int", "", "NULL")]
    [InlineData("decimal(10,2)", "-1.5", "-1.5")]
    [InlineData("varbinary(max)", "0x0A0B", "0x0A0B")]
    [InlineData("date", "2026-01-15", "'2026-01-15'")]
    public void A_value_becomes_a_literal_of_the_parameter_type(string type, string value, string expected)
    {
        var signature = new SqlSignature("f", [new SqlParameterInfo("@p", type)], "int");
        var script = Scripting.ScriptExecute(
            new DatabaseObject(new DatabaseObjectName(null, "dbo", "f"), DatabaseObjectKinds.Function), signature, [new RoutineArgument("@p", RoutineArgumentKind.Value, value)]);
        Assert.Equal($"SELECT [dbo].[f]({expected}) AS [Result];\n", script);
    }

    [Fact]
    public void A_table_function_is_selected_from_and_a_default_stays_in_place()
    {
        var signature = new SqlSignature("Cards", [new SqlParameterInfo("@a", "int"), new SqlParameterInfo("@b", "int", IsOptional: true)], "table");
        var script = Scripting.ScriptExecute(
            new DatabaseObject(new DatabaseObjectName(null, "dbo", "Cards"), DatabaseObjectKinds.Function), signature, [new RoutineArgument("@b", RoutineArgumentKind.Default)]);
        Assert.Equal("SELECT *\nFROM [dbo].[Cards](NULL, DEFAULT);\n", script);
    }
}
