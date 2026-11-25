namespace SearchEngine;

/// <summary>
/// Changes the content of an index. The updater keeps its own copy of every entry; each change rebuilds the whole
/// snapshot from that copy and publishes it through the <see cref="IIndexSnapshotProvider"/> that queries read.
/// </summary>
/// <remarks>
/// <para>Calls are serialized. Queries never wait for them: they keep reading the last published snapshot
/// until the new one is published.</para>
/// <para>A call that throws leaves the stored entries and the published snapshot as they were.</para>
/// <para>Every change costs a full rebuild, linear in the size of the whole index. Change many entries with one call
/// to <see cref="AddOrUpdateEntries(IEnumerable{KeyValuePair{int, IndexedEntry}})"/> or
/// <see cref="RemoveEntries"/>, or feed a running scan through
/// <see cref="Ingestion.ProgressiveIndexIngestion"/>.</para>
/// <para>A <see langword="string"/> entry is indexed as <c>new IndexedEntry(text, text)</c>: the same text is searched
/// and sorted, and the entry has no facets.</para>
/// </remarks>
public interface IIndexUpdater
{
    // ── Full rebuild ──

    /// <summary>Replaces all entries with <paramref name="entries"/> and publishes the new snapshot.</summary>
    /// <param name="entries">The new content, keyed by the id that queries return. The updater copies it.</param>
    void RebuildFrom(IDictionary<int, string> entries);

    /// <summary>Replaces all entries with <paramref name="entries"/> and publishes the new snapshot.</summary>
    /// <param name="entries">The new content, keyed by the id that queries return. The updater copies it.</param>
    void RebuildFrom(IDictionary<int, IndexedEntry> entries);

    /// <summary>
    /// Reads <paramref name="entries"/> to the end, then replaces all entries with them and publishes the new
    /// snapshot.
    /// </summary>
    /// <param name="entries">The new content. When an id occurs more than once, the last value wins.</param>
    /// <param name="progress">Receives the progress of the rebuild from 0 to 100; see
    /// <see cref="Snapshots.IndexSnapshotBuilder"/>. Reading the stream is not reported.</param>
    /// <param name="ct">Observed while the stream is read. The rebuild itself is not cancelled.</param>
    /// <returns>A task that completes when the new snapshot is published.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled while the stream was read.
    /// The index is unchanged.</exception>
    /// <remarks>The stream is read before the updater's lock is taken, so other changes are not blocked while it
    /// is read. A change made by another call in the meantime is replaced by this rebuild.</remarks>
    Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, string>> entries, IProgress<float>? progress = null, CancellationToken ct = default);

    /// <inheritdoc cref="RebuildFromAsync(IAsyncEnumerable{KeyValuePair{int, string}}, IProgress{float}?, CancellationToken)"/>
    Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> entries, IProgress<float>? progress = null, CancellationToken ct = default);

    // ── Single entry operations ──

    /// <summary>Adds the entry, or replaces the entry with the same id, and publishes the new snapshot.</summary>
    /// <param name="id">The id that queries return for this entry.</param>
    /// <param name="text">The text to search and sort.</param>
    void AddEntry(int id, string text);

    /// <summary>Adds the entry, or replaces the entry with the same id, and publishes the new snapshot.</summary>
    /// <param name="id">The id that queries return for this entry.</param>
    /// <param name="entry">The entry to index.</param>
    void AddEntry(int id, IndexedEntry entry);

    /// <summary>Removes the entry with the given id and publishes the new snapshot.</summary>
    /// <param name="id">The id of the entry to remove.</param>
    /// <returns><see langword="true"/> when the entry existed; <see langword="false"/> when it did not, in which
    /// case nothing is rebuilt.</returns>
    bool RemoveEntry(int id);

    /// <summary>Replaces the entry with the given id, or adds it when the id is new, and publishes the new
    /// snapshot.</summary>
    /// <param name="id">The id that queries return for this entry.</param>
    /// <param name="text">The text to search and sort.</param>
    /// <returns><see langword="true"/> when an entry with this id existed before the call.</returns>
    bool RefreshEntry(int id, string text);

    /// <summary>Replaces the entry with the given id, or adds it when the id is new, and publishes the new
    /// snapshot.</summary>
    /// <param name="id">The id that queries return for this entry.</param>
    /// <param name="entry">The entry to index.</param>
    /// <returns><see langword="true"/> when an entry with this id existed before the call.</returns>
    bool RefreshEntry(int id, IndexedEntry entry);

    // ── Batch operations (single rebuild) ──

    /// <summary>Adds or replaces every entry of <paramref name="entries"/> and publishes one new snapshot.</summary>
    /// <param name="entries">The entries to store. When an id occurs more than once, the last value wins.</param>
    /// <remarks>An exception thrown while <paramref name="entries"/> is enumerated undoes the entries already
    /// stored by this call.</remarks>
    void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, string>> entries);

    /// <inheritdoc cref="AddOrUpdateEntries(IEnumerable{KeyValuePair{int, string}})"/>
    void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, IndexedEntry>> entries);

    /// <summary>Removes the entries with the given ids and publishes one new snapshot.</summary>
    /// <param name="ids">The ids to remove. Ids that are not present are skipped.</param>
    /// <returns>The number of entries removed. When it is 0, nothing is rebuilt.</returns>
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
