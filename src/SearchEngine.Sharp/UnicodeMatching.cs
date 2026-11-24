using System.Buffers;
using System.Globalization;
using System.Text;

namespace SearchEngine;

/// <summary>
/// Optional Unicode-aware matching. The folding options are applied to indexed text and to queries
/// before tokenization; pure ASCII text is never changed, so the cost is paid only for names that
/// contain non-ASCII characters. Set through <see cref="SearchTokenization.WithUnicodeMatching"/>.
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
    /// Case folding for the characters where Unicode full or simple case folding differs from
    /// per-character lowercase: <c>ß</c>/<c>ẞ</c>→<c>ss</c>, final <c>ς</c>→<c>σ</c>, <c>İ</c>→<c>i</c>,
    /// <c>ſ</c>→<c>s</c>, <c>µ</c>→<c>μ</c>, Greek symbol variants, and the Latin ligatures U+FB00–U+FB06.
    /// Not applied to regular expressions (it would change character classes).
    /// </summary>
    CaseFold = 2,

    /// <summary>
    /// Accent folding: canonical decomposition, removal of non-spacing marks, then composition
    /// (<c>é</c>→<c>e</c>, <c>ą</c>→<c>a</c>, <c>ü</c>→<c>u</c>), plus <c>ł</c>→<c>l</c>, <c>ø</c>→<c>o</c>,
    /// <c>đ</c>→<c>d</c>, <c>ħ</c>→<c>h</c>, <c>ı</c>→<c>i</c>, which have no decomposition. Implies <see cref="Compose"/>.
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
    private static readonly SearchValues<char> CaseFoldSpecials =
        SearchValues.Create("ßẞςİſµϐϑϕϖϰϱϵẛͅﬀﬁﬂﬃﬄﬅﬆ");

    internal static string Fold(string text, UnicodeMatching normalization, bool isRegex)
    {
        const UnicodeMatching folding = UnicodeMatching.Compose | UnicodeMatching.CaseFold | UnicodeMatching.RemoveDiacritics;
        if ((normalization & folding) == 0 || string.IsNullOrEmpty(text) || Ascii.IsValid(text))
            return text;

        if ((normalization & UnicodeMatching.RemoveDiacritics) != 0)
            text = RemoveDiacritics(text);
        else if ((normalization & UnicodeMatching.Compose) != 0 && !text.IsNormalized(NormalizationForm.FormC))
            text = text.Normalize(NormalizationForm.FormC);

        if ((normalization & UnicodeMatching.CaseFold) != 0 && !isRegex && text.AsSpan().ContainsAny(CaseFoldSpecials))
            text = ApplyCaseFoldSpecials(text);

        return text;
    }

    private static string RemoveDiacritics(string text)
    {
        string decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            sb.Append(ch switch
            {
                'ł' => 'l', 'Ł' => 'L', 'ø' => 'o', 'Ø' => 'O', 'đ' => 'd', 'Đ' => 'D', 'ħ' => 'h', 'Ħ' => 'H', 'ı' => 'i',
                _ => ch,
            });
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string ApplyCaseFoldSpecials(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        foreach (char ch in text)
        {
            switch (ch)
            {
                case 'ß' or 'ẞ': sb.Append("ss"); break;
                case 'ς': sb.Append('σ'); break;
                case 'İ': sb.Append('i'); break;
                case 'ſ': sb.Append('s'); break;
                case 'µ': sb.Append('μ'); break;
                case 'ϐ': sb.Append('β'); break;
                case 'ϑ': sb.Append('θ'); break;
                case 'ϕ': sb.Append('φ'); break;
                case 'ϖ': sb.Append('π'); break;
                case 'ϰ': sb.Append('κ'); break;
                case 'ϱ': sb.Append('ρ'); break;
                case 'ϵ': sb.Append('ε'); break;
                case 'ẛ': sb.Append('ṡ'); break;
                case 'ͅ': sb.Append('ι'); break;
                case 'ﬀ': sb.Append("ff"); break;
                case 'ﬁ': sb.Append("fi"); break;
                case 'ﬂ': sb.Append("fl"); break;
                case 'ﬃ': sb.Append("ffi"); break;
                case 'ﬄ': sb.Append("ffl"); break;
                case 'ﬅ' or 'ﬆ': sb.Append("st"); break;
                default: sb.Append(ch); break;
            }
        }

        return sb.ToString();
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
