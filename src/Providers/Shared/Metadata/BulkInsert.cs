using System.Globalization;
using System.Text;
using JetQueries.Sdk;

namespace JetQueries.Providers.Metadata;

/// <summary>Multi-row INSERT statements with parameters <c>p{row}_{column}</c>, for the data import.</summary>
internal static class BulkInsert
{
    /// <summary>Rows per statement: as many as fit under the parameter limit, at most <paramref name="maxRows"/>.</summary>
    public static int Rows(int columnCount, int parameterLimit, int maxRows) => Math.Clamp(parameterLimit / Math.Max(1, columnCount), 1, maxRows);

    /// <summary><c>INSERT INTO t (a, b) VALUES (@p0_0, @p0_1), (@p1_0, @p1_1)</c>.</summary>
    public static string Values(Func<string, string> quote, char prefix, string table, IReadOnlyList<string> columns, int rowCount)
    {
        var text = new StringBuilder("INSERT INTO ").Append(table).Append(" (").AppendJoin(", ", columns.Select(quote)).Append(") VALUES");
        for (var row = 0; row < rowCount; row++)
        {
            text.Append(row == 0 ? "\n(" : ",\n(");
            for (var column = 0; column < columns.Count; column++)
            {
                text.Append(column == 0 ? string.Empty : ", ").Append(prefix).Append(Name(row, column));
            }

            text.Append(')');
        }

        return text.ToString();
    }

    /// <summary>The parameter of a value.</summary>
    public static string Name(int row, int column) => string.Create(CultureInfo.InvariantCulture, $"p{row}_{column}");
}
