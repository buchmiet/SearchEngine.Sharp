namespace SearchEngine.Index;

/// <summary>
/// Represents posting list location in the flattened indices array.
/// </summary>
internal readonly struct WordPostings(int offset, int count)
{
    public int Offset { get; } = offset;
    public int Count { get; } = count;
}
