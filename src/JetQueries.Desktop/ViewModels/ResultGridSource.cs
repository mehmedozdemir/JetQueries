using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Data.Converters;
using JetQueries.DataGrid;
using JetQueries.Presentation.ViewModels;

namespace JetQueries.Desktop.ViewModels;

/// <summary>A result set as the Avalonia grid's row source.</summary>
public sealed class ResultGridSource : IGridRowSource
{
    private static readonly ConditionalWeakTable<QueryResultSet, ResultGridSource> Sources = [];

    private readonly QueryResultSet _set;

    private ResultGridSource(QueryResultSet set)
    {
        _set = set;
        Columns = [.. set.Columns.Select(static c => new GridColumn(c.Header, c.Width, c.IsNumeric ? GridTextAlignment.Right : GridTextAlignment.Left))];
        set.RowCountChanged += (_, e) => RowCountChanged?.Invoke(this, e);
    }

    public event EventHandler? RowCountChanged;

    public IReadOnlyList<GridColumn> Columns { get; }

    public int RowCount => _set.RowCount;

    /// <summary>One source per result set, however often a template asks.</summary>
    public static ResultGridSource For(QueryResultSet set) => Sources.GetValue(set, static s => new ResultGridSource(s));

    public string? GetText(int row, int column) => _set.GetText(row, column);
}

/// <summary>Binds a grid's <c>Source</c> to a <see cref="QueryResultSet"/>.</summary>
public sealed class ResultGridSourceConverter : IValueConverter
{
    public static ResultGridSourceConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is QueryResultSet set ? ResultGridSource.For(set) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
