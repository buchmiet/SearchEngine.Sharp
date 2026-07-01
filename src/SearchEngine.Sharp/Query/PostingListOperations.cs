namespace SearchEngine.Query;

internal static class PostingListOperations
{
    // Translates document ordinals (positions in the RecordIds array) into the
    // caller-supplied IDs. Used only for the exact single-word fast path where
    // postings are read directly from ExactPostings without a bitset.
    public static List<int> MaterializeRecordIds(ReadOnlySpan<int> recordIds, ReadOnlySpan<int> docOrdinals)
    {
        if (docOrdinals.Length == 0)
            return [];

        var results = new List<int>(docOrdinals.Length);
        for (int i = 0; i < docOrdinals.Length; i++)
            results.Add(recordIds[docOrdinals[i]]);

        return results;
    }
}
