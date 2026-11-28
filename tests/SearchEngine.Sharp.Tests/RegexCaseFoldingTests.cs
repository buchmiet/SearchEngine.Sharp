using System.Text.RegularExpressions;
using SearchEngine.Query;

namespace SearchEngine.Sharp.Tests;

// A regex pattern gets the casing of indexed tokens from the Unicode tables (lowercase, and case folding under
// CaseFold) before it runs case-sensitively (src/Query/RegexCaseFolding.cs, docs/query-semantics.md, Regular
// expressions): only the characters that stand for themselves change, never the syntax around them, so a valid
// pattern stays valid and means the same, and an invalid one stays invalid and matches nothing.
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
    public async Task Apply_changes_only_the_characters_that_stand_for_themselves(string pattern, bool caseFold, string expected)
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
    public async Task Apply_leaves_an_invalid_pattern_invalid(string pattern)
    {
        string folded = RegexCaseFolding.Apply(pattern, caseFold: true);

        await Assert.That(() => new Regex(folded)).Throws<RegexParseException>();
    }
}
