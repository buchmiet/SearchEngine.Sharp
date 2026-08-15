using SearchEngine;

namespace SearchEngine.Sharp.Tests;

public class SingleSemanticWordTests
{
    [Fact]
    public void EnableOperatorsTrue_SingleWord_ExactSameAsOff()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("report-final.pdf", "report-final.pdf"),
            [2] = new("report-draft.pdf", "report-draft.pdf"),
        });
        var engine = new SearchEngineSharp(provider);

        Assert.Equal(
            engine.Find("report-final", WordMatchMethod.Exact, false),
            engine.Find("report-final", WordMatchMethod.Exact, true));

        Assert.Equal(
            engine.CountMatches("report-final", WordMatchMethod.Exact, false),
            engine.CountMatches("report-final", WordMatchMethod.Exact, true));
    }

    [Fact]
    public void EnableOperatorsTrue_BooleanExpression_StillEvaluates()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("report-final.pdf", "report-final.pdf"),
            [2] = new("report-draft.pdf", "report-draft.pdf"),
        });
        var engine = new SearchEngineSharp(provider);

        Assert.Equal(new[] { 1, 2 }, engine.Find("report OR draft", WordMatchMethod.Exact, true).OrderBy(x => x));
        Assert.Equal(new[] { 1 }, engine.Find("report AND NOT draft", WordMatchMethod.Exact, true));
    }
}
