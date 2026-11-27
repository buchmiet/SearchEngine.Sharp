using System.Buffers;
using SearchEngine.Snapshots;

namespace SearchEngine.Sorting;

// The order of SearchSortMode.NaturalSortAscending: natural sort key, then record id ascending. The sort of the hits
// of one query, the sort permutation of a segment and the placement of delta documents in it all use it, so ties
// come out alike on every path.
internal static class NaturalOrder
{
    // Sorts snapshot ordinals in place by the natural sort key of their sort text, equal keys by record id. An ordinal
    // belongs to second when second is not null and the ordinal is at or past its first ordinal, otherwise to first.
    internal static void Sort(Span<int> ordinals, IndexSegment first, IndexSegment? second)
    {
        if (ordinals.Length < 2)
            return;

        Item[] items = ArrayPool<Item>.Shared.Rent(ordinals.Length);
        try
        {
            Span<Item> span = items.AsSpan(0, ordinals.Length);
            for (int i = 0; i < span.Length; i++)
            {
                int ordinal = ordinals[i];
                var segment = second is not null && ordinal >= second.FirstOrdinal ? second : first;
                int local = ordinal - segment.FirstOrdinal;
                span[i] = new(NaturalSortKeyBuilder.Build(segment.SortTexts[local]), segment.RecordIds[local], ordinal);
            }

            span.Sort(new ItemComparer());

            for (int i = 0; i < span.Length; i++)
                ordinals[i] = span[i].Ordinal;
        }
        finally
        {
            ArrayPool<Item>.Shared.Return(items, clearArray: true);
        }
    }

    // The number of documents of the sorted permutation of segment that come before a document with this key and id.
    internal static int CountBefore(int[] permutation, IndexSegment segment, string key, int id)
    {
        int low = 0;
        int high = permutation.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            int local = permutation[middle] - segment.FirstOrdinal;
            if (Compare(NaturalSortKeyBuilder.Build(segment.SortTexts[local]), segment.RecordIds[local], key, id) < 0)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static int Compare(string key, int id, string otherKey, int otherId)
    {
        int byKey = string.CompareOrdinal(key, otherKey);
        return byKey != 0 ? byKey : id.CompareTo(otherId);
    }

    private readonly record struct Item(string Key, int Id, int Ordinal);

    private readonly struct ItemComparer : IComparer<Item>
    {
        public int Compare(Item x, Item y) => NaturalOrder.Compare(x.Key, x.Id, y.Key, y.Id);
    }
}
