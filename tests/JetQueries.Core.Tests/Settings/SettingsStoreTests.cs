using JetQueries.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace JetQueries.Core.Tests.Settings;

public sealed class SettingsStoreTests
{
    private sealed record Sample
    {
        public string Font { get; init; } = "Segoe UI";

        public double Size { get; init; } = 13;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_section_round_trips_through_its_file_and_defaults_fill_the_rest()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "settings-tests", Guid.NewGuid().ToString("N"));
        var store = new JsonSettingsStore(new SettingsStoreOptions { Directory = directory }, NullLogger<JsonSettingsStore>.Instance);

        Assert.Equal(new Sample(), await store.LoadAsync("appearance", new Sample(), Token));
        await store.SaveAsync("appearance", new Sample { Size = 15 }, Token);
        Assert.Equal(new Sample { Size = 15 }, await store.LoadAsync("appearance", new Sample(), Token));
        Assert.True(File.Exists(Path.Combine(directory, "appearance.json")));
        Assert.False(File.Exists(Path.Combine(directory, "appearance.json.tmp")));

        // A property missing from the file keeps its default.
        await File.WriteAllTextAsync(Path.Combine(directory, "appearance.json"), "{\"size\": 11}", Token);
        Assert.Equal(new Sample { Size = 11 }, await store.LoadAsync("appearance", new Sample(), Token));
    }

    [Fact]
    public async Task An_unreadable_file_gives_the_defaults_and_is_left_alone()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "appearance.json");
        await File.WriteAllTextAsync(path, "{ not json", Token);
        var store = new JsonSettingsStore(new SettingsStoreOptions { Directory = directory }, NullLogger<JsonSettingsStore>.Instance);

        Assert.Equal(new Sample(), await store.LoadAsync("appearance", new Sample(), Token));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(path, Token));
    }
}
