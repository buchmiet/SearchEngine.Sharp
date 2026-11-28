namespace SearchEngine.Snapshots;

// The live documents of one token in a snapshot, as snapshot ordinals in ascending order: the postings of each
// segment that holds the token, shifted by the offset of the segment and without the dead documents. Read without
// copying; the segments are visited in snapshot order and each list is ascending, so the whole is ascending.
internal readonly struct LivePostings
{
    private readonly IndexSnapshot _snapshot;
    private readonly string _word;

    internal LivePostings(IndexSnapshot snapshot, string word)
    {
        _snapshot = snapshot;
        _word = word;
    }

    internal int Count
    {
        get
        {
            int count = 0;
            var segments = _snapshot.Segments;
            for (int s = 0; s < segments.Length; s++)
            {
                if (!segments[s].ExactPostings.TryGetValue(_word, out var range))
                    continue;

                var dead = _snapshot.Dead[s];
                if (dead is null)
                {
                    count += range.Count;
                    continue;
                }

                var postings = segments[s].PostingDocIds.AsSpan(range.Offset, range.Count);
                foreach (int local in postings)
                {
                    if (!dead.Contains(local))
                        count++;
                }
            }

            return count;
        }
    }

    // An upper bound of Count that does not look at the dead documents.
    internal int UpperBound
    {
        get
        {
            int count = 0;
            var segments = _snapshot.Segments;
            for (int s = 0; s < segments.Length; s++)
            {
                if (segments[s].ExactPostings.TryGetValue(_word, out var range))
                    count += range.Count;
            }

            return count;
        }
    }

    // Appends the record ids of the live documents, in ascending ordinal order, reading the ids of each segment
    // directly instead of looking up the segment of every ordinal.
    internal void AppendRecordIdsTo(List<int> results)
    {
        var segments = _snapshot.Segments;
        for (int s = 0; s < segments.Length; s++)
        {
            if (!segments[s].ExactPostings.TryGetValue(_word, out var range))
                continue;

            var postings = segments[s].PostingDocIds.AsSpan(range.Offset, range.Count);
            var recordIds = segments[s].RecordIds;
            var dead = _snapshot.Dead[s];
            if (dead is null)
            {
                foreach (int local in postings)
                    results.Add(recordIds[local]);
            }
            else
            {
                foreach (int local in postings)
                {
                    if (!dead.Contains(local))
                        results.Add(recordIds[local]);
                }
            }
        }
    }

    // The postings as one span when exactly one segment holds the token, it starts at ordinal 0 and has no dead
    // document: then local and snapshot ordinals agree and the callers keep their tight loops over a span.
    internal bool TryGetSpan(out ReadOnlySpan<int> ordinals)
    {
        ordinals = default;
        var segments = _snapshot.Segments;
        int found = -1;
        for (int s = 0; s < segments.Length; s++)
        {
            if (!segments[s].ExactPostings.ContainsKey(_word))
                continue;

            if (found >= 0)
                return false;
            found = s;
        }

        if (found < 0)
            return true; // no segment holds the token: empty

        if (found != 0 || _snapshot.Dead[0] is not null)
            return false;

        var range = segments[0].ExactPostings[_word];
        ordinals = segments[0].PostingDocIds.AsSpan(range.Offset, range.Count);
        return true;
    }

    public Enumerator GetEnumerator() => new(_snapshot, _word);

    internal struct Enumerator
    {
        private readonly IndexSnapshot _snapshot;
        private readonly string _word;
        private int _segment;
        private int[]? _postings;
        private int _position;
        private int _end;
        private int _offset;
        private DeadSet? _dead;

        internal Enumerator(IndexSnapshot snapshot, string word)
        {
            _snapshot = snapshot;
            _word = word;
            _segment = -1;
        }

        public int Current { get; private set; }

        public bool MoveNext()
        {
            while (true)
            {
                while (_postings is not null && _position < _end)
                {
                    int local = _postings[_position++];
                    if (_dead is not null && _dead.Contains(local))
                        continue;

                    Current = _offset + local;
                    return true;
                }

                // Next segment that holds the token.
                _postings = null;
                var segments = _snapshot.Segments;
                while (++_segment < segments.Length)
                {
                    if (!segments[_segment].ExactPostings.TryGetValue(_word, out var range))
                        continue;

                    _postings = segments[_segment].PostingDocIds;
                    _position = range.Offset;
                    _end = range.Offset + range.Count;
                    _offset = _snapshot.Offsets[_segment];
                    _dead = _snapshot.Dead[_segment];
                    break;
                }

                if (_postings is null)
                    return false;
            }
        }
    }
}
