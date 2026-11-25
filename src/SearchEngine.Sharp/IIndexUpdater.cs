namespace SearchEngine;

/// <summary>
/// Manages index content. Every mutation (replacing all entries, or adding, updating or removing some) rebuilds
/// the whole snapshot from the current entries and publishes it.
/// All operations are serialized.
/// </summary>
public interface IIndexUpdater
{
    // ── Full rebuild ──

    void RebuildFrom(IDictionary<int, string> entries);
    void RebuildFrom(IDictionary<int, IndexedEntry> entries);
    Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, string>> entries, IProgress<float>? progress = null, CancellationToken ct = default);
    Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> entries, IProgress<float>? progress = null, CancellationToken ct = default);

    // ── Single entry operations ──

    void AddEntry(int id, string text);
    void AddEntry(int id, IndexedEntry entry);
    bool RemoveEntry(int id);
    bool RefreshEntry(int id, string text);
    bool RefreshEntry(int id, IndexedEntry entry);

    // ── Batch operations (single rebuild) ──

    void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, string>> entries);
    void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, IndexedEntry>> entries);
    int RemoveEntries(IEnumerable<int> ids);

    // ── Utility ──

    void Clear();
    int EntryCount { get; }
    bool ContainsEntry(int id);
}
