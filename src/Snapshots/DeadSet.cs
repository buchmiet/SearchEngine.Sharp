using System.Numerics;

namespace SearchEngine.Snapshots;

// The documents of one segment that were removed or replaced after the segment was built: an immutable bitmap over
// the local ordinals, kept in chunks of ChunkWords words so that one more dead document copies one chunk and the
// array of chunk references, not the whole bitmap. A chunk without a dead document is null.
//
// ── Cost ──
// Marking k documents dead copies one pointer array (segment length / ChunkBits entries) and at most k chunks of
// ChunkWords * 8 bytes. A query clears the dead documents of a segment with one AND NOT per non-null chunk
// (IndexSnapshot.RemoveDeadOrdinals).
internal sealed class DeadSet
{
    // 64 words, 4,096 documents, 512 bytes per chunk.
    internal const int ChunkWords = 64;
    internal const int ChunkBits = ChunkWords * 64;

    private readonly ulong[]?[] _chunks;

    private DeadSet(ulong[]?[] chunks, int count)
    {
        _chunks = chunks;
        Count = count;
    }

    // The number of dead documents.
    internal int Count { get; }

    internal int ChunkCount => _chunks.Length;

    // The words of chunk i, or null when none of its documents is dead.
    internal ulong[]? ChunkAt(int i) => _chunks[i];

    internal bool Contains(int local)
        => _chunks[local / ChunkBits] is { } chunk && (chunk[(local % ChunkBits) >> 6] & (1UL << (local & 63))) != 0;

    // previous plus the given local ordinals, for a segment of segmentLength documents. An ordinal that is already
    // dead counts once. previous is null for a segment without dead documents.
    internal static DeadSet With(DeadSet? previous, int segmentLength, ReadOnlySpan<int> locals)
    {
        int chunkTotal = (segmentLength + ChunkBits - 1) / ChunkBits;
        var chunks = new ulong[]?[chunkTotal];
        previous?._chunks.CopyTo(chunks, 0);
        // A chunk that came from previous is shared until the first write to it: copied once, then owned here.
        var owned = new bool[chunkTotal];
        int count = previous?.Count ?? 0;
        foreach (int local in locals)
        {
            int c = local / ChunkBits;
            var chunk = chunks[c];
            if (chunk is null)
            {
                chunks[c] = chunk = new ulong[ChunkWords];
                owned[c] = true;
            }
            else if (!owned[c])
            {
                chunks[c] = chunk = (ulong[])chunk.Clone();
                owned[c] = true;
            }

            ref ulong word = ref chunk[(local % ChunkBits) >> 6];
            ulong bit = 1UL << (local & 63);
            if ((word & bit) == 0)
            {
                word |= bit;
                count++;
            }
        }

        return new DeadSet(chunks, count);
    }

    // The dead local ordinals, ascending (tests and diagnostics).
    internal List<int> ToList()
    {
        var list = new List<int>(Count);
        for (int c = 0; c < _chunks.Length; c++)
        {
            if (_chunks[c] is not { } chunk)
                continue;

            for (int w = 0; w < chunk.Length; w++)
            {
                ulong bits = chunk[w];
                while (bits != 0)
                {
                    list.Add(c * ChunkBits + (w << 6) + BitOperations.TrailingZeroCount(bits));
                    bits &= bits - 1;
                }
            }
        }

        return list;
    }
}
