using UiKit.Controls;

namespace JetQueries.Wpf.Views;

/// <summary>
/// A result set as UiKit's grid data: the rows that have arrived so far, in the view's order and through its
/// filters. A snapshot has a fixed row count; the grid gets a new one as rows stream in or the view changes,
/// with the same schema key so column widths and scroll position stay.
/// </summary>
public sealed class ResultGridSource : IGridDataSource, IGridColumnCaptions
{
    private readonly QueryResultSet _set;
    private readonly GridColumnKind[] _kinds;
    private readonly int[]? _map;

    private ResultGridSource(QueryResultSet set, GridColumnKind[] kinds, int[]? map, int rowCount)
    {
        _set = set;
        _kinds = kinds;
        _map = map;
        RowCount = rowCount;
    }

    public object SchemaKey => _set;

    public int RowCount { get; }

    public int ColumnCount => _kinds.Length;

    /// <summary>The rows of <paramref name="set"/> available now, as its view shows them.</summary>
    public static ResultGridSource Snapshot(QueryResultSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var map = set.View.RowMap;
        return new ResultGridSource(set, [.. set.Columns.Select(static c => KindOf(c.ClrType))], map, map?.Length ?? set.RowCount);
    }

    public static GridColumnKind KindOf(Type type) =>
        type == typeof(bool) ? GridColumnKind.Boolean
        : type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(sbyte) ||
          type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) ? GridColumnKind.Integer
        : type == typeof(decimal) || type == typeof(double) || type == typeof(float) ? GridColumnKind.Number
        : type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) ? GridColumnKind.DateTime
        : GridColumnKind.Text;

    public string GetColumnName(int column) => _set.Columns[column].Header;

    /// <summary>The database type under the column name (nvarchar(50), NUMBER, …).</summary>
    public string? GetColumnCaption(int column) => _showTypes ? _set.Columns[column].DataType : null;

    // Read once per snapshot: the grid takes a new one when the setting changes.
    private readonly bool _showTypes = Services.AppearanceService.ShowColumnTypes;

    public GridColumnKind GetColumnKind(int column) => _kinds[column];

    public string GetText(int row, int column) => _set.GetText(Row(row), column) ?? string.Empty;

    public bool IsNull(int row, int column) => _set.GetText(Row(row), column) is null;

    private int Row(int row) => _map is null ? row : _map[row];
}
