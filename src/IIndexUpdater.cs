namespace SearchEngine;

/// <summary>
/// Changes the content of an index. The updater keeps its own copy of every entry; after each change it builds a new
/// snapshot from that copy and publishes it through the <see cref="IIndexSnapshotProvider"/> that queries read.
/// </summary>
/// <remarks>
/// <para>Calls are serialized. Queries never wait for them: they keep reading the last published snapshot
/// until the new one is published.</para>
/// <para>A call that throws leaves the entries and the published snapshot as they were.</para>
/// <para>A method that takes a sequence reads it to the end before it takes the updater's lock, so other changes
/// do not wait while it is read.</para>
/// <para>A change builds one small segment from the entries it adds or replaces, marks the documents it removes or
/// replaces as removed where they are, and publishes the new list of segments. Its cost grows with the size of the
/// change, not with the size of the index, and a change never rebuilds the index. Neighbouring segments of similar
/// size are merged on a background thread that neither queries nor changes wait for; the merge also drops the removed
/// documents. <see cref="RebuildFrom(IDictionary{int, string})"/> builds the whole index. Change many entries with one call to
/// <see cref="AddOrUpdateEntries(IEnumerable{KeyValuePair{int, IndexedEntry}})"/> or <see cref="RemoveEntries"/>,
/// or feed a running scan through <see cref="Ingestion.ProgressiveIndexIngestion"/>.</para>
/// <para>A <see langword="string"/> entry is indexed as <c>new IndexedEntry(text, text)</c>: the same text is searched
/// and sorted, and the entry has no facets.</para>
/// </remarks>
public interface IIndexUpdater
{
    // ── Replace all entries ──

    /// <summary>Replaces all entries with <paramref name="entries"/> and publishes the new snapshot.</summary>
    /// <param name="entries">The new content, keyed by the id that queries return. The updater copies it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An entry in <paramref name="entries"/> is
    /// <see langword="null"/>.</exception>
    void RebuildFrom(IDictionary<int, string> entries);

    /// <inheritdoc cref="RebuildFrom(IDictionary{int, string})"/>
    void RebuildFrom(IDictionary<int, IndexedEntry> entries);

    /// <summary>
    /// Reads <paramref name="entries"/> to the end, then replaces all entries with them and publishes the new
    /// snapshot.
    /// </summary>
    /// <param name="entries">The new content. When an id occurs more than once, the last value wins.</param>
    /// <param name="progress">Receives the progress of the build from 0 to 100; see
    /// <see cref="Snapshots.IndexSnapshotBuilder"/>. Reading the stream is not reported.</param>
    /// <param name="ct">Observed while the stream is read. The build itself is not cancelled.</param>
    /// <returns>A task that completes when the new snapshot is published.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The stream yields a <see langword="null"/> entry. The returned task
    /// faults with it, and the index is unchanged.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled while the stream was read.
    /// The index is unchanged.</exception>
    /// <remarks>A change that another call makes while the stream is read is replaced by this call. The method does
    /// not resume on the caller's synchronization context: after the stream completes a step asynchronously, the
    /// reading and the build continue on a thread-pool thread.</remarks>
    Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, string>> entries, IProgress<float>? progress = null, CancellationToken ct = default);

    /// <inheritdoc cref="RebuildFromAsync(IAsyncEnumerable{KeyValuePair{int, string}}, IProgress{float}?, CancellationToken)"/>
    Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> entries, IProgress<float>? progress = null, CancellationToken ct = default);

    // ── Single entry operations ──

    /// <summary>Adds the entry, or replaces the entry with the same id, and publishes the new snapshot.</summary>
    /// <param name="id">The id that queries return for this entry.</param>
    /// <param name="text">The text to search and sort.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    void AddEntry(int id, string text);

    /// <summary>Adds the entry, or replaces the entry with the same id, and publishes the new snapshot.</summary>
    /// <param name="id">The id that queries return for this entry.</param>
    /// <param name="entry">The entry to index.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    void AddEntry(int id, IndexedEntry entry);

    /// <summary>Removes the entry with the given id and publishes the new snapshot.</summary>
    /// <param name="id">The id of the entry to remove.</param>
    /// <returns><see langword="true"/> when the entry existed; <see langword="false"/> when it did not, in which
    /// case nothing is published.</returns>
    bool RemoveEntry(int id);

    /// <summary>Does the same as <see cref="AddEntry(int, string)"/> and returns whether the id existed.</summary>
    /// <param name="id">The id that queries return for this entry.</param>
    /// <param name="text">The text to search and sort.</param>
    /// <returns><see langword="true"/> when an entry with this id existed before the call.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    bool RefreshEntry(int id, string text);

    /// <summary>Does the same as <see cref="AddEntry(int, IndexedEntry)"/> and returns whether the id
    /// existed.</summary>
    /// <param name="id">The id that queries return for this entry.</param>
    /// <param name="entry">The entry to index.</param>
    /// <returns><see langword="true"/> when an entry with this id existed before the call.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    bool RefreshEntry(int id, IndexedEntry entry);

    // ── Batch operations (one publish) ──

    /// <summary>Adds or replaces every entry of <paramref name="entries"/> and publishes one new snapshot.</summary>
    /// <param name="entries">The entries to store. When an id occurs more than once, the last value wins.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An entry in <paramref name="entries"/> is
    /// <see langword="null"/>.</exception>
    void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, string>> entries);

    /// <inheritdoc cref="AddOrUpdateEntries(IEnumerable{KeyValuePair{int, string}})"/>
    void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, IndexedEntry>> entries);

    /// <summary>Removes the entries with the given ids and publishes one new snapshot.</summary>
    /// <param name="ids">The ids to remove. Ids that are not present are skipped.</param>
    /// <returns>The number of entries removed. When it is 0, nothing is published.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ids"/> is <see langword="null"/>.</exception>
    int RemoveEntries(IEnumerable<int> ids);

    // ── Utility ──

    /// <summary>Removes all entries and publishes an empty snapshot that keeps the updater's
    /// <see cref="SearchTokenization"/>.</summary>
    void Clear();

    /// <summary>Gets the number of entries the updater holds.</summary>
    int EntryCount { get; }

    /// <summary>Returns whether the updater holds an entry with the given id.</summary>
    /// <param name="id">The id to look up.</param>
    /// <returns><see langword="true"/> when an entry with <paramref name="id"/> exists.</returns>
    bool ContainsEntry(int id);
}
