using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using JetQueries.Core.Connections;
using JetQueries.Desktop.ViewModels;
using JetQueries.Desktop.Views;

namespace JetQueries.Desktop.Tests;

/// <summary>
/// Renders the real connection views (application styles and tokens, Skia, no display) in both themes and
/// writes them as PNG files next to the test assembly, under <c>screenshots/</c>. The assertions only guard
/// against a blank or broken render; the images are for looking at.
/// </summary>
[Collection(HeadlessUi.Collection)]
public sealed class ConnectionViewRenderTests
{
    [Fact]
    public async Task Connection_views_render_in_both_themes()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        var token = TestContext.Current.CancellationToken;

        // View models are prepared here, off the UI thread, exactly as far as the screenshots need them;
        // the dispatcher then only builds windows and draws.
        await using var env = new TestEnvironment();
        await env.Store.SaveAsync(TestEnvironment.Definition("Local development", string.Empty, ConnectionEnvironment.Development), token);
        var production = TestEnvironment.Definition("Cards — production", "Customer A", ConnectionEnvironment.Production) with { IsReadOnly = true };
        await env.Store.SaveAsync(production, token);
        var test = TestEnvironment.Definition("Cards — test", "Customer A", ConnectionEnvironment.Test);
        await env.Store.SaveAsync(test, token);
        await env.Store.SaveAsync(TestEnvironment.Definition("Müşteri İşlemleri", "Customer B"), token);
        await env.Credentials.SetPasswordAsync(test.Id, "x", token);

        var connections = env.Connections();
        await connections.LoadAsync();
        var nodes = connections.Roots.SelectMany(static r => r.Children).OfType<ConnectionNodeViewModel>().ToList();
        connections.Selected = nodes.Single(n => n.Definition.Id == test.Id);
        await connections.ToggleConnectionCommand.ExecuteAsync(null);
        nodes.Single(n => n.Definition.Id == production.Id).Error = "failed";

        var editor = env.Editor();
        editor.Name = "Cards — test";
        editor.Fields[0].Value = "10.20.30.40,1433";
        editor.Fields[1].Value = "Cards";
        editor.UserName = "app_reader";
        editor.Password = "secret";
        editor.Folder = "Customer A";
        editor.SelectedEnvironment = editor.Environments[2];
        env.Probe.Answer = "No answer from 10.20.30.40:1433 within 4 s.";
        await editor.TestCommand.ExecuteAsync(null);

        // A second tree with the object explorer opened down to the columns of a table.
        await using var explorerEnvironment = new TestEnvironment();
        var (explorer, connected) = await ObjectExplorerTests.ConnectAsync(explorerEnvironment);
        var database = (DatabaseObjectNodeViewModel)connected.Children[0];
        await ObjectExplorerTests.ExpandAsync(database);
        var schema = (DatabaseObjectNodeViewModel)database.Children[0];
        await ObjectExplorerTests.ExpandAsync(schema);
        var tables = (CategoryNodeViewModel)schema.Children[0];
        await ObjectExplorerTests.ExpandAsync(tables);
        await ObjectExplorerTests.ExpandAsync((DatabaseObjectNodeViewModel)tables.Children[0]);
        await ObjectExplorerTests.ExpandAsync((CategoryNodeViewModel)schema.Children[^1]);
        explorer.Selected = tables.Children[1];

        // The whole window: explorer on the left, a query with its results on the right.
        var documents = explorerEnvironment.Documents();
        var query = documents.Selected!;
        query.RefreshSessions();
        query.Text = "-- Cards issued this year\nSELECT c.Id, c.CardNo, c.Balance, c.IssuedAt\nFROM dbo.Cards c\nWHERE c.IssuedAt >= '2026-01-01' AND c.Status = @status\nORDER BY c.Id; /* first 100 */";
        await query.RunCommand.ExecuteAsync(null);
        documents.NewQueryCommand.Execute(null);
        documents.Selected = query;

        // The history panel: the run above plus a failed one, a canceled one and a long script.
        await explorerEnvironment.History.AddAsync(HistoryTests.Entry("UPDATE dbo.Cards SET Balance = 0 WHERE Id = 42") with { RowCount = null, ConnectionName = "Cards — test" }, token);
        await explorerEnvironment.History.AddAsync(
            HistoryTests.Entry("SELECT * FROM dbo.Cardz") with { Outcome = Core.Queries.QueryOutcome.Failed, Error = "Invalid object name 'dbo.Cardz'.", RowCount = null },
            token);
        await explorerEnvironment.History.AddAsync(
            HistoryTests.Entry("EXEC dbo.PurgeOldLogs @olderThanDays = 90") with { Outcome = Core.Queries.QueryOutcome.Canceled, RowCount = null },
            token);
        await explorerEnvironment.History.AddAsync(
            HistoryTests.Entry("-- monthly totals\nSELECT c.CardNo, SUM(t.Amount) AS Total FROM dbo.Cards c JOIN dbo.Transactions t ON t.CardId = c.Id GROUP BY c.CardNo") with
            {
                RowCount = 18_240,
                Duration = TimeSpan.FromSeconds(2.37),
                ConnectionName = "Müşteri İşlemleri",
            },
            token);
        var history = explorerEnvironment.HistoryPanel(documents);
        await history.LoadAsync();
        history.Selected = history.Entries[2];
        var shell = new MainWindowViewModel(new FixedTheme(), explorer, documents, history);
        shell.ShowExtensions(1, 0);

        await using var emptyEnvironment = new TestEnvironment();
        var empty = emptyEnvironment.Connections();
        await empty.LoadAsync();

        var session = HeadlessUnitTestSession.StartNew(typeof(RenderApp));
        var colours = await session.Dispatch(
            () =>
            {
                var results = new Dictionary<string, int>();
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    Application.Current!.RequestedThemeVariant = theme;
                    var suffix = theme == ThemeVariant.Light ? "light" : "dark";
                    results[$"connections-{suffix}"] = Capture(Host(connections, 320), Path.Combine(directory, $"connections-{suffix}.png"));
                    results[$"explorer-{suffix}"] = Capture(Host(explorer, 520), Path.Combine(directory, $"explorer-{suffix}.png"));
                    results[$"window-{suffix}"] = Capture(new MainWindow(shell) { Width = 1180, Height = 700 }, Path.Combine(directory, $"window-{suffix}.png"));
                    results[$"history-{suffix}"] = Capture(
                        new Window { Width = 300, Height = 330, Content = new Border { Classes = { "panel" }, Child = new HistoryView { DataContext = history } } },
                        Path.Combine(directory, $"history-{suffix}.png"));
                    results[$"empty-{suffix}"] = Capture(Host(empty, 160), Path.Combine(directory, $"empty-{suffix}.png"));
                    results[$"dialog-{suffix}"] = Capture(new ConnectionDialog { DataContext = editor }, Path.Combine(directory, $"dialog-{suffix}.png"));
                }

                return results;
            },
            token);

        // The code after the await above runs on the session's own dispatcher thread, and Dispose waits for
        // that thread to end: disposing from here would wait for itself.
        await Task.Run(session.Dispose, token);

        Assert.Equal(12, colours.Count);
        Assert.All(colours, static pair => Assert.True(pair.Value > 8, $"{pair.Key} rendered only {pair.Value} distinct colours."));
    }

    private sealed class FixedTheme : Services.IThemeService
    {
        public bool IsDark => false;

        public void Toggle()
        {
        }
    }

    private static Window Host(ConnectionsViewModel viewModel, double height) => new()
    {
        Width = 300,
        Height = height,
        Content = new Border { Classes = { "panel" }, Child = new ConnectionsView { DataContext = viewModel } },
    };

    private static int Capture(Window window, string path)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame()!;
        frame.Save(path, new PngBitmapEncoderOptions());
        var colours = CountDistinctColours(frame);
        window.Close();
        return colours;
    }

    private static unsafe int CountDistinctColours(WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        var colours = new HashSet<uint>();
        for (var y = 0; y < buffer.Size.Height; y += 2)
        {
            var row = (uint*)(buffer.Address + (y * buffer.RowBytes));
            for (var x = 0; x < buffer.Size.Width; x += 2)
            {
                colours.Add(row[x]);
            }
        }

        return colours.Count;
    }

    private static class RenderApp
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure(static () => new App())
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont();
    }
}
