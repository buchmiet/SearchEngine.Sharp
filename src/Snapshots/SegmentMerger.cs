using System.Runtime.InteropServices;

namespace SearchEngine.Snapshots;

// Merges adjacent segments into one without reading the original texts: the posting lists of the inputs are
// concatenated token by token, the dead documents are dropped and the remaining documents are renumbered.
//
// ── Cost ──
// Linear in the total number of postings and distinct tokens of the inputs, plus one pass over their per-document
// arrays. No text is tokenized again. IndexUpdater runs a merge on a background thread, off the write path.
//
// ── Order ──
// The documents keep the order of the inputs and, inside an input, their own order, so a merge never changes the
// relative order of two documents (SearchSortMode.SnapshotOrder). Every posting list stays ascending because the
// local ordinals of a later input all lie above those of an earlier one.
internal static class SegmentMerger
{
    // inputs in snapshot order; dead[i] holds the dead documents of inputs[i] at the time the merge starts, or is null.
    // Returns IndexSegment.Empty when every document is dead.
    internal static IndexSegment Merge(IReadOnlyList<IndexSegment> inputs, IReadOnlyList<DeadSet?> dead)
    {
        // remap[i][local] = the ordinal in the merged segment, or -1 for a dead document.
        var remaps = new int[inputs.Count][];
        int liveTotal = 0;
        for (int i = 0; i < inputs.Count; i++)
        {
            var segment = inputs[i];
            var remap = remaps[i] = new int[segment.DocumentCount];
            var deadSet = dead[i];
            for (int local = 0; local < remap.Length; local++)
                remap[local] = deadSet is not null && deadSet.Contains(local) ? -1 : liveTotal++;
        }

        if (liveTotal == 0)
            return IndexSegment.Empty;

        var recordIds = new int[liveTotal];
        var sortTexts = new string[liveTotal];
        Dictionary<string, long[]> facetColumns = [with(StringComparer.Ordinal)];
        for (int i = 0; i < inputs.Count; i++)
        {
            var segment = inputs[i];
            var remap = remaps[i];
            for (int local = 0; local < remap.Length; local++)
            {
                int to = remap[local];
                if (to < 0)
                    continue;

                recordIds[to] = segment.RecordIds[local];
                sortTexts[to] = segment.SortTexts[local];
            }

            foreach (var (facet, column) in segment.FacetColumns)
            {
                ref long[]? merged = ref CollectionsMarshal.GetValueRefOrAddDefault(facetColumns, facet, out _);
                merged ??= new long[liveTotal]; // a document without a value for a facet has the value 0
                for (int local = 0; local < remap.Length; local++)
                {
                    if (remap[local] >= 0)
                        merged[remap[local]] = column[local];
                }
            }
        }

        int distinctCap = 0;
        foreach (var segment in inputs)
            distinctCap = Math.Max(distinctCap, segment.WordCount);

        Dictionary<string, List<int>> invertedIndex = [with(distinctCap, StringComparer.Ordinal)];
        int totalWordsLength = 0;
        int totalPostings = 0;
        for (int i = 0; i < inputs.Count; i++)
        {
            var segment = inputs[i];
            var remap = remaps[i];
            foreach (var (word, range) in segment.ExactPostings)
            {
                var postings = segment.PostingDocIds.AsSpan(range.Offset, range.Count);
                ref List<int>? list = ref CollectionsMarshal.GetValueRefOrAddDefault(invertedIndex, word, out bool exists);
                foreach (int local in postings)
                {
                    int to = remap[local];
                    if (to < 0)
                        continue;

                    if (list is null)
                    {
                        list = [];
                        totalWordsLength += word.Length;
                    }

                    list.Add(to);
                    totalPostings++;
                }

                // A token whose documents are all dead leaves no entry.
                if (!exists && list is null)
                    invertedIndex.Remove(word);
            }
        }

        var mergedSegment = IndexSnapshotBuilder.Pack(invertedIndex, totalWordsLength, totalPostings, recordIds, sortTexts, facetColumns);
        _ = mergedSegment.Ids; // the updater probes it on the next change of an id: build it here, off the write path
        return mergedSegment;
    }
}
