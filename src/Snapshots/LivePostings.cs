namespace SearchEngine.Snapshots;

// The live documents of one word in a snapshot, in ascending ordinal order: its base postings without the dead
// ordinals, then its delta postings. Read without copying; both lists and the dead ordinals are ascending, so
// skipping the dead ones is one merge.
internal readonly ref struct LivePostings
{
    private readonly ReadOnlySpan<int> _base;
    private readonly ReadOnlySpan<int> _dead;
    private readonly ReadOnlySpan<int> _delta;

    internal LivePostings(ReadOnlySpan<int> basePostings, ReadOnlySpan<int> deadOrdinals, ReadOnlySpan<int> deltaPostings)
    {
        _base = basePostings;
        _dead = deadOrdinals;
        _delta = deltaPostings;
    }

    internal int Count
    {
        get
        {
            if (_dead.IsEmpty || _base.IsEmpty)
                return _base.Length + _delta.Length;

            int count = 0;
            foreach (int _ in this)
                count++;
            return count;
        }
    }

    // The postings as one span when no dead ordinal has to be skipped and only one segment holds the word: then the
    // callers keep their tight loops over a span.
    internal bool TryGetSpan(out ReadOnlySpan<int> ordinals)
    {
        if (_delta.IsEmpty && (_dead.IsEmpty || _base.IsEmpty))
        {
            ordinals = _base;
            return true;
        }

        if (_base.IsEmpty)
        {
            ordinals = _delta;
            return true;
        }

        ordinals = default;
        return false;
    }

    public Enumerator GetEnumerator() => new(_base, _dead, _delta);

    internal ref struct Enumerator
    {
        private readonly ReadOnlySpan<int> _base;
        private readonly ReadOnlySpan<int> _dead;
        private readonly ReadOnlySpan<int> _delta;
        private int _baseIndex;
        private int _deadIndex;
        private int _deltaIndex;

        internal Enumerator(ReadOnlySpan<int> basePostings, ReadOnlySpan<int> deadOrdinals, ReadOnlySpan<int> deltaPostings)
        {
            _base = basePostings;
            _dead = deadOrdinals;
            _delta = deltaPostings;
            // Start at the first dead ordinal that can match: the dead list can be much longer than one posting list.
            if (!_dead.IsEmpty && !_base.IsEmpty)
            {
                int start = _dead.BinarySearch(_base[0]);
                _deadIndex = start >= 0 ? start : ~start;
            }
        }

        public int Current { get; private set; }

        public bool MoveNext()
        {
            while (_baseIndex < _base.Length)
            {
                int ordinal = _base[_baseIndex++];
                while (_deadIndex < _dead.Length && _dead[_deadIndex] < ordinal)
                    _deadIndex++;
                if (_deadIndex < _dead.Length && _dead[_deadIndex] == ordinal)
                    continue;

                Current = ordinal;
                return true;
            }

            if (_deltaIndex < _delta.Length)
            {
                Current = _delta[_deltaIndex++];
                return true;
            }

            return false;
        }
    }
}
