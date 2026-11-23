using System.Globalization;
using JetQueries.Core.Connections;
using JetQueries.Presentation.ViewModels;
using JetQueries.Provider.Oracle;
using JetQueries.Provider.SqlServer;

namespace JetQueries.Presentation.Tests;

/// <summary>Which connection a query tab runs on is always visible; the connection editor speaks the user's language.</summary>
public sealed class ConnectionVisibilityTests
{
    public ConnectionVisibilityTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
    }

    [Theory]
    [InlineData(ConnectionEnvironment.Production, true, "ÜRETİM")]
    [InlineData(ConnectionEnvironment.Test, true, "TEST")]
    [InlineData(ConnectionEnvironment.Development, false, "Geliştirme")]
    [InlineData(ConnectionEnvironment.Unspecified, false, null)]
    public async Task A_tab_knows_the_environment_of_its_connection(ConnectionEnvironment environment, bool marked, string? name)
    {
        await using var env = new TestEnvironment();
        Assert.Null(env.Query().Environment);

        // The session is open before the tab exists: no list refresh from the manager's thread races the choice.
        var definition = await env.OpenAsync(TestEnvironment.Definition("Kartlar", environment: environment));
        var query = env.Query();
        query.SelectSession(definition.Id);

        Assert.Equal(environment, query.Environment);
        Assert.Equal(marked, query.IsMarkedEnvironment);
        Assert.Equal(name, query.EnvironmentName);

        // Switching the tab's connection tells the view (strip, dot and name follow).
        var session = query.SelectedSession;
        var changed = new System.Collections.Concurrent.ConcurrentQueue<string?>(); // A late list refresh may raise it from the manager's thread.
        query.PropertyChanged += (_, e) => changed.Enqueue(e.PropertyName);
        query.SelectedSession = null;
        query.SelectedSession = session;
        Assert.Contains(nameof(QueryDocumentViewModel.IsMarkedEnvironment), changed);
        Assert.Contains(nameof(QueryDocumentViewModel.Environment), changed);
    }

    [Fact]
    public async Task Choices_and_hints_of_providers_are_translated()
    {
        await using var env = new TestEnvironment();
        var editor = new ConnectionEditorViewModel(null, [new SqlServerProvider(), new OracleProvider()], env.Store, env.Credentials, env.Manager, env.Jobs);
        editor.ChooseTypeCommand.Execute(editor.Providers.Single(static p => p.Provider is SqlServerProvider));

        var encrypt = editor.Fields.Single(static f => f.Key == "encrypt");
        Assert.Equal(["Evet", "Hayır", "Katı (TDS 8)"], encrypt.Options.Select(static o => o.DisplayName));
        Assert.Equal("Evet", encrypt.SelectedOption?.DisplayName);
        Assert.Equal(["SQL Server oturumu", "Windows (tümleşik)"], editor.Fields.Single(static f => f.Key == "authentication").Options.Select(static o => o.DisplayName));
        Assert.Equal("boş: kullanıcının varsayılan veritabanı", editor.Fields.Single(static f => f.Key == "database").Hint);

        // A pasted string selects the translated choice, and the saved value stays the provider's.
        editor.ConnectionString = "Server=db;Database=x;Integrated Security=true;Encrypt=false";
        editor.ApplyConnectionStringCommand.Execute(null);
        Assert.Equal("Hayır", encrypt.SelectedOption?.DisplayName);
        Assert.Equal("false", editor.BuildDefinition().Settings["encrypt"]);

        editor.ChangeTypeCommand.Execute(null);
        editor.ChooseTypeCommand.Execute(editor.Providers.Single(static p => p.Provider is OracleProvider));
        Assert.Equal("Servis adı", editor.Fields.Single(static f => f.Key == "serviceName").Label);
        Assert.Equal("örn. ORCLPDB1; SID verildiyse boş bırakın", editor.Fields.Single(static f => f.Key == "serviceName").Hint);
        Assert.Equal("Bağlanma rolü", editor.Fields.Single(static f => f.Key == "role").Label);
    }
}
