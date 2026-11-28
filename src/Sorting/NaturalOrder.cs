using System.Buffers;
using System.Diagnostics;
using SearchEngine.Snapshots;

namespace SearchEngine.Sorting;

// The order of SearchSortMode.NaturalSortAscending: natural sort key, then record id ascending. The sort of the hits
// of one query, the sort permutation of a segment and the placement of delta documents in it all use it, so ties
// come out alike on every path.
internal static class NaturalOrder
{
    // Sorts snapshot ordinals in place by the natural sort key of their sort text, equal keys by record id. first and
    // second split the ordinals as the base and the delta do in IndexSnapshot.SegmentOf.
    internal static void Sort(Span<int> ordinals, IndexSegment first, IndexSegment? second)
        => Sort(ordinals, first, second, keys: default);

    // Sort, which also writes the key of each ordinal to keys in the sorted order when keys is not empty (then it is
    // as long as ordinals), so a caller that needs the keys does not build them again.
    internal static void Sort(Span<int> ordinals, IndexSegment first, IndexSegment? second, Span<string> keys)
    {
        Debug.Assert(keys.IsEmpty || keys.Length == ordinals.Length);
        if (ordinals.Length < 2)
        {
            if (!keys.IsEmpty)
                keys[0] = ItemOf(ordinals[0], first, second).Key;
            return;
        }

        Item[] items = ArrayPool<Item>.Shared.Rent(ordinals.Length);
        try
        {
            Span<Item> span = items.AsSpan(0, ordinals.Length);
            for (int i = 0; i < span.Length; i++)
                span[i] = ItemOf(ordinals[i], first, second);

            span.Sort(new ItemComparer());

            for (int i = 0; i < span.Length; i++)
                ordinals[i] = span[i].Ordinal;

            if (!keys.IsEmpty)
            {
                for (int i = 0; i < span.Length; i++)
                    keys[i] = span[i].Key;
            }
        }
        finally
        {
            ArrayPool<Item>.Shared.Return(items, clearArray: true);
        }
    }

    // For each document given by its key and record id, the number of documents of the sort permutation of segment
    // that come before it. The documents come in ascending order (as Sort leaves them), so each binary search starts
    // at the count of the one before. Cost: at most log2(permutation length) comparisons per document; each builds the
    // key of a segment document into one buffer reused for all documents, so the comparisons allocate no strings.
    internal static void CountBefore(int[] permutation, IndexSegment segment, ReadOnlySpan<string> keys, ReadOnlySpan<int> ids, Span<int> counts)
    {
        Debug.Assert(keys.Length == ids.Length && keys.Length == counts.Length);
        Span<char> stackBuffer = stackalloc char[NaturalSortKeyBuilder.MaxStackKeyLength];
        char[]? rented = null;
        try
        {
            int low = 0;
            for (int k = 0; k < keys.Length; k++)
            {
                int high = permutation.Length;
                while (low < high)
                {
                    int middle = low + ((high - low) >> 1);
                    int local = permutation[middle] - segment.FirstOrdinal;
                    string text = segment.SortTexts[local];
                    int maxLength = NaturalSortKeyBuilder.MaxKeyLength(text.Length);
                    Span<char> buffer = stackBuffer;
                    if (maxLength > buffer.Length)
                    {
                        if (rented is null || rented.Length < maxLength)
                        {
                            if (rented is not null)
                                ArrayPool<char>.Shared.Return(rented);
                            rented = null;
                            rented = ArrayPool<char>.Shared.Rent(maxLength);
                        }

                        buffer = rented;
                    }

                    int length = NaturalSortKeyBuilder.BuildInto(text, buffer);
                    if (Compare(buffer[..length], segment.RecordIds[local], keys[k], ids[k]) < 0)
                        low = middle + 1;
                    else
                        high = middle;
                }

                counts[k] = low;
            }
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static Item ItemOf(int ordinal, IndexSegment first, IndexSegment? second)
    {
        var segment = second is not null && ordinal >= second.FirstOrdinal ? second : first;
        int local = ordinal - segment.FirstOrdinal;
        return new(NaturalSortKeyBuilder.Build(segment.SortTexts[local]), segment.RecordIds[local], ordinal);
    }

    private static int Compare(string key, int id, string otherKey, int otherId)
    {
        int byKey = string.CompareOrdinal(key, otherKey);
        return byKey != 0 ? byKey : id.CompareTo(otherId);
    }

    // The same order as the string overload: SequenceCompareTo compares UTF-16 code units like string.CompareOrdinal.
    private static int Compare(ReadOnlySpan<char> key, int id, ReadOnlySpan<char> otherKey, int otherId)
    {
        int byKey = key.SequenceCompareTo(otherKey);
        return byKey != 0 ? byKey : id.CompareTo(otherId);
    }

    private readonly record struct Item(string Key, int Id, int Ordinal);

    private readonly struct ItemComparer : IComparer<Item>
    {
        public int Compare(Item x, Item y) => NaturalOrder.Compare(x.Key, x.Id, y.Key, y.Id);
    }
}
