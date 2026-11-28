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
        // One segment: the ordinal indexes RecordIds directly, with no SegmentOf per ordinal.
        if (snapshot.Delta is null)
        {
            var recordIds = snapshot.Base.RecordIds.AsSpan();
            for (int i = 0; i < ordinals.Length; i++)
                results.Add(recordIds[ordinals[i]]);
        }
        else
        {
            for (int i = 0; i < ordinals.Length; i++)
                results.Add(snapshot.RecordIdAt(ordinals[i]));
        }

        return results;
    }

    // Translates the live postings of a token into record ids, in ascending ordinal order.
    public static List<int> MaterializeRecordIds(IndexSnapshot snapshot, LivePostings postings)
    {
        if (postings.TryGetSpan(out var ordinals))
            return MaterializeRecordIds(snapshot, ordinals);

        int count = postings.Count;
        if (count == 0)
            return [];

        var results = new List<int>(count);
        foreach (int ordinal in postings)
            results.Add(snapshot.RecordIdAt(ordinal));

        return results;
    }
}
