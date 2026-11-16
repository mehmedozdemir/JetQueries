using System.Globalization;

namespace JetQueries.DataGrid.Spike;

/// <summary>
/// Spike-only row source: deterministic mixed-type data computed from (row, column), so a million rows cost no
/// memory and the measurement isolates the grid itself. Remove once a real result store exists.
/// </summary>
public sealed class SyntheticRowSource : IGridRowSource
{
    private static readonly string[] FirstNames =
        ["Ayşe", "Mehmet", "İbrahim", "Gülşen", "Çağrı", "Özlem", "Ümit", "Şule", "Oğuz", "Zeynep", "John", "Maria"];

    private static readonly string[] LastNames =
        ["Yılmaz", "Öztürk", "Çelik", "Şahin", "Kılıç", "Doğan", "Güneş", "Işık", "Smith", "García"];

    private static readonly string[] Statuses = ["NEW", "PENDING", "APPROVED", "REJECTED", "CANCELLED"];

    private static readonly string[] Kinds = ["name", "amount", "created_at", "status", "quantity", "trace_id", "notes"];

    private const string Filler =
        "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et " +
        "dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip.";

    private static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private readonly GridColumn[] _columns;

    /// <summary>Creates the source.</summary>
    /// <param name="rowCount">Initial number of rows.</param>
    /// <param name="columnCount">Number of columns (the first is a row id).</param>
    public SyntheticRowSource(int rowCount, int columnCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(columnCount, 1);

        RowCount = rowCount;
        _columns = new GridColumn[columnCount];
        _columns[0] = new GridColumn("id", 80, GridTextAlignment.Right);
        for (var i = 1; i < columnCount; i++)
        {
            var kind = (i - 1) % Kinds.Length;
            var header = string.Create(CultureInfo.InvariantCulture, $"{Kinds[kind]}_{((i - 1) / Kinds.Length) + 1}");
            _columns[i] = kind switch
            {
                0 => new GridColumn(header, 150),
                1 => new GridColumn(header, 110, GridTextAlignment.Right),
                2 => new GridColumn(header, 150),
                3 => new GridColumn(header, 100),
                4 => new GridColumn(header, 90, GridTextAlignment.Right),
                5 => new GridColumn(header, 140),
                _ => new GridColumn(header, 260),
            };
        }
    }

    /// <inheritdoc />
    public event EventHandler? RowCountChanged;

    /// <inheritdoc />
    public IReadOnlyList<GridColumn> Columns => _columns;

    /// <inheritdoc />
    public int RowCount { get; private set; }

    /// <summary>Simulates a streamed batch arriving. Call on the UI thread.</summary>
    /// <param name="count">Number of rows to add.</param>
    public void AppendRows(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        RowCount = checked(RowCount + count);
        RowCountChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public string? GetText(int row, int column)
    {
        if (column == 0)
        {
            return (row + 1).ToString(CultureInfo.InvariantCulture);
        }

        var hash = Mix((ulong)row, (ulong)column);
        return ((column - 1) % Kinds.Length) switch
        {
            0 => FirstNames[hash % (ulong)FirstNames.Length] + " " + LastNames[(hash >> 8) % (ulong)LastNames.Length],
            1 => ((hash % 1_000_000_000) / 100m).ToString("N2", CultureInfo.InvariantCulture),
            2 => Epoch.AddSeconds(hash % 200_000_000).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            3 => Statuses[hash % (ulong)Statuses.Length],
            4 => hash % 7 == 0 ? null : (hash % 100_000).ToString(CultureInfo.InvariantCulture),
            5 => hash.ToString("x16", CultureInfo.InvariantCulture),
            _ => Filler[(int)(hash % 40)..(int)(80 + (hash % 120))],
        };
    }

    // SplitMix64 finalizer: cheap, well-distributed, deterministic.
    private static ulong Mix(ulong row, ulong column)
    {
        var z = (row * 0x9E3779B97F4A7C15UL) + (column * 0xBF58476D1CE4E5B9UL) + 0x94D049BB133111EBUL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
