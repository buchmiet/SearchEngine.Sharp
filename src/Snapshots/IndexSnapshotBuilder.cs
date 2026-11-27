using System.Runtime.InteropServices;
using SearchEngine.Filters;
using SearchEngine.Index;
using SearchEngine.Text;
using SearchEngine.Tokenizer;

namespace SearchEngine.Snapshots;

// ── Build algorithm (four phases) ────────────────────────────────────────────
//
// Phase 1: Build the inverted index (word → list of document ordinals).
//   The text of each document is folded (SearchTokenization.FoldIndexText; IndexUpdater stores it folded
//   already) and tokenized.
//   One HashSet<string>, cleared for every document, tracks the unique words of the
//   current document, so duplicate words within one document do not generate
//   duplicate postings (one entry per document, not per occurrence).
//   Words are canonicalized through WordStringPool so identical strings share
//   the same heap instance, reducing memory when the same word appears across
//   many documents.
//
// Phase 2: Sort words by descending length.
//   QueryMatcher.MatchWithin relies on being able to break early once it
//   encounters a word shorter than the query. Descending length order makes
//   this loop termination correct.
//
// Phase 3: Pack into flat arrays.
//   All word characters are concatenated into a single char[] (WordsArray).
//   All posting lists are concatenated into a single int[] (PostingDocIds).
//   Parallel arrays (WordLengths, WordEnds, PostingOffsets, PostingCounts)
//   let the query engine navigate both flat arrays without pointer indirection,
//   which keeps the data contiguous in memory.
//
// Phase 4: Bigram index (lazy, see IndexSnapshot.BigramWordIndices).
//   For every pair of consecutive characters in each word, record the word's
//   ordinal under that bigram key. At query time, MatchWithin looks up only
//   the candidate words that share the query's first bigram, cutting the
//   search space from O(vocab) to O(avg bigram list length). Built on the first
//   Within query against a snapshot, not during Build.

/// <summary>
/// Builds an <see cref="IndexSnapshot"/> from a set of entries without an <see cref="IndexUpdater"/>.
/// </summary>
/// <remarks>
/// <para>The build takes time and memory linear in the total length of the search texts, plus sorting the distinct
/// tokens. Each call is independent and safe to run on several threads at once.</para>
/// <para>An id names one document, so an id that occurs twice in the entries throws
/// <see cref="ArgumentException"/>. The check is skipped for an <see cref="IDictionary{TKey, TValue}"/> or an
/// <see cref="IReadOnlyDictionary{TKey, TValue}"/>, whose keys are unique.</para>
/// <para>A document without a value for a facet that other documents carry gets 0 in that facet.</para>
/// <para>The progress values are percentages: the first half covers reading the entries, the second half packing
/// the tokens. <see cref="IProgress{T}.Report"/> is called on the building thread when the value reaches a new whole
/// percent, so a build of any size makes at most 102 reports, and the last report is 100, made when the snapshot is
/// complete. Each value is the total progress so far, so a handler that skips reports loses nothing. When the
/// entries do not expose their count without enumerating, the first half is reported once, as 50, after they are
/// read. No entries give a single report of 100.</para>
/// <para>To query a snapshot built here, return it from your own <see cref="IIndexSnapshotProvider"/> and pass that
/// provider to <see cref="SearchEngineSharp"/>.</para>
/// </remarks>
public static class IndexSnapshotBuilder
{
    /// <summary>
    /// Builds a snapshot with <see cref="SearchTokenization.Default"/> from string entries: each text is searched
    /// and sorted, and no entry has facets.
    /// </summary>
    /// <param name="entries">The documents, keyed by the id that queries return. They are enumerated once, in the
    /// order that becomes <see cref="SearchSortMode.SnapshotOrder"/>.</param>
    /// <returns>The new snapshot. No entries give an empty snapshot that keeps the preset.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An id occurs more than once in <paramref name="entries"/>, or an entry is
    /// <see langword="null"/>.</exception>
    public static IndexSnapshot Build(IEnumerable<KeyValuePair<int, string>> entries)
        => Build(entries, SearchTokenization.Default);

    /// <summary>
    /// Builds a snapshot from string entries: each text is searched and sorted, and the entries have no facets.
    /// </summary>
    /// <param name="entries">The documents, keyed by the id that queries return. They are enumerated once, in the
    /// order that becomes <see cref="SearchSortMode.SnapshotOrder"/>.</param>
    /// <param name="tokenization">The preset that splits and folds the text. The snapshot keeps it for its
    /// queries.</param>
    /// <returns>The new snapshot. No entries give an empty snapshot that keeps the preset.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> or <paramref name="tokenization"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An id occurs more than once in <paramref name="entries"/>, or an entry is
    /// <see langword="null"/>.</exception>
    public static IndexSnapshot Build(IEnumerable<KeyValuePair<int, string>> entries, SearchTokenization tokenization)
        => Build(entries, tokenization, progress: null);

    /// <summary>
    /// Builds a snapshot with <see cref="SearchTokenization.Default"/> from string entries: each text is searched
    /// and sorted, and no entry has facets.
    /// </summary>
    /// <param name="entries">The documents, keyed by the id that queries return. They are enumerated once, in the
    /// order that becomes <see cref="SearchSortMode.SnapshotOrder"/>.</param>
    /// <param name="progress">Receives the build progress from 0 to 100, or <see langword="null"/>.</param>
    /// <returns>The new snapshot. No entries give an empty snapshot that keeps the preset.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An id occurs more than once in <paramref name="entries"/>, or an entry is
    /// <see langword="null"/>.</exception>
    public static IndexSnapshot Build(IEnumerable<KeyValuePair<int, string>> entries, IProgress<float>? progress)
        => Build(entries, SearchTokenization.Default, progress);

    /// <summary>
    /// Builds a snapshot from string entries: each text is searched and sorted, and the entries have no facets.
    /// </summary>
    /// <param name="entries">The documents, keyed by the id that queries return. They are enumerated once, in the
    /// order that becomes <see cref="SearchSortMode.SnapshotOrder"/>.</param>
    /// <param name="tokenization">The preset that splits and folds the text. The snapshot keeps it for its
    /// queries.</param>
    /// <param name="progress">Receives the build progress from 0 to 100, or <see langword="null"/>.</param>
    /// <returns>The new snapshot. No entries give an empty snapshot that keeps the preset.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> or <paramref name="tokenization"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An id occurs more than once in <paramref name="entries"/>, or an entry is
    /// <see langword="null"/>.</exception>
    public static IndexSnapshot Build(
        IEnumerable<KeyValuePair<int, string>> entries,
        SearchTokenization tokenization,
        IProgress<float>? progress)
    {
        ArgumentNullException.ThrowIfNull(tokenization);
        return BuildCore(entries, tokenization.FoldIndexText, static text => text, static _ => null, tokenization, progress);
    }

    /// <summary>
    /// Builds a snapshot with <see cref="SearchTokenization.Default"/> from <see cref="IndexedEntry"/> values.
    /// </summary>
    /// <param name="entries">The documents, keyed by the id that queries return. They are enumerated once, in the
    /// order that becomes <see cref="SearchSortMode.SnapshotOrder"/>.</param>
    /// <returns>The new snapshot. No entries give an empty snapshot that keeps the preset.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An id occurs more than once in <paramref name="entries"/>, or an entry is
    /// <see langword="null"/>.</exception>
    public static IndexSnapshot Build(IEnumerable<KeyValuePair<int, IndexedEntry>> entries)
        => Build(entries, SearchTokenization.Default);

    /// <summary>
    /// Builds a snapshot from <see cref="IndexedEntry"/> values.
    /// </summary>
    /// <param name="entries">The documents, keyed by the id that queries return. They are enumerated once, in the
    /// order that becomes <see cref="SearchSortMode.SnapshotOrder"/>.</param>
    /// <param name="tokenization">The preset that splits and folds the text. The snapshot keeps it for its
    /// queries.</param>
    /// <returns>The new snapshot. No entries give an empty snapshot that keeps the preset.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> or <paramref name="tokenization"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An id occurs more than once in <paramref name="entries"/>, or an entry is
    /// <see langword="null"/>.</exception>
    public static IndexSnapshot Build(IEnumerable<KeyValuePair<int, IndexedEntry>> entries, SearchTokenization tokenization)
        => Build(entries, tokenization, progress: null);

    /// <summary>
    /// Builds a snapshot with <see cref="SearchTokenization.Default"/> from <see cref="IndexedEntry"/> values.
    /// </summary>
    /// <param name="entries">The documents, keyed by the id that queries return. They are enumerated once, in the
    /// order that becomes <see cref="SearchSortMode.SnapshotOrder"/>.</param>
    /// <param name="progress">Receives the build progress from 0 to 100, or <see langword="null"/>.</param>
    /// <returns>The new snapshot. No entries give an empty snapshot that keeps the preset.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An id occurs more than once in <paramref name="entries"/>, or an entry is
    /// <see langword="null"/>.</exception>
    public static IndexSnapshot Build(IEnumerable<KeyValuePair<int, IndexedEntry>> entries, IProgress<float>? progress)
        => Build(entries, SearchTokenization.Default, progress);

    /// <summary>
    /// Builds a snapshot from <see cref="IndexedEntry"/> values.
    /// </summary>
    /// <param name="entries">The documents, keyed by the id that queries return. They are enumerated once, in the
    /// order that becomes <see cref="SearchSortMode.SnapshotOrder"/>.</param>
    /// <param name="tokenization">The preset that splits and folds the text. The snapshot keeps it for its
    /// queries.</param>
    /// <param name="progress">Receives the build progress from 0 to 100, or <see langword="null"/>.</param>
    /// <returns>The new snapshot. No entries give an empty snapshot that keeps the preset.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> or <paramref name="tokenization"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An id occurs more than once in <paramref name="entries"/>, or an entry is
    /// <see langword="null"/>.</exception>
    public static IndexSnapshot Build(
        IEnumerable<KeyValuePair<int, IndexedEntry>> entries,
        SearchTokenization tokenization,
        IProgress<float>? progress)
    {
        ArgumentNullException.ThrowIfNull(tokenization);
        return BuildCore(entries, entry => tokenization.FoldIndexText(entry.SearchText), static entry => entry.SortText, static entry => entry.Facets, tokenization, progress);
    }

    // Entries whose SearchText is already folded with tokenization (SearchTokenization.FoldEntry): IndexUpdater folds
    // each entry once when it comes in, so a rebuild after a change does not fold every stored text again.
    internal static IndexSnapshot BuildFolded(
        IEnumerable<KeyValuePair<int, IndexedEntry>> entries,
        SearchTokenization tokenization,
        IProgress<float>? progress = null)
    {
        return BuildCore(entries, static entry => entry.SearchText, static entry => entry.SortText, static entry => entry.Facets, tokenization, progress);
    }

    // getSearchText returns the folded search text of an entry.
    private static IndexSnapshot BuildCore<TEntry>(
        IEnumerable<KeyValuePair<int, TEntry>> entries,
        Func<TEntry, string> getSearchText,
        Func<TEntry, string> getSortText,
        Func<TEntry, FacetValues?> getFacets,
        SearchTokenization tokenization,
        IProgress<float>? progress)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(tokenization);
        var indexSeparators = tokenization.IndexSeparatorValues;
        int entryCount = entries.TryGetNonEnumeratedCount(out int count) ? count : 0;
        var stringPool = new WordStringPool();
        Dictionary<string, List<int>> invertedIndex = [with(entryCount, StringComparer.Ordinal)];
        List<int> recordIds = [with(capacity: entryCount)];
        List<string> sortTexts = [with(capacity: entryCount)];
        HashSet<string> uniqueWords = [with(StringComparer.Ordinal)];
        HashSet<string> facetKeys = [with(StringComparer.Ordinal)];
        List<PendingFacetValue> pendingFacetValues = [];
        Dictionary<string, long[]> directFacetColumns = [with(StringComparer.Ordinal)];

        // A dictionary cannot hold an id twice; any other sequence is checked.
        HashSet<int>? seenIds = entries is IDictionary<int, TEntry> or IReadOnlyDictionary<int, TEntry>
            ? null
            : entryCount > 0 ? new HashSet<int>(entryCount) : [];

        int totalWordsLength = 0;
        int totalPostings = 0;
        int totalSize = entryCount;
        int documentIndex = 0;
        var reporter = new PercentReporter(progress);

        foreach (var (key, entry) in entries)
        {
            if (entry is null)
                throw IndexedEntry.NullElement(key, nameof(entries));

            if (seenIds is not null && !seenIds.Add(key))
                throw new ArgumentException($"The id {key} occurs more than once. Each id must name one document.", nameof(entries));

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
                    pendingFacetValues.Add(new(documentIndex, facetName, facets.ValueAt(f)));
                }
            }

            if (totalSize > 0)
                reporter.Report(Math.Min(50.0f, (float)documentIndex / totalSize * 50.0f));
            documentIndex++;
        }

        if (recordIds.Count == 0)
        {
            reporter.Complete();
            return IndexSnapshot.EmptyFor(tokenization);
        }

        reporter.Report(50.0f);

        List<WordDescription> wordDescriptions = [with(capacity: invertedIndex.Count)];
        foreach (var (word, docIds) in invertedIndex)
            wordDescriptions.Add(new(word, docIds));

        // Phase 2: sort descending by word length.
        // QueryMatcher.MatchWithin breaks as soon as it sees a word shorter than the
        // query, so MatchWithin returns wrong results without this ordering.
        CollectionsMarshal.AsSpan(wordDescriptions).Sort(static (a, b) => b.Length.CompareTo(a.Length));

        var wordsArray = new char[totalWordsLength];
        var postingDocIds = new int[totalPostings];
        var postingCounts = new int[wordDescriptions.Count];
        var wordLengths = new int[wordDescriptions.Count];
        var wordEnds = new int[wordDescriptions.Count];
        var postingOffsets = new int[wordDescriptions.Count];
        Dictionary<string, WordPostings> exactPostings = [with(wordDescriptions.Count, StringComparer.Ordinal)];

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
            exactPostings[word] = new(postingOffset, docIds.Count);

            foreach (var docId in docIds)
                postingsSpan[postingOffset++] = docId;

            wordsOffset += word.Length;
            wordLengths[i] = word.Length;
            wordEnds[i] = wordsOffset; // exclusive end; word starts at wordsOffset - word.Length

            reporter.Report(50.0f + ((float)i / wordDescSpan.Length * 50.0f));
        }

        // Phase 4 (bigram index) is built lazily by IndexSnapshot on the first Within query:
        // Exact, glob and regex queries never read it, and it is the largest build phase.

        var recordIdsArr = recordIds.ToArray();
        var facetColumns = BuildFacetColumns(recordIdsArr.Length, facetKeys, pendingFacetValues, directFacetColumns);

        var snapshot = new IndexSnapshot(
            recordIds: recordIdsArr,
            exactPostings: exactPostings,
            wordsArray: wordsArray,
            postingDocIds: postingDocIds,
            postingOffsets: postingOffsets,
            wordEnds: wordEnds,
            wordLengths: wordLengths,
            postingCounts: postingCounts,
            sortTexts: [.. sortTexts],
            facetColumns: facetColumns,
            tokenization: tokenization,
            documentCount: recordIdsArr.Length,
            uniqueWordCount: stringPool.Count
        );

        reporter.Complete();
        return snapshot;
    }

    // Passes a percentage on only when it reaches the next whole percent, so the number of reports does not grow
    // with the number of entries or tokens.
    private struct PercentReporter(IProgress<float>? progress)
    {
        private int _nextWholePercent;

        public void Report(float percent)
        {
            if (progress is null || percent < _nextWholePercent)
                return;

            progress.Report(percent);
            _nextWholePercent = (int)percent + 1;
        }

        public readonly void Complete() => progress?.Report(100.0f);
    }

    private static Dictionary<string, long[]> BuildFacetColumns(
        int documentCount,
        HashSet<string> facetKeys,
        List<PendingFacetValue> pendingFacetValues,
        Dictionary<string, long[]> directFacetColumns)
    {
        if (facetKeys.Count == 0 && directFacetColumns.Count == 0)
            return [];

        Dictionary<string, long[]> facetColumns = [with(facetKeys.Count + directFacetColumns.Count, StringComparer.Ordinal)];
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
