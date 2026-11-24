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

    [Fact]
    public async Task A_protected_section_is_written_encrypted_and_a_plain_one_is_moved_over()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(AppContext.BaseDirectory, "settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new SettingsStoreOptions { Directory = directory, Protector = new WindowsDataProtector() };
        options.ProtectedSections.Add("workspace");
        var store = new JsonSettingsStore(options, NullLogger<JsonSettingsStore>.Instance);

        // A workspace saved by an earlier version, in plain text, is still read…
        var plain = Path.Combine(directory, "workspace.json");
        await File.WriteAllTextAsync(plain, "{\"font\": \"IDENTIFIED BY Gizli123\"}", Token);
        Assert.Equal("IDENTIFIED BY Gizli123", (await store.LoadAsync("workspace", new Sample(), Token)).Font);

        // …and the next save encrypts it and removes the plain file.
        await store.SaveAsync("workspace", new Sample { Font = "IDENTIFIED BY Gizli123" }, Token);
        Assert.False(File.Exists(plain));
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "workspace.bin"), Token);
        Assert.DoesNotContain("Gizli123", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("Gizli123", System.Text.Encoding.Unicode.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal("IDENTIFIED BY Gizli123", (await store.LoadAsync("workspace", new Sample(), Token)).Font);

        // Other sections stay plain JSON.
        await store.SaveAsync("appearance", new Sample(), Token);
        Assert.True(File.Exists(Path.Combine(directory, "appearance.json")));

        // A damaged file gives the defaults.
        await File.WriteAllBytesAsync(Path.Combine(directory, "workspace.bin"), [1, 2, 3], Token);
        Assert.Equal(new Sample(), await store.LoadAsync("workspace", new Sample(), Token));
    }

    [Fact]
    public async Task Without_a_protector_a_protected_section_never_reaches_the_disk()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "settings-tests", Guid.NewGuid().ToString("N"));
        var options = new SettingsStoreOptions { Directory = directory };
        options.ProtectedSections.Add("workspace");
        var store = new JsonSettingsStore(options, NullLogger<JsonSettingsStore>.Instance);

        await store.SaveAsync("workspace", new Sample { Size = 20 }, Token);

        Assert.False(File.Exists(Path.Combine(directory, "workspace.json")));
        Assert.False(File.Exists(Path.Combine(directory, "workspace.bin")));
    }
}
