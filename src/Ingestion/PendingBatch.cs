namespace SearchEngine.Ingestion;

/// <summary>An entry as the scanner handed it over, stamped with the time of the handover.</summary>
internal readonly record struct BufferedEntry(int Id, IndexedEntry Entry, long BufferedAtTimestamp);

/// <summary>
/// The entries taken from the channel and not yet published. The scanner stamps entries in the order it hands them
/// over, so the first entry is the oldest and its timestamp alone decides the staleness of the batch.
/// </summary>
internal sealed class PendingBatch
{
    public List<KeyValuePair<int, IndexedEntry>> Entries { get; } = [];

    public long OldestTimestamp { get; private set; }

    public int Count => Entries.Count;

    public void Add(BufferedEntry entry)
    {
        if (Entries.Count == 0)
            OldestTimestamp = entry.BufferedAtTimestamp;

        Entries.Add(new(entry.Id, entry.Entry));
    }

    public void Clear() => Entries.Clear();
}
