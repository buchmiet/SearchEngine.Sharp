using System.Text.RegularExpressions;
using SearchEngine.Query;

namespace SearchEngine.Sharp.Tests;

/// <summary>
/// A regex pattern gets the casing of indexed tokens from the Unicode tables (lowercase, and case folding under CaseFold):
/// only the characters that stand for themselves change, never the syntax around them.
/// </summary>
public sealed class RegexCaseFoldingTests
{
    [Test]
    [Arguments("REPORT", false, "report")]
    [Arguments("İstanbul", false, "istanbul")] // U+0130 lowercases to i in UnicodeData.txt
    [Arguments(@"(X)\1\D\W\S\B\p{Lu}\P{Lu}\cA", false, @"(x)\1\D\W\S\B\p{Lu}\P{Lu}\cA")]
    [Arguments(@"İ\x41", false, @"ia")]
    [Arguments(@"(?<Name>A)\k<Name>(?'Other'B)", false, @"(?<Name>a)\k<Name>(?'Other'b)")]
    [Arguments("(?i)A(?#Comment)(?<=B)(?!C)(?:D)", false, "(?i)a(?#Comment)(?<=b)(?!c)(?:d)")]
    [Arguments("[A-C]", false, @"[A-Ca-c]")]
    [Arguments("[AB]", false, @"[ABab]")]
    [Arguments("[a-zA-Z]", false, "[a-zA-Z]")] // the lowercase letters are listed already
    [Arguments("[]A]", false, @"[]Aa]")] // a ] right after [ is a literal
    [Arguments(@"[\d\-A]", false, @"[\d\-Aa]")]
    [Arguments("[A-Z-[AEIOU]]", false, @"[A-Za-z-[AEIOUaeiou]]")]
    [Arguments("ß", false, "ß")] // lowercase only: no folding
    [Arguments("ß+", true, "(?:ss)+")]
    [Arguments("[aß]", true, @"(?:[aß]|ss)")]
    [Arguments("[^ß]", true, "[^ß]")] // a negated class cannot exclude two characters
    [Arguments("σΣς", true, "σσσ")]
    [Arguments(@"ﬃ", true, @"(?:ffi)")]
    [Arguments("ſ", true, "s")]
    public async Task Apply_FoldsLiteralCharactersOnly(string pattern, bool caseFold, string expected)
    {
        string folded = RegexCaseFolding.Apply(pattern, caseFold);

        await Assert.That(folded).IsEqualTo(expected);
        await Assert.That(() => new Regex(folded)).ThrowsNothing();
    }

    [Test]
    [Arguments("(unclosed")]
    [Arguments("[Abc")]
    [Arguments(@"[Z-A]")]
    [Arguments("ABC\\")]
    public async Task Apply_LeavesAnInvalidPatternInvalid(string pattern)
    {
        string folded = RegexCaseFolding.Apply(pattern, caseFold: true);

        await Assert.That(() => new Regex(folded)).Throws<RegexParseException>();
    }
}
