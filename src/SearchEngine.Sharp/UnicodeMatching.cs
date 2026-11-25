using System.Buffers;
using System.Globalization;
using System.Text;

namespace SearchEngine;

/// <summary>
/// Optional Unicode-aware matching. The folding options are applied to indexed text and to queries;
/// pure ASCII text is never changed, so the cost is paid only for names that contain non-ASCII characters.
/// A <see cref="WordMatchMethod.Regex"/> pattern is not folded (that would change its character classes): it runs
/// as written against the folded text. Set through <see cref="SearchTokenization.WithUnicodeMatching"/>.
/// </summary>
[Flags]
public enum UnicodeMatching
{
    /// <summary>Text is used as given (per-character invariant lowercase only). The default.</summary>
    None = 0,

    /// <summary>
    /// Canonical composition (NFC): a decomposed <c>e</c> + U+0301 and a precomposed <c>é</c>, or Hangul
    /// jamo and syllables, become the same token. macOS file names are often stored decomposed (NFD).
    /// </summary>
    Compose = 1,

    /// <summary>
    /// Unicode simple case folding (case variants that per-character lowercase keeps apart match: final <c>ς</c> and
    /// <c>σ</c>, <c>µ</c> and <c>μ</c>, Greek symbol variants such as <c>ϐ</c>, and supplementary-plane letters
    /// such as Deseret <c>𐐀</c> and <c>𐐨</c>), plus the full foldings <c>ß</c>/<c>ẞ</c>→<c>ss</c> and the Latin
    /// ligatures U+FB00–U+FB06, and <c>İ</c>→<c>i</c>, <c>ſ</c>→<c>s</c>. Other full foldings (<c>ŉ</c>, Greek
    /// letters with ypogegrammeni, Armenian ligatures) are not applied.
    /// </summary>
    CaseFold = 2,

    /// <summary>
    /// Accent folding: canonical decomposition, removal of the accents of Latin, Greek and Cyrillic letters (the
    /// non-spacing marks of the Combining Diacritical Marks blocks U+0300–U+036F, U+1AB0–U+1AFF, U+1DC0–U+1DFF and
    /// U+FE20–U+FE2F), then composition (<c>é</c>→<c>e</c>, <c>ą</c>→<c>a</c>, <c>ü</c>→<c>u</c>, <c>ά</c>→<c>α</c>,
    /// <c>й</c>→<c>и</c>), plus <c>ł</c>→<c>l</c>, <c>ø</c>→<c>o</c>, <c>đ</c>→<c>d</c>, <c>ħ</c>→<c>h</c>,
    /// <c>ı</c>→<c>i</c>, which have no decomposition. Marks of other scripts are part of the letter and stay
    /// (Japanese dakuten: <c>バ</c> ≠ <c>ハ</c>; Thai tone marks; Indic vowel signs). Implies <see cref="Compose"/>.
    /// </summary>
    RemoveDiacritics = 4,

    /// <summary>
    /// <see cref="WordMatchMethod.Within"/> matches must start and end on extended grapheme cluster
    /// boundaries (UAX #29): <c>cafe</c> no longer matches inside a decomposed <c>café</c>, and one emoji
    /// no longer matches inside a ZWJ sequence. Exact and glob matches are whole tokens and need no check.
    /// </summary>
    GraphemeBoundaries = 8,
}

internal static class TextFolder
{
    // Globalization-invariant mode (InvariantGlobalization, DOTNET_SYSTEM_GLOBALIZATION_INVARIANT; the default for
    // some trimmed and container deployments) has no normalization data: string.Normalize returns its input and
    // IsNormalized reports true, so Compose and RemoveDiacritics would silently match nothing. A decomposed é is not
    // NFC wherever normalization works.
    internal static bool IsNormalizationAvailable { get; } = !"e\u0301".IsNormalized(NormalizationForm.FormC);

    internal static string Fold(string text, UnicodeMatching normalization)
    {
        const UnicodeMatching folding = UnicodeMatching.Compose | UnicodeMatching.CaseFold | UnicodeMatching.RemoveDiacritics;
        if ((normalization & folding) == 0 || string.IsNullOrEmpty(text) || Ascii.IsValid(text))
            return text;

        bool removeDiacritics = (normalization & UnicodeMatching.RemoveDiacritics) != 0;
        if (removeDiacritics || (normalization & UnicodeMatching.Compose) != 0)
            text = NormalizeWellFormedRuns(text, removeDiacritics);

        if ((normalization & UnicodeMatching.CaseFold) != 0)
            text = ApplyCaseFolding(text);

        return text;
    }

    // string.Normalize and string.IsNormalized throw ArgumentException on ill-formed UTF-16 (a lone surrogate, which
    // file systems such as NTFS allow in names) and on U+FFFE. Such a code unit takes no part in composition, so it is
    // kept as it is and the well-formed runs around it are normalized on their own: the text still matches itself.
    private static string NormalizeWellFormedRuns(string text, bool removeDiacritics)
    {
        int invalid = IndexOfUnnormalizable(text);
        if (invalid < 0)
            return Normalize(text, removeDiacritics);

        var sb = new StringBuilder(text.Length);
        int start = 0;
        while (invalid >= 0)
        {
            sb.Append(Normalize(text[start..invalid], removeDiacritics)).Append(text[invalid]);
            start = invalid + 1;
            int next = IndexOfUnnormalizable(text.AsSpan(start));
            invalid = next < 0 ? -1 : start + next;
        }

        return sb.Append(Normalize(text[start..], removeDiacritics)).ToString();
    }

    private static int IndexOfUnnormalizable(ReadOnlySpan<char> text)
    {
        if (!text.ContainsAnyInRange('\uD800', '\uDFFF') && !text.Contains('\uFFFE'))
            return -1;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            else if (char.IsSurrogate(ch) || ch == '\uFFFE')
                return i;
        }

        return -1;
    }

    private static string Normalize(string text, bool removeDiacritics)
    {
        if (text.Length == 0)
            return text;

        if (removeDiacritics)
            return RemoveDiacritics(text);

        return text.IsNormalized(NormalizationForm.FormC) ? text : text.Normalize(NormalizationForm.FormC);
    }

    private static string RemoveDiacritics(string text)
    {
        string decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char ch in decomposed)
        {
            if (IsAccent(ch))
                continue;

            sb.Append(ch switch
            {
                'ł' => 'l', 'Ł' => 'L', 'ø' => 'o', 'Ø' => 'O', 'đ' => 'd', 'Đ' => 'D', 'ħ' => 'h', 'Ħ' => 'H', 'ı' => 'i',
                _ => ch,
            });
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    // Accents are the non-spacing marks of the Combining Diacritical Marks blocks, the marks that Latin, Greek and
    // Cyrillic letters decompose into: U+0300–U+036F, U+1AB0–U+1AFF (Extended), U+1DC0–U+1DFF (Supplement) and the
    // half marks U+FE20–U+FE2F. Marks of other scripts belong to the letter and change the word (the Japanese
    // dakuten U+3099 turns ハス into バス, Thai tone marks, Indic vowel signs, Hebrew points), and U+20D0–U+20FF are
    // marks for symbols (including the keycap U+20E3), so all of those stay. Every accent is in the BMP: testing
    // UTF-16 code units is exact, and a surrogate never matches.
    private static bool IsAccent(char ch)
        => ch is (>= '\u0300' and <= '\u036F') or (>= '\u1AB0' and <= '\u1AFF') or (>= '\u1DC0' and <= '\u1DFF') or (>= '\uFE20' and <= '\uFE2F')
            && CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark;

    // Simple case folding through the BCL: case variants of one letter (σ/ς/Σ, μ/µ, β/ϐ, ι/U+0345/U+1FBE, в/ᲀ,
    // Deseret 𐐨/𐐀) share their uppercase, and the lowercase of that is their folded form. Per-character lowercase
    // alone misses the lowercase variants and, working on UTF-16 code units, every supplementary-plane letter. On top
    // of that: the full foldings ß/ẞ→ss and the Latin ligatures U+FB00–U+FB06; İ→i (the Turkic mapping, because the
    // default full folding i + U+0307 would not match i); ſ→s, which the casing tables of globalization-invariant mode
    // do not uppercase. A text where folding differs from per-character lowercase nowhere is returned unchanged.
    private static string ApplyCaseFolding(string text)
    {
        StringBuilder? sb = null;
        Span<char> buffer = stackalloc char[2];
        int position = 0;
        while (position < text.Length)
        {
            int start = position;
            if (Rune.DecodeFromUtf16(text.AsSpan(position), out Rune rune, out int length) != OperationStatus.Done)
            {
                sb?.Append(text[position]); // ill-formed UTF-16 is kept as it is
                position++;
                continue;
            }

            position += length;
            string? expansion = rune.Value switch
            {
                'ß' or 'ẞ' => "ss",
                'İ' => "i",
                'ſ' => "s",
                'ﬀ' => "ff",
                'ﬁ' => "fi",
                'ﬂ' => "fl",
                'ﬃ' => "ffi",
                'ﬄ' => "ffl",
                'ﬅ' or 'ﬆ' => "st",
                _ => null,
            };

            scoped ReadOnlySpan<char> folded;
            if (expansion is not null)
            {
                folded = expansion;
            }
            else
            {
                Rune simple = Rune.ToLowerInvariant(Rune.ToUpperInvariant(rune));
                Rune lowered = rune.IsBmp ? new Rune(char.ToLowerInvariant((char)rune.Value)) : rune;
                if (simple == lowered)
                {
                    sb?.Append(text, start, length);
                    continue;
                }

                folded = buffer[..simple.EncodeToUtf16(buffer)];
            }

            sb ??= new StringBuilder(text.Length + 4).Append(text, 0, start);
            sb.Append(folded);
        }

        return sb?.ToString() ?? text;
    }

    /// <summary>
    /// True when <paramref name="start"/> and <paramref name="start"/> + <paramref name="length"/> are
    /// both extended grapheme cluster boundaries of <paramref name="word"/> (UAX #29).
    /// </summary>
    internal static bool IsOnGraphemeBoundaries(ReadOnlySpan<char> word, int start, int length)
    {
        int end = start + length;
        int position = 0;
        bool startOk = start == 0;
        while (position < end)
        {
            position += StringInfo.GetNextTextElementLength(word[position..]);
            if (position == start)
                startOk = true;
        }

        return startOk && position == end;
    }
}
