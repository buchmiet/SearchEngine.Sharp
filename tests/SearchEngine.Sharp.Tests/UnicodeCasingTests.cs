namespace SearchEngine.Sharp.Tests;

// The generated Unicode 17.0.0 casing tables (src/Tokenizer/UnicodeCasing.g.cs) that lowercasing and CaseFold use
// instead of the casing of the platform, so an index built on Windows NLS, ICU or in globalization-invariant mode
// holds the same tokens. The counts pin the table to one Unicode version; lowercasing works per UTF-16 code unit,
// so no letter of the basic plane may lowercase outside it.
public sealed class UnicodeCasingTests
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
    public async Task Fold_returns_the_simple_case_folding(int codePoint, int expected)
    {
        await Assert.That(UnicodeCasing.Fold(codePoint)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0x0041, 0x0061)] // A -> a
    [Arguments(0x00C9, 0x00E9)] // É -> é
    [Arguments(0x0130, 0x0069)] // İ -> i; char.ToLowerInvariant keeps İ
    [Arguments(0x03C2, 0x03C2)] // final sigma is already lowercase
    [Arguments(0x1E9E, 0x00DF)] // capital sharp s -> sharp s
    [Arguments(0x212A, 0x006B)] // Kelvin sign -> k
    [Arguments(0x13A0, 0xAB70)] // Cherokee capital a -> small a
    [Arguments(0x1C89, 0x1C8A)] // Cyrillic capital tje, added in Unicode 16
    [Arguments(0x1E900, 0x1E922)] // Adlam capital alif -> small alif
    [Arguments(-1, -1)]
    [Arguments(0x110000, 0x110000)]
    public async Task ToLower_returns_the_simple_lowercase_mapping(int codePoint, int expected)
    {
        await Assert.That(UnicodeCasing.ToLower(codePoint)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0x00DF, new[] { 0x73, 0x73 })] // ß -> ss
    [Arguments(0x1E9E, new[] { 0x73, 0x73 })] // ẞ -> ss
    [Arguments(0x0130, new[] { 0x69, 0x307 })] // İ -> i + combining dot above
    [Arguments(0x0149, new[] { 0x2BC, 0x6E })] // ŉ -> ʼn
    [Arguments(0x0390, new[] { 0x3B9, 0x308, 0x301 })] // ΐ -> ι + diaeresis + acute
    [Arguments(0xFB03, new[] { 0x66, 0x66, 0x69 })] // ﬃ -> ffi
    [Arguments(0x0041, new[] { 0x61 })] // status C
    [Arguments(0x0061, new[] { 0x61 })] // no folding
    [Arguments(0x110000, new[] { 0x110000 })]
    public async Task FullFold_returns_the_full_case_folding(int codePoint, int[] expected)
    {
        await Assert.That(FullFold(codePoint)).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    private static int[] FullFold(int codePoint)
    {
        Span<int> buffer = stackalloc int[3];
        return buffer[..UnicodeCasing.FullFold(codePoint, buffer)].ToArray();
    }

    [Test]
    public async Task The_tables_hold_every_mapping_of_Unicode_17()
    {
        int folding = 0, full = 0, lowercase = 0;
        for (var codePoint = 0; codePoint <= 0x10FFFF; codePoint++)
        {
            if (UnicodeCasing.Fold(codePoint) != codePoint)
                folding++;
            if (FullFold(codePoint).Length > 1)
                full++;
            if (UnicodeCasing.ToLower(codePoint) != codePoint)
                lowercase++;
        }

        await Assert.That(folding).IsEqualTo(1512); // CaseFolding.txt 17.0.0, statuses C and S
        await Assert.That(full).IsEqualTo(104); // status F
        await Assert.That(lowercase).IsEqualTo(1488); // UnicodeData.txt 17.0.0, field 13
    }

    [Test]
    public async Task ToLowerInvariantFast_stays_in_the_basic_plane()
    {
        // Lowercase works per UTF-16 code unit, so a BMP letter must lowercase to a BMP letter.
        var leaves = new List<int>();
        for (var codePoint = 0; codePoint <= 0xFFFF; codePoint++)
            if (UnicodeCasing.ToLower(codePoint) > 0xFFFF)
                leaves.Add(codePoint);

        await Assert.That(leaves).IsEmpty();
        await Assert.That(Tokenizer.TextNormalizer.ToLowerInvariantFast('\u0130')).IsEqualTo('i');
        await Assert.That(Tokenizer.TextNormalizer.ToLowerInvariantFast('\u1C89')).IsEqualTo('\u1C8A');
        await Assert.That(Tokenizer.TextNormalizer.ToLowerInvariantFast('\uD801')).IsEqualTo('\uD801');
    }
}
