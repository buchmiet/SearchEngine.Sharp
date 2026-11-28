namespace SearchEngine;

// How an IndexUpdater keeps the number of segments small (IndexUpdater, Cost). Internal: the defaults suit the
// documented workloads; tests and benchmarks change them.
internal sealed class MergePolicy
{
    internal static readonly MergePolicy Default = new();

    // At least this many adjacent segments of similar size merge into one, so sizes grow by about this factor from one
    // merge to the next. A document is then rewritten about log_FanOut(N) times over its life, and an index has at most
    // FanOut - 1 segments per size class.
    internal int FanOut { get; init; } = 4;

    // A segment joins the run that ends with a newer one only when it has at most this many times the live documents
    // of the largest segment in the run so far. Near 1 a merge combines segments of one size class and leaves a larger
    // older segment alone until its newer neighbours have grown to about its size.
    internal double SimilarFactor { get; init; } = 2.0;

    // Past this many segments, a change merges the newest ones before it returns, so a writer that outruns the
    // background merges cannot make queries visit an unbounded number of segments.
    internal int MaxSegments { get; init; } = 64;

    // A segment in which this share of the documents is dead is rewritten without them, even when it has no
    // neighbour of its level to merge with.
    internal double DeadRatio { get; init; } = 0.3;

    // The second background lane takes only merges whose result has at most this many documents, so a long merge of a
    // large segment does not hold up the merges of the small ones that every change creates.
    internal int SmallLaneLimit { get; init; } = 16_384;

    // A run longer than this is merged in pieces of this many segments (the newest ones first).
    internal int MaxRun => FanOut * 2;

    // False merges only through the MaxSegments rule and IndexUpdater.MergeAllNow (tests).
    internal bool Background { get; init; } = true;
}
