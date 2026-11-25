namespace SearchEngine.Index;

/// <summary>
/// Location of a posting list in the flattened indices array.
/// </summary>
internal readonly struct WordPostings(int offset, int count)
{
    public int Offset { get; } = offset;
    public int Count { get; } = count;
}
