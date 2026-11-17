using System.Globalization;
using JetQueries.Core.Connections;
using JetQueries.Desktop.ViewModels;

namespace JetQueries.Desktop.Tests;

public sealed class ConnectionEditorViewModelTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ConnectionEditorViewModelTests()
    {
        // Messages are compared with their English text.
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    [Fact]
    public async Task A_new_connection_starts_with_the_providers_fields_and_defaults()
    {
        await using var env = new TestEnvironment();
        var editor = env.Editor();

        Assert.Equal("New connection", editor.Title);
        Assert.Same(env.Provider, editor.SelectedProvider!.Provider);
        Assert.Equal(["server", "database", "authentication", "trustServerCertificate", "connectTimeoutSeconds"], editor.Fields.Select(static f => f.Key));
        Assert.Equal("sql", editor.Fields[2].Value);
        Assert.Equal("SQL Server login", editor.Fields[2].SelectedOption!.DisplayName);
        Assert.False(editor.Fields[3].BooleanValue);
        Assert.True(editor.NeedsCredential);
        Assert.True(editor.SavePassword);
    }

    [Fact]
    public async Task Save_is_refused_until_the_required_values_are_there()
    {
        await using var env = new TestEnvironment();
        var editor = env.Editor();
        var closed = false;
        editor.CloseRequested += (_, _) => closed = true;

        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Enter a name.", editor.ValidationMessage);

        editor.Name = "Cards";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Server is required.", editor.ValidationMessage);

        editor.Fields[0].Value = "sql01";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Enter a user name.", editor.ValidationMessage);

        Assert.False(closed);
        Assert.Empty(await env.Store.GetAllAsync(Token));
    }

    [Fact]
    public async Task Saving_puts_the_password_in_the_credential_store_and_nowhere_else()
    {
        await using var env = new TestEnvironment();
        var editor = env.Editor();
        bool? saved = null;
        editor.CloseRequested += (_, result) => saved = result;
        editor.Name = "  Cards  ";
        editor.Fields[0].Value = "sql01,1433";
        editor.Folder = @" Müşteri A \ Test/ ";
        editor.SelectedEnvironment = editor.Environments.Single(static e => e.Value == ConnectionEnvironment.Test);
        editor.IsReadOnly = true;
        editor.UserName = "app";
        editor.Password = "s3cret";

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(saved);
        var definition = Assert.Single(await env.Store.GetAllAsync(Token));
        Assert.Equal("Cards", definition.Name);
        Assert.Equal("Müşteri A/Test", definition.Folder);
        Assert.Equal(ConnectionEnvironment.Test, definition.Environment);
        Assert.True(definition.IsReadOnly);
        Assert.Equal("app", definition.UserName);
        Assert.True(definition.SavePassword);
        Assert.Equal("sql01,1433", definition.Settings["server"]);
        Assert.DoesNotContain("s3cret", definition.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(definition.Settings.Values, static v => v.Contains("s3cret", StringComparison.Ordinal));
        Assert.Equal("s3cret", await env.Credentials.GetPasswordAsync(definition.Id, Token));
        Assert.Empty(editor.Password);
    }

    [Fact]
    public async Task Integrated_authentication_hides_and_drops_the_credential()
    {
        await using var env = new TestEnvironment();
        var editor = env.Editor();
        editor.Name = "Cards";
        editor.Fields[0].Value = "sql01";
        editor.UserName = "left over";
        editor.Password = "left over";

        editor.Fields[2].SelectedOption = editor.Fields[2].Options[1];
        Assert.False(editor.NeedsCredential);
        await editor.SaveCommand.ExecuteAsync(null);

        var definition = Assert.Single(await env.Store.GetAllAsync(Token));
        Assert.Equal("windows", definition.Settings["authentication"]);
        Assert.Null(definition.UserName);
        Assert.False(definition.SavePassword);
        Assert.Null(await env.Credentials.GetPasswordAsync(definition.Id, Token));
    }

    [Fact]
    public async Task Editing_keeps_the_saved_password_unless_a_new_one_is_typed_or_saving_is_switched_off()
    {
        await using var env = new TestEnvironment();
        var existing = TestEnvironment.Definition("Cards");
        await env.Store.SaveAsync(existing, Token);
        await env.Credentials.SetPasswordAsync(existing.Id, "old", Token);

        var rename = env.Editor(existing);
        Assert.Equal("Edit connection", rename.Title);
        Assert.Equal("leave empty to keep the saved password", rename.PasswordHint);
        rename.Name = "Cards 2";
        await rename.SaveCommand.ExecuteAsync(null);
        Assert.Equal("old", await env.Credentials.GetPasswordAsync(existing.Id, Token));
        Assert.Equal("Cards 2", Assert.Single(await env.Store.GetAllAsync(Token)).Name);

        var change = env.Editor(existing);
        change.Password = "new";
        await change.SaveCommand.ExecuteAsync(null);
        Assert.Equal("new", await env.Credentials.GetPasswordAsync(existing.Id, Token));

        var forget = env.Editor(existing);
        forget.SavePassword = false;
        await forget.SaveCommand.ExecuteAsync(null);
        Assert.Null(await env.Credentials.GetPasswordAsync(existing.Id, Token));
        Assert.False(Assert.Single(await env.Store.GetAllAsync(Token)).SavePassword);
    }

    [Fact]
    public async Task Test_reports_each_outcome_in_words()
    {
        await using var env = new TestEnvironment();
        var editor = env.Editor();
        editor.Name = "Cards";
        editor.Fields[0].Value = "sql01";
        editor.UserName = "app";
        editor.Password = "typed";

        await editor.TestCommand.ExecuteAsync(null);
        Assert.True(editor.TestSucceeded);
        Assert.StartsWith("Connected in ", editor.TestMessage, StringComparison.Ordinal);
        Assert.Equal(("app", "typed"), env.Provider.LastCredential);
        Assert.False(editor.IsTesting);

        env.Provider.OpenError = "Login failed for user 'app'.";
        await editor.TestCommand.ExecuteAsync(null);
        Assert.False(editor.TestSucceeded);
        Assert.True(editor.TestFailed);
        Assert.Equal("The server refused the connection: Login failed for user 'app'.", editor.TestMessage);

        env.Probe.Answer = "No answer from sql01:1433 within 4 s.";
        await editor.TestCommand.ExecuteAsync(null);
        Assert.Contains("VPN", editor.TestMessage, StringComparison.Ordinal);
        Assert.Contains("No answer from sql01:1433", editor.TestMessage, StringComparison.Ordinal);

        // Nothing was saved by testing.
        Assert.Empty(await env.Store.GetAllAsync(Token));
    }

    [Fact]
    public async Task Testing_an_existing_connection_with_an_empty_box_uses_the_saved_password()
    {
        await using var env = new TestEnvironment();
        var existing = TestEnvironment.Definition("Cards");
        await env.Credentials.SetPasswordAsync(existing.Id, "stored", Token);
        var editor = env.Editor(existing);

        await editor.TestCommand.ExecuteAsync(null);

        Assert.True(editor.TestSucceeded);
        Assert.Equal(("app", "stored"), env.Provider.LastCredential);
    }

    [Fact]
    public void Messages_are_available_in_turkish()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            var message = ConnectionEditorViewModel.Describe(new ConnectionAttempt(ConnectionOutcome.Unreachable, "x", TimeSpan.Zero));
            Assert.StartsWith("Sunucuya ulaşılamıyor", message, StringComparison.Ordinal);
            Assert.Equal("Sunucu", new SettingFieldViewModel(new("server", "Server", Sdk.ConnectionSettingKind.Text), null).Label);
        }
        finally
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        }
    }
}

public sealed class ConnectionsViewModelTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ConnectionsViewModelTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    [Fact]
    public async Task The_tree_groups_connections_by_folder_with_folders_first()
    {
        await using var env = new TestEnvironment();
        await env.Store.SaveAsync(TestEnvironment.Definition("Local"), Token);
        await env.Store.SaveAsync(TestEnvironment.Definition("Prod", "Customer A", ConnectionEnvironment.Production), Token);
        await env.Store.SaveAsync(TestEnvironment.Definition("Test 1", "Customer A/Test", ConnectionEnvironment.Test), Token);
        await env.Store.SaveAsync(TestEnvironment.Definition("Test 2", "customer a/test"), Token);
        var connections = env.Connections();

        Assert.False(connections.IsEmpty);
        await connections.LoadAsync();

        Assert.Equal(["Customer A", "Local"], connections.Roots.Select(static n => n.Name));
        var customer = Assert.IsType<FolderNodeViewModel>(connections.Roots[0]);
        Assert.Equal(["Test", "Prod"], customer.Children.Select(static n => n.Name));
        Assert.Equal(["Test 1", "Test 2"], customer.Children[0].Children.Select(static n => n.Name));
        Assert.True(Assert.IsType<ConnectionNodeViewModel>(customer.Children[1]).IsProduction);
        Assert.False(connections.IsEmpty);
    }

    [Fact]
    public async Task An_empty_store_shows_the_empty_state()
    {
        await using var env = new TestEnvironment();
        var connections = env.Connections();

        await connections.LoadAsync();

        Assert.True(connections.IsEmpty);
        Assert.False(connections.EditCommand.CanExecute(null));
        Assert.False(connections.ToggleConnectionCommand.CanExecute(null));
    }

    [Fact]
    public async Task Connect_and_disconnect_toggle_the_selected_connection()
    {
        await using var env = new TestEnvironment();
        var definition = TestEnvironment.Definition("Cards");
        await env.Store.SaveAsync(definition, Token);
        await env.Credentials.SetPasswordAsync(definition.Id, "stored", Token);
        var connections = env.Connections();
        await connections.LoadAsync();
        var node = Assert.IsType<ConnectionNodeViewModel>(connections.Roots[0]);
        connections.Selected = node;
        Assert.Equal("Connect", connections.ToggleConnectionText);

        await connections.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.True(node.IsConnected);
        Assert.False(node.IsBusy);
        Assert.Empty(node.Error);
        Assert.Single(env.Manager.Sessions);
        Assert.Equal("Disconnect", connections.ToggleConnectionText);
        Assert.Empty(env.Dialogs.Asked);

        // A reload keeps the live state.
        await connections.LoadAsync();
        node = Assert.IsType<ConnectionNodeViewModel>(connections.Selected);
        Assert.True(node.IsConnected);

        await connections.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.False(node.IsConnected);
        Assert.Empty(env.Manager.Sessions);
    }

    [Fact]
    public async Task A_connection_without_a_stored_password_asks_for_it()
    {
        await using var env = new TestEnvironment();
        var definition = TestEnvironment.Definition("Cards") with { SavePassword = false };
        await env.Store.SaveAsync(definition, Token);
        var connections = env.Connections();
        await connections.LoadAsync();
        var node = (ConnectionNodeViewModel)connections.Roots[0];
        connections.Selected = node;

        // Canceled prompt: nothing happens, and it is not an error.
        await connections.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.False(node.IsConnected);
        Assert.Empty(node.Error);

        env.Dialogs.Password = "typed";
        await connections.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.True(node.IsConnected);
        Assert.Equal(("app", "typed"), env.Provider.LastCredential);
        Assert.Equal(["password:Cards", "password:Cards"], env.Dialogs.Asked);
        Assert.Null(await env.Credentials.GetPasswordAsync(definition.Id, Token));
    }

    [Fact]
    public async Task A_failed_connection_shows_why()
    {
        await using var env = new TestEnvironment();
        var definition = TestEnvironment.Definition("Cards");
        await env.Store.SaveAsync(definition, Token);
        await env.Credentials.SetPasswordAsync(definition.Id, "stored", Token);
        env.Probe.Answer = "No answer from sql01:1433 within 4 s.";
        var connections = env.Connections();
        await connections.LoadAsync();
        var node = (ConnectionNodeViewModel)connections.Roots[0];
        connections.Selected = node;

        await connections.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.False(node.IsConnected);
        Assert.True(node.HasError);
        Assert.Contains("VPN", node.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_asks_first_and_removes_definition_password_and_session()
    {
        await using var env = new TestEnvironment();
        var definition = TestEnvironment.Definition("Cards");
        await env.Store.SaveAsync(definition, Token);
        await env.Credentials.SetPasswordAsync(definition.Id, "stored", Token);
        var connections = env.Connections();
        await connections.LoadAsync();
        connections.Selected = connections.Roots[0];
        await connections.ToggleConnectionCommand.ExecuteAsync(null);

        await connections.DeleteCommand.ExecuteAsync(null);
        Assert.Single(await env.Store.GetAllAsync(Token));
        Assert.Contains("'Cards'", Assert.Single(env.Dialogs.Asked), StringComparison.Ordinal);

        env.Dialogs.Confirm = true;
        await connections.DeleteCommand.ExecuteAsync(null);

        Assert.Empty(await env.Store.GetAllAsync(Token));
        Assert.Null(await env.Credentials.GetPasswordAsync(definition.Id, Token));
        Assert.Empty(env.Manager.Sessions);
        Assert.True(connections.IsEmpty);
        Assert.Null(connections.Selected);
    }

    [Fact]
    public async Task New_and_edit_reload_only_when_something_was_saved()
    {
        await using var env = new TestEnvironment();
        var connections = env.Connections();
        await connections.LoadAsync();

        await connections.NewCommand.ExecuteAsync(null);
        Assert.True(connections.IsEmpty);

        await env.Store.SaveAsync(TestEnvironment.Definition("Added by the dialog"), Token);
        env.Dialogs.EditResult = true;
        await connections.NewCommand.ExecuteAsync(null);

        Assert.Equal("Added by the dialog", Assert.Single(connections.Roots).Name);
        Assert.Equal(["edit:new", "edit:new"], env.Dialogs.Asked);
    }
}
