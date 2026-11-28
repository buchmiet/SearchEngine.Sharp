namespace SearchEngine.Snapshots;

// Record id → local ordinal of one segment: an open-addressing table of local ordinals (the id is read back from
// RecordIds, so the table stores 4 bytes per slot, at most half full). Immutable once built; ids of a segment are
// unique. The updater probes it to find the live copy of an id that a change removes or replaces.
internal sealed class IdIndex
{
    private readonly int[] _slots; // local ordinal + 1, 0 = empty
    private readonly int[] _recordIds;
    private readonly int _mask;

    internal IdIndex(int[] recordIds)
    {
        _recordIds = recordIds;
        int capacity = 4;
        while (capacity < recordIds.Length * 2)
            capacity <<= 1;

        _slots = new int[capacity];
        _mask = capacity - 1;
        for (int local = 0; local < recordIds.Length; local++)
        {
            int slot = Hash(recordIds[local]) & _mask;
            while (_slots[slot] != 0)
                slot = (slot + 1) & _mask;
            _slots[slot] = local + 1;
        }
    }

    internal bool TryFind(int recordId, out int local)
    {
        int slot = Hash(recordId) & _mask;
        while (true)
        {
            int entry = _slots[slot];
            if (entry == 0)
            {
                local = -1;
                return false;
            }

            if (_recordIds[entry - 1] == recordId)
            {
                local = entry - 1;
                return true;
            }

            slot = (slot + 1) & _mask;
        }
    }

    // Fibonacci hashing: ids are often sequential, which would fill neighbouring slots one after another.
    private static int Hash(int id) => (int)(((uint)id * 2654435761u) >> 4);
}
