namespace SearchEngine.Sharp.Tests;

/// <summary>
/// The generated Unicode 17.0.0 Simple_Case_Folding table that CaseFold uses instead of the casing of the platform.
/// </summary>
public sealed class UnicodeCaseFoldingTests
{
    [Test]
    [Arguments(0x0041, 0x0061)] // A -> a
    [Arguments(0x0061, 0x0061)] // a has no folding
    [Arguments(0x03A3, 0x03C3)] // capital sigma -> sigma
    [Arguments(0x03C2, 0x03C3)] // final sigma -> sigma
    [Arguments(0x1C80, 0x0432)] // rounded ve -> ve; Windows NLS leaves it
    [Arguments(0x017F, 0x0073)] // long s -> s; invariant globalization mode leaves it
    [Arguments(0x212A, 0x006B)] // Kelvin sign -> k
    [Arguments(0x1E9E, 0x00DF)] // capital sharp s -> sharp s (status S)
    [Arguments(0xAB70, 0x13A0)] // Cherokee small a -> the capital, as CaseFolding.txt defines it
    [Arguments(0x1E900, 0x1E922)] // Adlam capital alif -> small alif
    [Arguments(0x0130, 0x0130)] // capital I with dot has only full and Turkic foldings
    [Arguments(0x00DF, 0x00DF)] // sharp s has only a full folding (ss)
    [Arguments(-1, -1)]
    [Arguments(0x110000, 0x110000)]
    public async Task Fold_ReturnsSimpleCaseFolding(int codePoint, int expected)
    {
        await Assert.That(UnicodeCaseFolding.Fold(codePoint)).IsEqualTo(expected);
    }

    [Test]
    public async Task Table_FoldsEveryCodePointOfUnicode17CaseFolding()
    {
        var folding = 0;
        for (var codePoint = 0; codePoint <= 0x10FFFF; codePoint++)
            if (UnicodeCaseFolding.Fold(codePoint) != codePoint)
                folding++;

        await Assert.That(folding).IsEqualTo(1512); // CaseFolding.txt 17.0.0, statuses C and S
    }
}
