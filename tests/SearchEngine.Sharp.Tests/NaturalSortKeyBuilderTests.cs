using SearchEngine.Sorting;

namespace SearchEngine.Sharp.Tests;

public class NaturalSortKeyBuilderTests
{
    [Test]
    public async Task NaturalSortKey_ProducesCorrectOrder_ForCasioModels()
    {
        var inputs = new[]
        {
            "GA-1000",
            "GA-100A",
            "GA-100-1B",
            "GA-10",
            "GA-100",
            "GA-100-1A",
            "GA-2",
            "GA-100-1"
        };

        var expectedOrder = new[]
        {
            "GA-2",
            "GA-10",
            "GA-100",
            "GA-100-1",
            "GA-100-1A",
            "GA-100-1B",
            "GA-100A",
            "GA-1000"
        };

        var sorted = inputs
            .OrderBy(NaturalSortKeyBuilder.Build, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(sorted).IsEquivalentTo(expectedOrder, CollectionOrdering.Matching);
    }

    [Test]
    public async Task NaturalSortKey_NumberBeforeText_AtSamePosition()
    {
        var key1 = NaturalSortKeyBuilder.Build("GA-100-1");
        var key100A = NaturalSortKeyBuilder.Build("GA-100A");

        await Assert.That(string.Compare(key1, key100A, StringComparison.Ordinal)).IsLessThan(0)
            .Because($"Expected GA-100-1 key '{key1}' < GA-100A key '{key100A}'");
    }

    [Test]
    public async Task NaturalSortKey_NumericComparison_NotLexicographic()
    {
        var key2 = NaturalSortKeyBuilder.Build("GA-2");
        var key10 = NaturalSortKeyBuilder.Build("GA-10");

        await Assert.That(string.Compare(key2, key10, StringComparison.Ordinal)).IsLessThan(0)
            .Because($"Expected GA-2 key '{key2}' < GA-10 key '{key10}'");
    }

    [Test]
    public async Task NaturalSortKey_ShorterSequenceFirst_WhenAllTokensEqual()
    {
        var key100 = NaturalSortKeyBuilder.Build("GA-100");
        var key1001 = NaturalSortKeyBuilder.Build("GA-100-1");

        await Assert.That(string.Compare(key100, key1001, StringComparison.Ordinal)).IsLessThan(0)
            .Because($"Expected GA-100 key '{key100}' < GA-100-1 key '{key1001}'");
    }

    [Test]
    public async Task NaturalSortKey_TextTokens_CaseInsensitive()
    {
        var keyLower = NaturalSortKeyBuilder.Build("ga-100");
        var keyUpper = NaturalSortKeyBuilder.Build("GA-100");

        await Assert.That(keyUpper).IsEqualTo(keyLower);
    }

    [Test]
    public async Task NaturalSortKey_EmptyString_ReturnsEmpty()
    {
        await Assert.That(NaturalSortKeyBuilder.Build("")).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task NaturalSortKey_ComplexModel_GST()
    {
        var inputs = new[]
        {
            "GST-B400D-1B",
            "GST-B400D-1A",
            "GST-B400C-1A"
        };

        var sorted = inputs
            .OrderBy(NaturalSortKeyBuilder.Build, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(sorted).IsEquivalentTo(new[] { "GST-B400C-1A", "GST-B400D-1A", "GST-B400D-1B" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task NaturalSortKey_DW_Models()
    {
        var inputs = new[]
        {
            "DW-5600BB-1",
            "DW-5600BB-1A",
            "DW-5600BB-2",
            "DW-5600E-1"
        };

        var sorted = inputs
            .OrderBy(NaturalSortKeyBuilder.Build, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(sorted).IsEquivalentTo(new[]
        {
            "DW-5600BB-1",
            "DW-5600BB-1A",
            "DW-5600BB-2",
            "DW-5600E-1"
        }, CollectionOrdering.Matching);
    }
}
