using System.Globalization;
using JetQueries.Presentation.ViewModels;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>The Execute dialog of a stored procedure or function.</summary>
public sealed class ExecuteRoutineTests
{
    private static readonly DatabaseObject Transfer = new(new DatabaseObjectName("Cards", "dbo", "Transfer"), DatabaseObjectKinds.Procedure);

    public ExecuteRoutineTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
    }

    [Fact]
    public async Task The_parameters_are_read_and_the_script_follows_what_is_typed()
    {
        await using var env = new TestEnvironment();
        env.Provider.Signatures = static _ =>
        [
            new SqlSignature("Transfer", [new SqlParameterInfo("from", "int"), new SqlParameterInfo("note", "nvarchar(50)", IsOptional: true), new SqlParameterInfo("total", "int") { Direction = "OUT" }], null),
            new SqlSignature("Transfer", [new SqlParameterInfo("code", "varchar(10)")], null),
        ];
        var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar"));
        var session = env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
        Assert.True(ExecuteRoutineViewModel.CanExecute(session, Transfer));
        Assert.False(ExecuteRoutineViewModel.CanExecute(session, Transfer with { Kind = DatabaseObjectKinds.Table }));

        var routine = new ExecuteRoutineViewModel(session, Transfer, env.Metadata, env.Jobs);
        await routine.LoadAsync();

        Assert.Equal("dbo.Transfer", routine.Title);
        Assert.True(routine.HasOverloads);
        Assert.Equal(["from", "note", "total"], routine.Parameters.Select(static p => p.Name));

        // Optional parameters keep their default, others start as NULL; a pure OUT takes no value.
        Assert.Equal(RoutineArgumentKind.Null, routine.Parameters[0].SelectedOption.Kind);
        Assert.Equal(RoutineArgumentKind.Default, routine.Parameters[1].SelectedOption.Kind);
        Assert.False(routine.Parameters[2].CanTakeValue);
        Assert.Equal("OUT", routine.Parameters[2].Direction);
        Assert.Equal("CALL Transfer(from=Null:, note=Default:, total=Null:)", routine.Script);

        // Typing a value passes it; an expression is chosen in the list.
        routine.Parameters[0].Text = "42";
        Assert.Equal(RoutineArgumentKind.Value, routine.Parameters[0].SelectedOption.Kind);
        routine.Parameters[1].SelectedOption = routine.Parameters[1].Options.Single(static o => o.Kind == RoutineArgumentKind.Expression);
        routine.Parameters[1].Text = "GETDATE()";
        Assert.Equal("CALL Transfer(from=Value:42, note=Expression:GETDATE(), total=Null:)", routine.Script);
        Assert.Equal("Değer", routine.Parameters[0].Options[0].Text);

        // The other form has its own parameters.
        routine.SelectedOverload = routine.Overloads[1];
        Assert.Equal("CALL Transfer(code=Null:)", routine.Script);

        // The query tab: the script on the routine's connection and database, run or not.
        var request = routine.Request(run: true);
        Assert.Equal(definition.Id, request.ConnectionId);
        Assert.Equal(routine.Script, request.Text);
        Assert.True(request.Run);
    }

    [Fact]
    public async Task Without_signatures_the_routine_is_called_without_arguments()
    {
        await using var env = new TestEnvironment();
        var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar"));
        var session = env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
        var routine = new ExecuteRoutineViewModel(session, Transfer, env.Metadata, env.Jobs);
        await routine.LoadAsync();

        Assert.True(routine.HasNoParameters);
        Assert.False(routine.HasOverloads);
        Assert.Equal("CALL Transfer()", routine.Script);
    }
}
