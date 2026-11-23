using System.Globalization;

namespace JetQueries.Presentation.Tests;

/// <summary>Manual commit in a query tab: when a transaction starts, how it ends, and what may not end it silently.</summary>
public sealed class TransactionTests
{
    public TransactionTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Manual_commit_begins_before_changes_only_and_ends_on_commit_or_rollback()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var query = env.Query();
        await Until(() => query.SupportsTransactions);
        Assert.True(query.AutoCommit);
        Assert.False(query.ShowTransactionButtons);

        // Auto-commit: nothing is begun.
        query.Text = "UPDATE dbo.Cards SET Balance = 0 WHERE Id = 1";
        await query.RunCommand.ExecuteAsync(null);
        Assert.Empty(env.Provider.TransactionLog);

        await query.ToggleAutoCommitCommand.ExecuteAsync(null);
        Assert.False(query.AutoCommit);
        Assert.Equal("Manual commit", query.TransactionModeText);
        Assert.True(query.ShowTransactionButtons);

        // A SELECT never opens a transaction; text that can change data does.
        query.Text = "SELECT * FROM dbo.Cards";
        await query.RunCommand.ExecuteAsync(null);
        Assert.Empty(env.Provider.TransactionLog);
        Assert.False(query.CommitCommand.CanExecute(null));

        query.Text = "DELETE FROM dbo.Cards WHERE Id = 3";
        await query.RunCommand.ExecuteAsync(null);
        Assert.Equal(["begin"], env.Provider.TransactionLog);
        Assert.True(query.HasOpenTransaction);
        Assert.True(query.CommitCommand.CanExecute(null));

        await query.CommitCommand.ExecuteAsync(null);
        Assert.Equal(["begin", "commit"], env.Provider.TransactionLog);
        Assert.False(query.HasOpenTransaction);
        Assert.Equal("Transaction committed.", query.Status);

        await query.RunCommand.ExecuteAsync(null);
        await query.RollbackCommand.ExecuteAsync(null);
        Assert.Equal(["begin", "commit", "begin", "rollback"], env.Provider.TransactionLog);
        Assert.False(query.HasOpenTransaction);
    }

    [Fact]
    public async Task Leaving_manual_mode_or_switching_database_with_an_open_transaction_asks_or_refuses()
    {
        await using var env = new TestEnvironment();
        env.Provider.SupportsDatabaseContext = true;
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var query = env.Query();
        await Until(() => query.SupportsTransactions && query.SelectedDatabase == "Cards");
        await query.ToggleAutoCommitCommand.ExecuteAsync(null);
        query.Text = "UPDATE dbo.Cards SET Balance = 0";
        await query.RunCommand.ExecuteAsync(null);
        Assert.True(query.HasOpenTransaction);

        // Cancel: still manual, still open.
        env.Dialogs.TransactionChoice = TransactionChoice.Cancel;
        await query.ToggleAutoCommitCommand.ExecuteAsync(null);
        Assert.False(query.AutoCommit);
        Assert.True(query.HasOpenTransaction);
        Assert.Contains("transaction:Query 1", env.Dialogs.Asked);

        // No database switch while it is open.
        query.SelectedDatabase = "master";
        await Until(() => query.SelectedDatabase == "Cards");
        Assert.Equal("Commit or roll back the open transaction before switching the database.", query.Status);

        // Rollback: back to auto-commit.
        env.Dialogs.TransactionChoice = TransactionChoice.Rollback;
        await query.ToggleAutoCommitCommand.ExecuteAsync(null);
        Assert.True(query.AutoCommit);
        Assert.False(query.HasOpenTransaction);
        Assert.Equal("rollback", env.Provider.TransactionLog[^1]);
    }

    [Fact]
    public async Task Closing_a_tab_or_the_application_asks_about_open_transactions()
    {
        await using var env = new TestEnvironment();
        await env.OpenAsync(TestEnvironment.Definition("Cards"));
        var documents = env.Documents();
        var first = documents.Selected!;
        first.RefreshSessions();
        await Until(() => first.SupportsTransactions);
        await first.ToggleAutoCommitCommand.ExecuteAsync(null);
        first.Text = "INSERT INTO dbo.Log VALUES (1)";
        await first.RunCommand.ExecuteAsync(null);
        Assert.True(first.HasOpenTransaction);

        // Cancel keeps the tab; a failing commit keeps it too, with the server's reason.
        env.Dialogs.TransactionChoice = TransactionChoice.Cancel;
        Assert.False(await documents.ResolveOpenTransactionsAsync());
        env.Dialogs.TransactionChoice = TransactionChoice.Commit;
        env.Provider.CommitError = "deadlock victim";
        await documents.CloseCommand.ExecuteAsync(first);
        Assert.Contains(first, documents.Documents);
        Assert.Equal("deadlock victim", first.Status);

        env.Provider.CommitError = null;
        Assert.True(await documents.ResolveOpenTransactionsAsync());
        Assert.Equal("commit", env.Provider.TransactionLog[^1]);
        Assert.False(first.HasOpenTransaction);

        // Nothing open: closing asks about the text only, not about a transaction.
        var asked = env.Dialogs.Asked.Count(static a => a.StartsWith("transaction:", StringComparison.Ordinal));
        env.Dialogs.SaveChoice = SaveChangesChoice.Discard;
        await documents.CloseCommand.ExecuteAsync(first);
        Assert.DoesNotContain(first, documents.Documents);
        Assert.Equal(asked, env.Dialogs.Asked.Count(static a => a.StartsWith("transaction:", StringComparison.Ordinal)));
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Token);
        }

        Assert.True(condition());
    }
}
