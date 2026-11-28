using SearchEngine.Snapshots;

namespace SearchEngine.Query;

internal static class PostingListOperations
{
    // Translates snapshot ordinals into record ids, in the order given.
    public static List<int> MaterializeRecordIds(IndexSnapshot snapshot, ReadOnlySpan<int> ordinals)
    {
        if (ordinals.Length == 0)
            return [];

        var results = new List<int>(ordinals.Length);
        // One segment: the ordinal indexes RecordIds directly, with no SegmentIndexOf per ordinal.
        if (snapshot.Segments.Length == 1)
        {
            var recordIds = snapshot.Segments[0].RecordIds.AsSpan();
            for (int i = 0; i < ordinals.Length; i++)
                results.Add(recordIds[ordinals[i]]);
        }
        else
        {
            // The ordinals of a query come in ascending or sorted order, so the segment of the previous ordinal is
            // usually the segment of the next one.
            var offsets = snapshot.Offsets;
            var segments = snapshot.Segments;
            int s = 0;
            for (int i = 0; i < ordinals.Length; i++)
            {
                int ordinal = ordinals[i];
                if (ordinal < offsets[s] || s + 1 < offsets.Length && ordinal >= offsets[s + 1])
                    s = snapshot.SegmentIndexOf(ordinal);
                results.Add(segments[s].RecordIds[ordinal - offsets[s]]);
            }
        }

        return results;
    }

    // Translates the first limit live postings of a token into record ids, in ascending ordinal order.
    public static List<int> MaterializeRecordIds(IndexSnapshot snapshot, LivePostings postings, int limit = int.MaxValue)
    {
        if (postings.TryGetSpan(out var ordinals))
            return MaterializeRecordIds(snapshot, ordinals[..Math.Min(ordinals.Length, limit)]);

        var results = new List<int>(Math.Min(postings.UpperBound, limit));
        postings.AppendRecordIdsTo(results, limit);
        return results;
    }
}
