using System.Globalization;
using System.IO;
using JetQueries.Provider.Oracle;
using JetQueries.Provider.PostgreSql;
using JetQueries.Provider.Sqlite;
using JetQueries.Provider.SqlServer;
using JetQueries.Wpf.Views;
using UiKit.Theming;

namespace JetQueries.Wpf.Tests;

/// <summary>The connection editor's two steps, rendered in both themes.</summary>
[Collection(WpfUi.Name)]
public sealed class ConnectionDialogTests
{
    [Fact]
    public async Task The_type_list_the_form_and_the_connection_string_box_render()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);

        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            await using var env = new TestEnvironment();
            var editor = new ConnectionEditorViewModel(
                null, [new SqlServerProvider(), new OracleProvider(), new PostgreSqlProvider(), new SqliteProvider()], env.Store, env.Credentials, env.Manager, env.Jobs);
            var dialog = new ConnectionDialog(editor) { Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            dialog.Show();

            var results = new Dictionary<string, int>();
            async Task Shoot(string name)
            {
                foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
                {
                    ThemeManager.Apply(theme, save: false);
                    await EditorActionsTests.Idle();
                    var suffix = theme == AppTheme.Dark ? "dark" : "light";
                    results[$"{name}-{suffix}"] = ResultGridViewTests.Capture(dialog, Path.Combine(directory, $"wpf-connection-{name}-{suffix}.png"));
                }
            }

            Assert.True(editor.IsChoosingType);
            await Shoot("types");

            editor.ChooseTypeCommand.Execute(editor.Providers.Single(static p => p.Provider is SqlServerProvider));
            editor.IsConnectionStringMode = true;
            editor.ConnectionString = "Server=10.20.30.40,1433;Database=Cards;User Id=app_reader;Password=x;TrustServerCertificate=True";
            await Shoot("string");

            editor.ApplyConnectionStringCommand.Execute(null);
            Assert.True(editor.IsFormMode);
            await Shoot("form");

            ThemeManager.Apply(AppTheme.Dark, save: false);
            dialog.Close();
            return results;
        });

        Assert.All(colours, static pair => Assert.True(pair.Value > 12, $"{pair.Key} rendered only {pair.Value} distinct colours."));
    }
}
