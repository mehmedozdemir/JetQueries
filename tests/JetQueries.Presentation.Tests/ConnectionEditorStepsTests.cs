using System.Globalization;
using JetQueries.Core.Connections;
using JetQueries.Presentation.ViewModels;
using JetQueries.Provider.Oracle;
using JetQueries.Provider.SqlServer;
using JetQueries.Sdk;

namespace JetQueries.Presentation.Tests;

/// <summary>The two steps of the connection editor (type, then details) and reading a pasted connection string.</summary>
public sealed class ConnectionEditorStepsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ConnectionEditorStepsTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    private static ConnectionEditorViewModel Editor(TestEnvironment env, ConnectionDefinition? existing = null) =>
        new(existing, [new SqlServerProvider(), new OracleProvider(), env.Provider], env.Store, env.Credentials, env.Manager, env.Jobs);

    private static ConnectionEditorViewModel SqlServerForm(TestEnvironment env)
    {
        var editor = Editor(env);
        editor.ChooseTypeCommand.Execute(editor.Providers.Single(static p => p.Provider is SqlServerProvider));
        return editor;
    }

    [Fact]
    public async Task A_new_connection_starts_with_the_types_and_their_marks()
    {
        await using var env = new TestEnvironment();
        var editor = Editor(env);

        Assert.True(editor.IsChoosingType);
        Assert.False(editor.IsEditingSettings);
        Assert.Null(editor.SelectedProvider);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.False(editor.TestCommand.CanExecute(null));

        var sqlServer = editor.Providers.Single(static p => p.Provider is SqlServerProvider);
        Assert.Equal("SQL Server and Azure SQL", sqlServer.Description);
        Assert.Equal("#B4262D", sqlServer.Mark.Color);
        Assert.True(sqlServer.ReadsConnectionStrings);

        // A provider that does not describe itself gets the default mark and no connection string box.
        var fake = editor.Providers.Single(p => p.Provider == env.Provider);
        Assert.Same(ProviderMark.Default, fake.Mark);
        Assert.False(fake.ReadsConnectionStrings);
    }

    [Fact]
    public async Task Choosing_a_type_shows_its_form_and_change_goes_back()
    {
        await using var env = new TestEnvironment();
        var editor = SqlServerForm(env);

        Assert.False(editor.IsChoosingType);
        Assert.True(editor.IsFormMode);
        Assert.Equal("server", editor.Fields[0].Key);
        Assert.True(editor.SaveCommand.CanExecute(null));
        Assert.True(editor.CanChangeType);

        editor.ChangeTypeCommand.Execute(null);
        Assert.True(editor.IsChoosingType);
        editor.ChooseTypeCommand.Execute(editor.Providers.Single(static p => p.Provider is OracleProvider));
        Assert.Equal("host", editor.Fields[0].Key);
    }

    [Fact]
    public async Task A_saved_connection_opens_on_its_form_and_keeps_its_type()
    {
        await using var env = new TestEnvironment();
        var existing = new ConnectionDefinition { Id = Guid.NewGuid(), Name = "x", ProviderId = SqlServerProvider.ProviderId, Settings = new Dictionary<string, string> { ["server"] = "db" } };
        var editor = Editor(env, existing);

        Assert.False(editor.IsChoosingType);
        Assert.False(editor.CanChangeType);
        Assert.Equal("db", editor.Fields[0].Value);
    }

    [Fact]
    public async Task A_connection_string_fills_the_form_and_hands_the_password_to_the_box()
    {
        await using var env = new TestEnvironment();
        var editor = SqlServerForm(env);
        editor.Fields.Single(static f => f.Key == "connectTimeoutSeconds").Value = "99";
        string? boxPassword = null;
        editor.PasswordProvided += (_, password) => boxPassword = password;

        editor.IsConnectionStringMode = true;
        editor.ConnectionString = "Server=db01,1500;Database=Sales;User Id=app;Password=s3cret;TrustServerCertificate=true;Column Encryption Setting=Enabled";
        Assert.True(editor.ApplyConnectionStringCommand.CanExecute(null));
        editor.ApplyConnectionStringCommand.Execute(null);

        Assert.True(editor.IsFormMode);
        Assert.Empty(editor.ConnectionString);
        Assert.False(editor.ConnectionStringFailed);
        Assert.Equal("The details and the password were filled in from the connection string. Not taken: Column Encryption Setting.", editor.ConnectionStringMessage, ignoreCase: true);
        Assert.Equal("db01,1500", editor.Fields.Single(static f => f.Key == "server").Value);
        Assert.Equal("true", editor.Fields.Single(static f => f.Key == "trustServerCertificate").Value);

        // What the string does not say is back at its default.
        Assert.Equal("15", editor.Fields.Single(static f => f.Key == "connectTimeoutSeconds").Value);
        Assert.Equal("sql", editor.Fields.Single(static f => f.Key == "authentication").SelectedOption!.Value);
        Assert.Equal("app", editor.UserName);
        Assert.Equal("s3cret", boxPassword);
        Assert.Equal("db01 / Sales", editor.Name);

        // The password goes to the credential store only.
        var definition = editor.BuildDefinition();
        Assert.DoesNotContain(definition.Settings.Values, static v => v.Contains("s3cret", StringComparison.Ordinal));
        await editor.SaveCommand.ExecuteAsync(null);
        var saved = Assert.Single(await env.Store.GetAllAsync(Token));
        Assert.Equal("s3cret", await env.Credentials.GetPasswordAsync(saved.Id, Token));
    }

    [Fact]
    public async Task Windows_authentication_from_a_string_needs_no_credential()
    {
        await using var env = new TestEnvironment();
        var editor = SqlServerForm(env);
        editor.Name = "Mine";
        editor.ConnectionString = "Data Source=db\\INST;Integrated Security=SSPI";
        editor.ApplyConnectionStringCommand.Execute(null);

        Assert.False(editor.NeedsCredential);
        Assert.Equal("Mine", editor.Name);
        Assert.Equal("The details were filled in from the connection string.", editor.ConnectionStringMessage);
    }

    [Fact]
    public async Task A_string_that_cannot_be_read_leaves_the_form_alone()
    {
        await using var env = new TestEnvironment();
        var editor = Editor(env);
        editor.ChooseTypeCommand.Execute(editor.Providers.Single(static p => p.Provider is OracleProvider));
        editor.Fields[0].Value = "keep";
        editor.IsConnectionStringMode = true;
        editor.ConnectionString = "User Id=hr;Password=pw;Data Source=PRODDB";

        editor.ApplyConnectionStringCommand.Execute(null);

        Assert.True(editor.ConnectionStringFailed);
        Assert.StartsWith("The connection string could not be read: TNS names", editor.ConnectionStringMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("pw", editor.ConnectionStringMessage, StringComparison.Ordinal);
        Assert.True(editor.IsConnectionStringMode);
        Assert.Equal("keep", editor.Fields[0].Value);
        Assert.Empty(editor.Password);
    }

    [Fact]
    public async Task Cancel_forgets_the_pasted_text()
    {
        await using var env = new TestEnvironment();
        var editor = SqlServerForm(env);
        editor.ConnectionString = "Server=x;Password=pw";
        editor.CancelCommand.Execute(null);
        Assert.Empty(editor.ConnectionString);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#GG0000")]
    public void A_mark_with_a_bad_colour_is_the_default(string color)
    {
        Assert.Same(ProviderMark.Default, ProviderMark.For(new Painted(color)));
    }

    private sealed class Painted(string color) : IDatabaseProvider, IProviderAppearance
    {
        public string Id => "painted";

        public string DisplayName => "Painted";

        public ProviderCapabilities Capabilities => ProviderCapabilities.None;

        public ISqlLanguageService? LanguageService => null;

        public string IconPathData => "M0 0h24v24H0z";

        public string AccentColor => color;

        public string Description => "x";

        public string QuoteIdentifier(string identifier) => identifier;

        public Task<IDatabaseConnection> OpenConnectionAsync(ConnectionProfile profile, ConnectionCredential? credential, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
