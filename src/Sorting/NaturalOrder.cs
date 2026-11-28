using System.Buffers;
using System.Diagnostics;
using SearchEngine.Snapshots;

namespace SearchEngine.Sorting;

// The order of SearchSortMode.NaturalSortAscending: natural sort key, then record id ascending. The sort of the hits
// of one query, the sort permutation of a segment and the placement of the documents of the other segments in it all
// use it, so ties come out alike on every path.
internal static class NaturalOrder
{
    // Sorts the local ordinals of one segment in place by the natural sort key of their sort text, equal keys by record
    // id.
    internal static void Sort(Span<int> ordinals, IndexSegment segment)
    {
        if (ordinals.Length < 2)
            return;

        Item[] items = ArrayPool<Item>.Shared.Rent(ordinals.Length);
        try
        {
            Span<Item> span = items.AsSpan(0, ordinals.Length);
            for (int i = 0; i < span.Length; i++)
                span[i] = new(NaturalSortKeyBuilder.Build(segment.SortTexts[ordinals[i]]), segment.RecordIds[ordinals[i]], ordinals[i]);

            span.Sort(new ItemComparer());
            for (int i = 0; i < span.Length; i++)
                ordinals[i] = span[i].Ordinal;
        }
        finally
        {
            ArrayPool<Item>.Shared.Return(items, clearArray: true);
        }
    }

    // Sorts snapshot ordinals in place by the natural sort key of their sort text, equal keys by record id.
    internal static void Sort(Span<int> ordinals, IndexSnapshot snapshot)
        => Sort(ordinals, snapshot, keys: default);

    // Sort, which also writes the key of each ordinal to keys in the sorted order when keys is not empty (then it is
    // as long as ordinals), so a caller that needs the keys does not build them again.
    internal static void Sort(Span<int> ordinals, IndexSnapshot snapshot, Span<string> keys)
    {
        Debug.Assert(keys.IsEmpty || keys.Length == ordinals.Length);
        if (ordinals.Length < 2)
        {
            if (!keys.IsEmpty && ordinals.Length == 1)
                keys[0] = ItemOf(ordinals[0], snapshot).Key;
            return;
        }

        Item[] items = ArrayPool<Item>.Shared.Rent(ordinals.Length);
        try
        {
            Span<Item> span = items.AsSpan(0, ordinals.Length);
            for (int i = 0; i < span.Length; i++)
                span[i] = ItemOf(ordinals[i], snapshot);

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

    // Writes to destination the ordinals of the destination.Length first documents of hits in natural order (key, then
    // record id), without sorting the others: a max-heap keeps the best destination.Length documents seen so far, and
    // a document that does not beat its root is dropped after one comparison without allocating its key. Cost:
    // one key per hit and a heap update for each hit that enters, so O(H + L log L log H) for H hits and L = limit.
    // destination is not longer than the number of hits.
    internal static void SelectFirst(Index.FastBitSet hits, IndexSnapshot snapshot, Span<int> destination)
    {
        int limit = destination.Length;
        if (limit == 0)
            return;

        Item[] heap = ArrayPool<Item>.Shared.Rent(limit);
        char[]? rented = null;
        Span<char> buffer = stackalloc char[NaturalSortKeyBuilder.MaxStackKeyLength];
        try
        {
            int size = 0;
            foreach (int ordinal in hits.EnumerateSetBits())
            {
                int s = snapshot.SegmentIndexOf(ordinal);
                var segment = snapshot.Segments[s];
                int local = ordinal - snapshot.Offsets[s];
                string text = segment.SortTexts[local];
                int id = segment.RecordIds[local];

                int maxLength = NaturalSortKeyBuilder.MaxKeyLength(text.Length);
                Span<char> keyBuffer = buffer;
                if (maxLength > keyBuffer.Length)
                {
                    if (rented is null || rented.Length < maxLength)
                    {
                        if (rented is not null)
                            ArrayPool<char>.Shared.Return(rented);
                        rented = null;
                        rented = ArrayPool<char>.Shared.Rent(maxLength);
                    }

                    keyBuffer = rented;
                }

                ReadOnlySpan<char> key = keyBuffer[..NaturalSortKeyBuilder.BuildInto(text, keyBuffer)];
                if (size == limit)
                {
                    if (Compare(key, id, heap[0].Key, heap[0].Id) >= 0)
                        continue;

                    heap[0] = new(new string(key), id, ordinal);
                    SiftDown(heap, size);
                }
                else
                {
                    heap[size] = new(new string(key), id, ordinal);
                    SiftUp(heap, size++);
                }
            }

            Span<Item> best = heap.AsSpan(0, size);
            best.Sort(new ItemComparer());
            for (int i = 0; i < best.Length; i++)
                destination[i] = best[i].Ordinal;
        }
        finally
        {
            ArrayPool<Item>.Shared.Return(heap, clearArray: true);
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    // The heap keeps its greatest item at the root.
    private static void SiftUp(Item[] heap, int index)
    {
        var item = heap[index];
        while (index > 0)
        {
            int parent = (index - 1) >> 1;
            if (Compare(heap[parent].Key, heap[parent].Id, item.Key, item.Id) >= 0)
                break;

            heap[index] = heap[parent];
            index = parent;
        }

        heap[index] = item;
    }

    private static void SiftDown(Item[] heap, int size)
    {
        var item = heap[0];
        int index = 0;
        while (true)
        {
            int child = 2 * index + 1;
            if (child >= size)
                break;

            if (child + 1 < size && Compare(heap[child + 1].Key, heap[child + 1].Id, heap[child].Key, heap[child].Id) > 0)
                child++;

            if (Compare(heap[child].Key, heap[child].Id, item.Key, item.Id) <= 0)
                break;

            heap[index] = heap[child];
            index = child;
        }

        heap[index] = item;
    }

    // For each document given by its key and record id, the number of documents of the sort permutation of segment
    // (local ordinals) that come before it. The documents come in ascending order (as Sort leaves them), so each binary search starts
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
                    int local = permutation[middle];
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

    private static Item ItemOf(int ordinal, IndexSnapshot snapshot)
    {
        int s = snapshot.SegmentIndexOf(ordinal);
        var segment = snapshot.Segments[s];
        int local = ordinal - snapshot.Offsets[s];
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
