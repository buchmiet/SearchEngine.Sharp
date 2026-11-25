using System.Buffers;
using System.Globalization;
using System.Text;

namespace SearchEngine;

/// <summary>
/// Optional Unicode-aware matching, set through <see cref="SearchTokenization.WithUnicodeMatching"/>. The folding
/// options (<see cref="Compose"/>, <see cref="CaseFold"/>, <see cref="RemoveDiacritics"/>) are applied to indexed
/// text and to query terms. Pure ASCII text is never changed, so only names with non-ASCII characters pay for them.
/// </summary>
/// <remarks>
/// A <see cref="WordMatchMethod.Regex"/> pattern is not folded, because folding would change its character classes;
/// it runs as written against the folded tokens. <see cref="IndexedEntry.SortText"/> is never folded.
/// </remarks>
[Flags]
public enum UnicodeMatching
{
    /// <summary>No Unicode processing: text is only lowercased character by character (invariant culture). The
    /// default.</summary>
    None = 0,

    /// <summary>
    /// Canonical composition (NFC): a decomposed <c>e</c> + U+0301 and a precomposed <c>é</c>, or Hangul
    /// jamo and syllables, become the same token. macOS file names are often stored decomposed (NFD). Needs
    /// Unicode normalization, which globalization-invariant mode does not have.
    /// </summary>
    Compose = 1,

    /// <summary>
    /// Unicode simple case folding, so case variants that per-character lowercase keeps apart match: final
    /// <c>ς</c> and <c>σ</c>, <c>µ</c> and <c>μ</c>, Greek symbol variants such as <c>ϐ</c>, and
    /// supplementary-plane letters such as Deseret <c>𐐀</c> and <c>𐐨</c>. On top of that come the full foldings
    /// <c>ß</c>/<c>ẞ</c>→<c>ss</c> and the Latin ligatures U+FB00 to U+FB06, and <c>İ</c>→<c>i</c>,
    /// <c>ſ</c>→<c>s</c>. Other full foldings (<c>ŉ</c>, Greek letters with ypogegrammeni, Armenian ligatures) are
    /// not applied.
    /// </summary>
    CaseFold = 2,

    /// <summary>
    /// Accent folding: canonical decomposition, removal of the accents of Latin, Greek and Cyrillic letters (the
    /// non-spacing marks of the Combining Diacritical Marks blocks U+0300 to U+036F, U+1AB0 to U+1AFF, U+1DC0 to
    /// U+1DFF and U+FE20 to U+FE2F), then composition (<c>é</c>→<c>e</c>, <c>ą</c>→<c>a</c>, <c>ü</c>→<c>u</c>,
    /// <c>ά</c>→<c>α</c>, <c>й</c>→<c>и</c>), plus <c>ł</c>→<c>l</c>, <c>ø</c>→<c>o</c>, <c>đ</c>→<c>d</c>,
    /// <c>ħ</c>→<c>h</c>, <c>ı</c>→<c>i</c>, which have no decomposition. Marks of other scripts are part of the
    /// letter and stay (Japanese dakuten: <c>バ</c> ≠ <c>ハ</c>; Thai tone marks; Indic vowel signs). Implies
    /// <see cref="Compose"/> and, like it, needs Unicode normalization.
    /// The folding does not know the language. <c>й</c> and <c>ў</c> become <c>и</c> and <c>у</c>, which are
    /// different letters in Russian and Belarusian, so words that differ only by them match each other; leave this
    /// option off for such texts.
    /// </summary>
    RemoveDiacritics = 4,

    /// <summary>
    /// Matches must not split an extended grapheme cluster (UAX #29): a <see cref="WordMatchMethod.Within"/> match
    /// starts and ends on cluster boundaries, and so does every place where a glob <c>*</c> or <c>?</c> meets
    /// literal text. <c>cafe</c> and <c>cafe*</c> do not match a decomposed <c>café</c>, and one emoji (or
    /// emoji followed by <c>*</c>) does not match inside a ZWJ sequence. Exact matches without wildcards are whole
    /// tokens and need no check.
    /// </summary>
    GraphemeBoundaries = 8,
}

internal static class TextFolder
{
    // Globalization-invariant mode (InvariantGlobalization, DOTNET_SYSTEM_GLOBALIZATION_INVARIANT; the default for
    // some trimmed and container deployments) has no normalization data: string.Normalize returns its input and
    // IsNormalized reports true, so Compose and RemoveDiacritics would silently have no effect. A decomposed é is not
    // NFC wherever normalization works.
    internal static bool IsNormalizationAvailable { get; } = !"e\u0301".IsNormalized(NormalizationForm.FormC);

    internal const UnicodeMatching FoldingOptions = UnicodeMatching.Compose | UnicodeMatching.CaseFold | UnicodeMatching.RemoveDiacritics;

    /// <summary>
    /// Folds indexed text: every run between <paramref name="separators"/> is folded on its own (<see cref="Fold"/>)
    /// and the separators are kept. Text is split as written, before folding, exactly like a query, so a combining
    /// mark after a separator never composes with it. Returns <paramref name="text"/> itself when nothing changes.
    /// </summary>
    internal static string FoldRuns(string text, UnicodeMatching options, SearchValues<char> separators)
    {
        if ((options & FoldingOptions) == 0 || Ascii.IsValid(text))
            return text;

        StringBuilder? sb = null;
        int copied = 0;
        int position = 0;
        while (position < text.Length)
        {
            int offset = text.AsSpan(position).IndexOfAnyExcept(separators);
            if (offset < 0)
                break;

            int start = position + offset;
            int length = text.AsSpan(start).IndexOfAny(separators);
            position = length < 0 ? text.Length : start + length;
            var run = text.AsSpan(start, position - start);
            if (Ascii.IsValid(run))
                continue;

            string folded = Fold(run.Length == text.Length ? text : run.ToString(), options, separators);
            if (folded.AsSpan().SequenceEqual(run))
                continue;

            sb ??= new StringBuilder(text.Length + 8);
            sb.Append(text, copied, start - copied).Append(folded);
            copied = position;
        }

        return sb?.Append(text, copied, text.Length - copied).ToString() ?? text;
    }

    /// <summary>
    /// Folds one run of text that holds no separator as written: a token of indexed text or a query term. The steps
    /// are composition (or accent removal), case folding, and then, when composing, the per-character lowercase that
    /// the tokenizer applies anyway followed by composition again: lowercasing can undo NFC (T + U+0308 has no
    /// precomposed form, t + U+0308 composes to U+1E97), so composition comes last. A character whose lowercase is
    /// one of <paramref name="separators"/> is left for the tokenizer, so lowercasing here never moves a token
    /// boundary.
    /// </summary>
    internal static string Fold(string text, UnicodeMatching options, SearchValues<char>? separators = null)
    {
        if ((options & FoldingOptions) == 0 || string.IsNullOrEmpty(text) || Ascii.IsValid(text))
            return text;

        bool removeDiacritics = (options & UnicodeMatching.RemoveDiacritics) != 0;
        bool normalize = removeDiacritics || (options & UnicodeMatching.Compose) != 0;
        if (normalize)
            text = NormalizeWellFormedRuns(text, removeDiacritics);

        string composed = text;
        if ((options & UnicodeMatching.CaseFold) != 0)
            text = ApplyCaseFolding(text);

        // Case folding can undo NFC as well (U+0130 + U+0301 folds to i + U+0301, which composes to U+00ED).
        if (normalize)
        {
            text = Lowercase(text, separators);
            if (!ReferenceEquals(text, composed))
                text = NormalizeWellFormedRuns(text, removeDiacritics);
        }

        return text;
    }

    // TextNormalizer.ToLowerInvariantFast per UTF-16 unit, except for characters whose lowercase is a separator.
    private static string Lowercase(string text, SearchValues<char>? separators)
    {
        int first = 0;
        while (first < text.Length && !LowersHere(text[first], separators))
            first++;

        if (first == text.Length)
            return text;

        return string.Create(text.Length, (text, separators, first), static (destination, state) =>
        {
            state.text.AsSpan(0, state.first).CopyTo(destination);
            for (int i = state.first; i < destination.Length; i++)
            {
                char ch = state.text[i];
                destination[i] = LowersHere(ch, state.separators) ? Tokenizer.TextNormalizer.ToLowerInvariantFast(ch) : ch;
            }
        });
    }

    private static bool LowersHere(char ch, SearchValues<char>? separators)
    {
        char lower = Tokenizer.TextNormalizer.ToLowerInvariantFast(ch);
        return lower != ch && (separators is null || !separators.Contains(lower));
    }

    // string.Normalize and string.IsNormalized throw ArgumentException on ill-formed UTF-16 (a lone surrogate, which
    // file systems such as NTFS allow in names) and on noncharacters: ICU rejects U+FFFE, Windows NLS
    // (DOTNET_SYSTEM_GLOBALIZATION_USENLS) all 66 of them. Such a code point takes no part in composition (it has no
    // decomposition, combining class 0 and no composition partner), so it is kept as it is and the runs around it are
    // normalized on their own. The result is the one ICU gives for the whole text, on every backend, and the text
    // still matches itself.
    private static string NormalizeWellFormedRuns(string text, bool removeDiacritics)
    {
        int invalid = IndexOfUnnormalizable(text, out int length);
        if (invalid < 0)
            return Normalize(text, removeDiacritics);

        var sb = new StringBuilder(text.Length);
        int start = 0;
        while (invalid >= 0)
        {
            sb.Append(Normalize(text[start..invalid], removeDiacritics)).Append(text, invalid, length);
            start = invalid + length;
            int next = IndexOfUnnormalizable(text.AsSpan(start), out length);
            invalid = next < 0 ? -1 : start + next;
        }

        return sb.Append(Normalize(text[start..], removeDiacritics)).ToString();
    }

    // Index and length (1 or 2 UTF-16 units) of the first lone surrogate or noncharacter, or -1.
    private static int IndexOfUnnormalizable(ReadOnlySpan<char> text, out int length)
    {
        length = 1;
        if (!text.ContainsAnyInRange('\uD800', '\uDFFF') && !text.ContainsAnyInRange('\uFDD0', '\uFDEF') && !text.ContainsAny('\uFFFE', '\uFFFF'))
            return -1;

        for (int i = 0; i < text.Length; i++)
        {
            if (Rune.DecodeFromUtf16(text[i..], out Rune rune, out int consumed) != OperationStatus.Done)
                return i; // a lone surrogate

            if (IsNoncharacter(rune.Value))
            {
                length = consumed;
                return i;
            }

            i += consumed - 1;
        }

        return -1;
    }

    // U+FDD0 to U+FDEF and the last two code points of every plane (Unicode 15.1, section 23.7).
    private static bool IsNoncharacter(int codePoint) => codePoint is >= 0xFDD0 and <= 0xFDEF || (codePoint & 0xFFFE) == 0xFFFE;

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
    /// Index of the first occurrence of <paramref name="value"/> in <paramref name="word"/> that starts and ends on
    /// extended grapheme cluster boundaries, or -1: the substring match of <see cref="UnicodeMatching.GraphemeBoundaries"/>
    /// for <see cref="WordMatchMethod.Within"/> and <c>*value*</c> globs.
    /// </summary>
    internal static int IndexOfOnGraphemeBoundaries(ReadOnlySpan<char> word, ReadOnlySpan<char> value)
    {
        int index = word.IndexOf(value);
        if (index < 0)
            return -1;

        var boundaries = new GraphemeBoundaryMap(word, word.Length < GraphemeBoundaryMap.MaxStackLength ? stackalloc bool[word.Length + 1] : default);
        try
        {
            while (index >= 0 && !(boundaries.IsBoundary(index) && boundaries.IsBoundary(index + value.Length)))
            {
                int next = word[(index + 1)..].IndexOf(value);
                index = next < 0 ? -1 : index + 1 + next;
            }

            return index;
        }
        finally
        {
            boundaries.Dispose();
        }
    }
}

/// <summary>
/// The extended grapheme cluster boundaries (UAX #29) of one word. The first test that cannot be answered from the two
/// characters around the position finds all boundaries in one forward walk; every later test reads that table, so
/// testing many positions costs one walk instead of one walk per test.
/// </summary>
/// <remarks>Not copied once used (the table and its state live in the instance), and not declared in a
/// <see langword="using"/> statement, whose variable is read-only: call <see cref="Dispose"/> in a
/// <see langword="finally"/> block.</remarks>
internal ref struct GraphemeBoundaryMap
{
    /// <summary>Words shorter than this get their table on the stack from the caller; longer ones rent it.</summary>
    internal const int MaxStackLength = 256;

    private readonly ReadOnlySpan<char> _word;
    private Span<bool> _isBoundary;
    private bool[]? _rented;
    private bool _built;

    /// <param name="word">The word.</param>
    /// <param name="buffer">Room for <c>word.Length + 1</c> flags, or empty to rent it from the shared pool when
    /// needed.</param>
    internal GraphemeBoundaryMap(ReadOnlySpan<char> word, Span<bool> buffer)
    {
        _word = word;
        _isBoundary = buffer;
    }

    /// <summary>True when <paramref name="position"/> (0 to the word's length) is a cluster boundary.</summary>
    internal bool IsBoundary(int position)
    {
        if (position <= 0 || position >= _word.Length)
            return true;

        // Two ASCII characters are always split, except CR LF (UAX #29 rules GB3 to GB5).
        char before = _word[position - 1];
        char after = _word[position];
        if (char.IsAscii(before) && char.IsAscii(after))
            return before != '\r' || after != '\n';

        if (!_built)
            Build();

        return _isBoundary[position];
    }

    private void Build()
    {
        if (_isBoundary.Length < _word.Length + 1)
            _isBoundary = _rented = ArrayPool<bool>.Shared.Rent(_word.Length + 1);

        _isBoundary[.._word.Length].Clear();
        for (int boundary = 0; boundary < _word.Length; boundary += StringInfo.GetNextTextElementLength(_word[boundary..]))
            _isBoundary[boundary] = true;

        _isBoundary[_word.Length] = true;
        _built = true;
    }

    /// <summary>Returns a rented table to the pool.</summary>
    public void Dispose()
    {
        if (_rented is not null)
            ArrayPool<bool>.Shared.Return(_rented);

        _rented = null;
    }
}
