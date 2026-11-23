using JetQueries.Sdk;

namespace JetQueries.Providers.Metadata;

/// <summary>Builds an <see cref="ObjectDetailSection"/> row by row.</summary>
/// <param name="key">The section's key (<see cref="ObjectDetailKeys"/>).</param>
/// <param name="title">Its English title.</param>
/// <param name="columns">(key, English title) of each column.</param>
internal sealed class DetailTable(string key, string title, params (string Key, string Title)[] columns)
{
    private readonly List<ObjectDetailRow> _rows = [];

    public int Count => _rows.Count;

    /// <summary>Adds a row; <paramref name="cells"/> has one value per column.</summary>
    public DetailTable Add(IReadOnlyList<string?> cells, DatabaseObject? target = null, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Count != columns.Length)
        {
            throw new ArgumentException($"{columns.Length} cells expected, {cells.Count} given.", nameof(cells));
        }

        _rows.Add(new ObjectDetailRow(cells) { Target = target, Source = source });
        return this;
    }

    /// <summary>The section; null when it has no rows and <paramref name="keepEmpty"/> is false.</summary>
    public ObjectDetailSection? Build(bool keepEmpty = false) =>
        _rows.Count == 0 && !keepEmpty ? null : new ObjectDetailSection(key, title, [.. columns.Select(static c => new ObjectDetailColumn(c.Key, c.Title))], [.. _rows]);

    /// <summary>"Yes"/empty for flags in cells.</summary>
    public static string? Flag(bool value) => value ? "✓" : null;
}

/// <summary>Collects properties in order, skipping empty values.</summary>
internal sealed class DetailProperties
{
    private readonly List<ObjectProperty> _items = [];

    public DetailProperties Add(string key, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _items.Add(new ObjectProperty(key, label, value));
        }

        return this;
    }

    public IReadOnlyList<ObjectProperty> Build() => [.. _items];
}
