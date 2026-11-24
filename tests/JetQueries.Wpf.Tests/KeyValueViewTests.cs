using System.Globalization;
using System.IO;
using System.Windows;
using JetQueries.Core.Connections;
using JetQueries.Presentation.ViewModels;
using JetQueries.Provider.Redis;
using JetQueries.Sdk;
using JetQueries.Wpf.Views;
using Testcontainers.Redis;

namespace JetQueries.Wpf.Tests;

/// <summary>A Redis key in its tab, rendered (a hash and a string), on a real Redis (opt-in: JETQUERIES_REDIS_TESTS=Y).</summary>
[Collection(WpfUi.Name)]
public sealed class KeyValueViewTests
{
    [Fact]
    public async Task A_hash_and_a_string_render_in_the_key_tab()
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable("JETQUERIES_REDIS_TESTS"), "Y", StringComparison.OrdinalIgnoreCase),
            "Set JETQUERIES_REDIS_TESTS=Y to start a Redis container.");
        await using var container = new RedisBuilder("redis:7-alpine").Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);

        var colours = await WpfTestHost.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr");
            await using var env = new TestEnvironment();
            await using var manager = new ConnectionManager(new FakeExtensions(new RedisProvider()), new SessionCredentialStore(), env.Probe);
            var settings = new Dictionary<string, string>
            {
                ["host"] = container.Hostname,
                ["port"] = container.GetMappedPublicPort(6379).ToString(CultureInfo.InvariantCulture),
            };
            var session = (await manager.OpenAsync(new ConnectionDefinition { Id = Guid.NewGuid(), Name = "Önbellek", ProviderId = RedisProvider.ProviderId, Settings = settings }, null, CancellationToken.None)).Session!;
            await foreach (var _ in session.ExecuteAsync(new QueryRequest("HSET user:1 name Ayşe city İstanbul mail ayse@example.com\nEXPIRE user:1 3600\nSET config:mode \"{\\\"theme\\\":\\\"dark\\\",\\\"size\\\":13}\""), CancellationToken.None))
            {
            }

            var results = new List<int>();
            foreach (var (key, file) in new[] { ("user:1", "wpf-redis-hash.png"), ("config:mode", "wpf-redis-string.png") })
            {
                var viewModel = new KeyValueViewModel(session, 0, key, env.Jobs, env.Dialogs);
                await viewModel.RefreshAsync();
                var view = new KeyValueView { DataContext = viewModel };
                var window = new Window { Content = view, Width = 900, Height = 420, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
                window.SetResourceReference(Window.BackgroundProperty, "Brush.Surface");
                window.Show();
                await EditorActionsTests.Idle();
                if (key == "user:1")
                {
                    Assert.Equal(3, view.Grid.Source.RowCount);
                }

                results.Add(ResultGridViewTests.Capture(window, Path.Combine(directory, file)));
                window.Close();
            }

            return results.Min();
        });

        Assert.True(colours > 8, $"rendered only {colours} distinct colours.");
    }
}
