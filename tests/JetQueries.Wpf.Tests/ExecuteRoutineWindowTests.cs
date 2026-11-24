using System.Globalization;
using System.IO;
using System.Windows;
using JetQueries.Presentation.ViewModels;
using JetQueries.Sdk;
using JetQueries.Wpf.Views;
using UiKit.Theming;

namespace JetQueries.Wpf.Tests;

/// <summary>The Execute dialog of a routine, rendered.</summary>
[Collection(WpfUi.Name)]
public sealed class ExecuteRoutineWindowTests
{
    [Fact]
    public async Task The_dialog_lists_the_parameters_and_shows_the_script()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            await using var env = new TestEnvironment();
            env.Provider.Signatures = static _ =>
            [
                new SqlSignature(
                    "KartTransfer",
                    [
                        new SqlParameterInfo("@kaynak_kart", "int"), new SqlParameterInfo("@aciklama", "nvarchar(100)", IsOptional: true),
                        new SqlParameterInfo("@islem_tarihi", "datetime2(7)"), new SqlParameterInfo("@yeni_bakiye", "decimal(12,2)") { Direction = "OUT" },
                    ],
                    null),
            ];
            var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar"));
            var session = env.Manager.Sessions.Single(s => s.Definition.Id == definition.Id);
            var routine = new ExecuteRoutineViewModel(session, new DatabaseObject(new DatabaseObjectName("Cards", "dbo", "KartTransfer"), DatabaseObjectKinds.Procedure), env.Metadata, env.Jobs);
            await routine.LoadAsync();
            routine.Parameters[0].Text = "42";
            routine.Parameters[2].SelectedOption = routine.Parameters[2].Options[1];
            routine.Parameters[2].Text = "SYSDATETIME()";

            var window = new ExecuteRoutineWindow(routine) { Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            var results = new Dictionary<string, int>();
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.Apply(theme, save: false);
                await EditorActionsTests.Idle();
                var suffix = theme == AppTheme.Dark ? "dark" : "light";
                results[suffix] = ResultGridViewTests.Capture(window, Path.Combine(directory, $"wpf-execute-routine-{suffix}.png"));
            }

            Assert.Contains("@kaynak_kart=Value:42", window.ScriptView.Text, StringComparison.Ordinal);
            ThemeManager.Apply(AppTheme.Dark, save: false);
            window.Close();
            return results;
        });

        Assert.All(colours, static pair => Assert.True(pair.Value > 12, $"{pair.Key} rendered only {pair.Value} distinct colours."));
    }
}
