using System.Buffers;

namespace SearchEngine.Sorting;

// The order of SearchSortMode.NaturalSortAscending: natural sort key, then record id ascending. Both the sort of
// the hits of one query and the sort permutation of a whole snapshot use it, so ties come out alike on every path.
internal static class NaturalOrder
{
    // Sorts document ordinals in place by the natural sort key of their sort text, equal keys by record id.
    internal static void Sort(Span<int> ordinals, string[] sortTexts, int[] recordIds)
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
                span[i] = new(NaturalSortKeyBuilder.Build(sortTexts[ordinal]), recordIds[ordinal], ordinal);
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

    private readonly record struct Item(string Key, int Id, int Ordinal);

    private readonly struct ItemComparer : IComparer<Item>
    {
        public int Compare(Item x, Item y)
        {
            int byKey = string.CompareOrdinal(x.Key, y.Key);
            return byKey != 0 ? byKey : x.Id.CompareTo(y.Id);
        }
    }
}
