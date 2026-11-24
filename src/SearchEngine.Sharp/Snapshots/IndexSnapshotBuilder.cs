using System.Runtime.InteropServices;
using SearchEngine.Filters;
using SearchEngine.Index;
using SearchEngine.Text;
using SearchEngine.Tokenizer;

namespace SearchEngine.Snapshots;

// ── Build algorithm (four phases) ────────────────────────────────────────────
//
// Phase 1 — Build inverted index (word → list of document ordinals).
//   Each document is tokenized; unique words per document are tracked via a
//   per-call HashSet<string> so that duplicate words within one document do
//   not generate duplicate postings (one entry per document, not per occurrence).
//   Words are canonicalized through WordStringPool so identical strings share
//   the same heap instance, reducing memory when the same word appears across
//   many documents.
//
// Phase 2 — Sort words by descending length.
//   QueryMatcher.MatchWithin relies on being able to break early once it
//   encounters a word shorter than the query. Descending length order makes
//   this loop termination correct.
//
// Phase 3 — Pack into flat arrays.
//   All word characters are concatenated into a single char[] (WordsArray).
//   All posting lists are concatenated into a single int[] (PostingDocIds).
//   Parallel arrays (WordLengths, WordEnds, PostingOffsets, PostingCounts)
//   let the query engine navigate both flat arrays without pointer indirection.
//   This improves cache locality significantly for large indexes.
//
// Phase 4 — Bigram index (lazy, see IndexSnapshot.BigramWordIndices).
//   For every pair of consecutive characters in each word, record the word's
//   ordinal under that bigram key. At query time, MatchWithin looks up only
//   the candidate words that share the query's first bigram, cutting the
//   search space from O(vocab) to O(avg bigram list length). Built on the first
//   Within query against a snapshot, not during Build.

/// <summary>
/// Builds immutable IndexSnapshot instances.
/// </summary>
public static class IndexSnapshotBuilder
{
    /// <summary>
    /// Builds an IndexSnapshot from string entries.
    /// </summary>
    public static IndexSnapshot Build(IEnumerable<KeyValuePair<int, string>> entries)
        => Build(entries, SearchTokenization.Default);

    /// <summary>
    /// Builds an IndexSnapshot from string entries with a tokenization preset.
    /// </summary>
    public static IndexSnapshot Build(IEnumerable<KeyValuePair<int, string>> entries, SearchTokenization tokenization)
        => Build(entries, tokenization, progress: null);

    /// <summary>
    /// Builds an IndexSnapshot from string entries with progress reporting.
    /// </summary>
    public static IndexSnapshot Build(IEnumerable<KeyValuePair<int, string>> entries, IProgress<float>? progress)
        => Build(entries, SearchTokenization.Default, progress);

    /// <summary>
    /// Builds an IndexSnapshot from string entries with a tokenization preset and progress reporting.
    /// </summary>
    public static IndexSnapshot Build(
        IEnumerable<KeyValuePair<int, string>> entries,
        SearchTokenization tokenization,
        IProgress<float>? progress)
    {
        return BuildCore(entries, static text => text, static text => text, static _ => null, tokenization, progress);
    }

    /// <summary>
    /// Builds an IndexSnapshot from IndexedEntry entries.
    /// </summary>
    public static IndexSnapshot Build(IEnumerable<KeyValuePair<int, IndexedEntry>> entries)
        => Build(entries, SearchTokenization.Default);

    /// <summary>
    /// Builds an IndexSnapshot from IndexedEntry entries with a tokenization preset.
    /// </summary>
    public static IndexSnapshot Build(IEnumerable<KeyValuePair<int, IndexedEntry>> entries, SearchTokenization tokenization)
        => Build(entries, tokenization, progress: null);

    /// <summary>
    /// Builds an IndexSnapshot from IndexedEntry entries with progress reporting.
    /// </summary>
    public static IndexSnapshot Build(IEnumerable<KeyValuePair<int, IndexedEntry>> entries, IProgress<float>? progress)
        => Build(entries, SearchTokenization.Default, progress);

    /// <summary>
    /// Builds an IndexSnapshot from IndexedEntry entries with a tokenization preset and progress reporting.
    /// </summary>
    public static IndexSnapshot Build(
        IEnumerable<KeyValuePair<int, IndexedEntry>> entries,
        SearchTokenization tokenization,
        IProgress<float>? progress)
    {
        return BuildCore(entries, static entry => entry.SearchText, static entry => entry.SortText, static entry => entry.Facets, tokenization, progress);
    }

    private static IndexSnapshot BuildCore<TEntry>(
        IEnumerable<KeyValuePair<int, TEntry>> entries,
        Func<TEntry, string> getSearchText,
        Func<TEntry, string> getSortText,
        Func<TEntry, FacetValues?> getFacets,
        SearchTokenization tokenization,
        IProgress<float>? progress)
    {
        var indexSeparators = tokenization.IndexSeparatorValues;
        int entryCount = entries.TryGetNonEnumeratedCount(out int count) ? count : 0;
        var stringPool = new WordStringPool();
        var invertedIndex = entryCount > 0
            ? new Dictionary<string, List<int>>(entryCount, StringComparer.Ordinal)
            : new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var recordIds = entryCount > 0 ? new List<int>(entryCount) : [];
        var sortTexts = entryCount > 0 ? new List<string>(entryCount) : [];
        var uniqueWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var facetKeys = new HashSet<string>(StringComparer.Ordinal);
        var pendingFacetValues = new List<PendingFacetValue>();
        var directFacetColumns = new Dictionary<string, long[]>(StringComparer.Ordinal);

        int totalWordsLength = 0;
        int totalPostings = 0;
        int totalSize = entryCount;
        int documentIndex = 0;

        foreach (var (key, entry) in entries)
        {
            recordIds.Add(key);
            sortTexts.Add(getSortText(entry));

            // BuildWordState is a mutable struct passed by ref so the callback
            // can accumulate running totals (TotalWordsLength, TotalPostings)
            // without boxing or heap allocation.
            var buildState = new BuildWordState(
                documentIndex,
                stringPool,
                invertedIndex,
                totalWordsLength,
                totalPostings);

            TextNormalizer.ForEachUniqueWord(
                getSearchText(entry),
                uniqueWords,
                indexSeparators,
                ref buildState,
                static (ref BuildWordState state, string word) =>
                {
                    word = state.StringPool.Canonicalize(word); // deduplicate string instances

                    // GetValueRefOrAddDefault avoids a double dictionary lookup:
                    // one call to find-or-insert the entry, returning a ref to the list.
                    ref var docIds = ref CollectionsMarshal.GetValueRefOrAddDefault(state.InvertedIndex, word, out bool exists);
                    if (!exists)
                    {
                        docIds = [state.DocumentIndex];
                        state.TotalWordsLength += word.Length;
                    }
                    else
                    {
                        docIds!.Add(state.DocumentIndex);
                    }

                    state.TotalPostings++;
                });

            totalWordsLength = buildState.TotalWordsLength;
            totalPostings = buildState.TotalPostings;

            FacetValues? facets = getFacets(entry);
            if (facets is not null && !facets.IsEmpty)
            {
                for (int f = 0; f < facets.Count; f++)
                {
                    string facetName = facets.NameAt(f);
                    if (entryCount > 0)
                    {
                        // Count known up front: write straight into the column (no pending list).
                        ref long[]? column = ref CollectionsMarshal.GetValueRefOrAddDefault(directFacetColumns, facetName, out _);
                        column ??= new long[entryCount];
                        if (documentIndex < column.Length)
                        {
                            column[documentIndex] = facets.ValueAt(f);
                            continue;
                        }
                    }

                    facetKeys.Add(facetName);
                    pendingFacetValues.Add(new PendingFacetValue(documentIndex, facetName, facets.ValueAt(f)));
                }
            }

            progress?.Report(totalSize > 0 ? (float)documentIndex / totalSize * 50.0f : 50.0f);
            documentIndex++;
        }

        if (recordIds.Count == 0)
            return IndexSnapshot.Empty;

        var wordDescriptions = new List<WordDescription>(invertedIndex.Count);
        foreach (var (word, docIds) in invertedIndex)
            wordDescriptions.Add(new WordDescription(word, docIds));

        // Phase 2: sort descending by word length.
        // QueryMatcher.MatchWithin breaks as soon as it sees a word shorter than the
        // query, so this ordering is load-bearing — not just cosmetic.
        CollectionsMarshal.AsSpan(wordDescriptions).Sort(static (a, b) => b.Length.CompareTo(a.Length));

        var wordsArray = new char[totalWordsLength];
        var postingDocIds = new int[totalPostings];
        var postingCounts = new int[wordDescriptions.Count];
        var wordLengths = new int[wordDescriptions.Count];
        var wordEnds = new int[wordDescriptions.Count];
        var postingOffsets = new int[wordDescriptions.Count];
        var exactPostings = new Dictionary<string, WordPostings>(wordDescriptions.Count, StringComparer.Ordinal);

        // Phase 3: pack into flat arrays.
        var wordsSpan = wordsArray.AsSpan();
        var postingsSpan = postingDocIds.AsSpan();
        var wordDescSpan = CollectionsMarshal.AsSpan(wordDescriptions);

        int wordsOffset = 0;
        int postingOffset = 0;

        for (int i = 0; i < wordDescSpan.Length; i++)
        {
            ref readonly var item = ref wordDescSpan[i];
            var word = item.Word;
            var docIds = item.DocumentIds;

            word.AsSpan().CopyTo(wordsSpan[wordsOffset..]);

            postingCounts[i] = docIds.Count;
            postingOffsets[i] = postingOffset;
            exactPostings[word] = new WordPostings(postingOffset, docIds.Count);

            foreach (var docId in docIds)
                postingsSpan[postingOffset++] = docId;

            wordsOffset += word.Length;
            wordLengths[i] = word.Length;
            wordEnds[i] = wordsOffset; // exclusive end; word starts at wordsOffset - word.Length

            progress?.Report(50.0f + ((float)i / wordDescSpan.Length * 50.0f));
        }

        // Phase 4 (bigram index) is built lazily by IndexSnapshot on the first Within query:
        // Exact, glob and regex queries never read it, and it is the largest build phase.

        var recordIdsArr = recordIds.ToArray();
        var facetColumns = BuildFacetColumns(recordIdsArr.Length, facetKeys, pendingFacetValues, directFacetColumns);

        return new IndexSnapshot(
            recordIds: recordIdsArr,
            exactPostings: exactPostings,
            wordsArray: wordsArray,
            postingDocIds: postingDocIds,
            postingOffsets: postingOffsets,
            wordEnds: wordEnds,
            wordLengths: wordLengths,
            postingCounts: postingCounts,
            bigramWordIndices: null,
            sortTexts: [.. sortTexts],
            sortedPermutation: null,
            facetColumns: facetColumns,
            tokenization: tokenization,
            documentCount: recordIdsArr.Length,
            uniqueWordCount: stringPool.Count
        );
    }

    private static Dictionary<string, long[]> BuildFacetColumns(
        int documentCount,
        HashSet<string> facetKeys,
        List<PendingFacetValue> pendingFacetValues,
        Dictionary<string, long[]> directFacetColumns)
    {
        if (facetKeys.Count == 0 && directFacetColumns.Count == 0)
            return [];

        var facetColumns = new Dictionary<string, long[]>(facetKeys.Count + directFacetColumns.Count, StringComparer.Ordinal);
        foreach (var (facetName, column) in directFacetColumns)
        {
            var sized = column;
            if (sized.Length != documentCount)
                Array.Resize(ref sized, documentCount); // the enumerated count differed from the reported count
            facetColumns[facetName] = sized;
        }

        foreach (string facetName in facetKeys)
        {
            if (!facetColumns.ContainsKey(facetName))
                facetColumns[facetName] = new long[documentCount];
        }

        foreach (var pending in pendingFacetValues)
            facetColumns[pending.FacetName][pending.DocumentIndex] = pending.Value;

        return facetColumns;
    }

    private readonly record struct PendingFacetValue(int DocumentIndex, string FacetName, long Value);

    private readonly struct WordDescription(string word, List<int> documentIds)
    {
        public string Word { get; } = word;
        public List<int> DocumentIds { get; } = documentIds;
        public int Length { get; } = word.Length;
    }

    private struct BuildWordState(
        int documentIndex,
        WordStringPool stringPool,
        Dictionary<string, List<int>> invertedIndex,
        int totalWordsLength,
        int totalPostings)
    {
        public int DocumentIndex { get; } = documentIndex;
        public WordStringPool StringPool { get; } = stringPool;
        public Dictionary<string, List<int>> InvertedIndex { get; } = invertedIndex;
        public int TotalWordsLength { get; set; } = totalWordsLength;
        public int TotalPostings { get; set; } = totalPostings;
    }
}
