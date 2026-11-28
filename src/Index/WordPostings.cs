namespace SearchEngine.Index;

// Offset and count of a token's postings in IndexSegment.PostingDocIds.
internal readonly struct WordPostings(int offset, int count)
{
    public int Offset { get; } = offset;
    public int Count { get; } = count;
}
