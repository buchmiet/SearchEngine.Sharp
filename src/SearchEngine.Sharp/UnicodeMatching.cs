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

    // Globalization-invariant mode (InvariantGlobalization, DOTNET_SYSTEM_GLOBALIZATION_INVARIANT; the default for
    // some trimmed and container deployments) has no normalization data: string.Normalize returns its input and
    // IsNormalized reports true, so Compose and RemoveDiacritics would silently match nothing. A decomposed é is not
    // NFC wherever normalization works.
    internal static bool IsNormalizationAvailable { get; } = !"e\u0301".IsNormalized(NormalizationForm.FormC);

    internal static string Fold(string text, UnicodeMatching normalization, bool isRegex)
    {
        const UnicodeMatching folding = UnicodeMatching.Compose | UnicodeMatching.CaseFold | UnicodeMatching.RemoveDiacritics;
        if ((normalization & folding) == 0 || string.IsNullOrEmpty(text) || Ascii.IsValid(text))
            return text;

        bool removeDiacritics = (normalization & UnicodeMatching.RemoveDiacritics) != 0;
        if (removeDiacritics || (normalization & UnicodeMatching.Compose) != 0)
            text = NormalizeWellFormedRuns(text, removeDiacritics);

        if ((normalization & UnicodeMatching.CaseFold) != 0 && !isRegex && text.AsSpan().ContainsAny(CaseFoldSpecials))
            text = ApplyCaseFoldSpecials(text);

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
