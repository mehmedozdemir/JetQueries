using JetQueries.Sdk;

namespace JetQueries.Presentation.ViewModels;

/// <summary>What the analysis says about an index.</summary>
public enum IndexFindingKind
{
    /// <summary>Nothing to say.</summary>
    None,

    /// <summary>No user query read it since the counters started, yet writes keep it up to date.</summary>
    Unused,

    /// <summary>Another index has the same key columns (and filter) and holds everything this one holds.</summary>
    Duplicate,

    /// <summary>Its key columns are the start of another index's, which holds everything this one holds.</summary>
    Covered,
}

/// <summary>A finding about an index, and the other index it refers to.</summary>
/// <param name="Kind">What was found.</param>
/// <param name="Other">The index that makes this one unnecessary (duplicates and covered ones).</param>
public sealed record IndexFinding(IndexFindingKind Kind, string? Other = null)
{
    /// <summary>No finding.</summary>
    public static IndexFinding None { get; } = new(IndexFindingKind.None);
}

/// <summary>
/// Finds the indexes a table could do without: unused ones, exact duplicates and those whose keys start another
/// index's keys. Clustered indexes and indexes behind a primary key or unique constraint are never proposed; a unique
/// index is only a duplicate of another unique one.
/// </summary>
public static class IndexAnalysis
{
    /// <summary>A finding for each index, in the order given.</summary>
    public static IReadOnlyList<IndexFinding> Analyze(IReadOnlyList<IndexUsage> indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        var findings = new IndexFinding[indexes.Count];
        for (var i = 0; i < indexes.Count; i++)
        {
            findings[i] = Analyze(indexes[i], indexes, i);
        }

        return findings;
    }

    private static IndexFinding Analyze(IndexUsage index, IReadOnlyList<IndexUsage> all, int position)
    {
        if (index.IsClustered || index.IsConstraint || index.KeyColumns.Count == 0)
        {
            return IndexFinding.None;
        }

        for (var j = 0; j < all.Count; j++)
        {
            var other = all[j];
            // A nonclustered index beside the clustered one with the same leading keys is narrower to scan: kept.
            if (j == position || other.IsClustered || other.Table != index.Table || !SameFilter(index, other) || other.KeyColumns.Count == 0)
            {
                continue;
            }

            var holds = Holds(other, index);
            if (SameKeys(index, other) && holds && (!index.IsUnique || other.IsUnique))
            {
                // Of two identical ones, the first is kept: only the later one is reported.
                var identical = Holds(index, other) && index.IsUnique == other.IsUnique && !other.IsConstraint;
                if (!identical || j < position)
                {
                    return new IndexFinding(IndexFindingKind.Duplicate, other.Name);
                }
            }
            else if (!index.IsUnique && IsPrefix(index.KeyColumns, other.KeyColumns) && holds)
            {
                return new IndexFinding(IndexFindingKind.Covered, other.Name);
            }
        }

        return index.Reads == 0 ? new IndexFinding(IndexFindingKind.Unused) : IndexFinding.None;
    }

    private static bool SameFilter(IndexUsage a, IndexUsage b) => string.Equals(a.Filter ?? string.Empty, b.Filter ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static bool SameKeys(IndexUsage a, IndexUsage b) =>
        a.KeyColumns.Count == b.KeyColumns.Count && a.KeyColumns.SequenceEqual(b.KeyColumns, StringComparer.OrdinalIgnoreCase);

    private static bool IsPrefix(IReadOnlyList<string> shorter, IReadOnlyList<string> longer) =>
        shorter.Count < longer.Count && shorter.SequenceEqual(longer.Take(shorter.Count), StringComparer.OrdinalIgnoreCase);

    // Every column of the small index (key or included) is somewhere in the big one.
    private static bool Holds(IndexUsage big, IndexUsage small)
    {
        var columns = big.KeyColumns.Select(Bare).Concat(big.IncludedColumns).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return small.KeyColumns.Select(Bare).Concat(small.IncludedColumns).All(columns.Contains);
    }

    private static string Bare(string key) => key.EndsWith(" DESC", StringComparison.OrdinalIgnoreCase) ? key[..^5] : key;
}
