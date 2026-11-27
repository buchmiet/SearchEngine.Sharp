using System.Globalization;
using System.Text;
using SearchEngine.Tokenizer;

namespace SearchEngine.Query;

/// <summary>
/// Gives the literal characters of a regex pattern the casing that indexed tokens have: case folding under
/// <see cref="UnicodeMatching.CaseFold"/> (<see cref="TextFolder.ApplyCaseFolding"/>), then the per-character lowercase
/// (<see cref="TextNormalizer.ToLowerInvariantFast(char)"/>), both from the Unicode tables in UnicodeCasing.g.cs. The
/// pattern then runs case-sensitively, so the case tables of .NET (<c>RegexOptions.IgnoreCase</c>) play no part.
/// </summary>
/// <remarks>
/// Only characters that stand for themselves change: plain characters, <c>\uXXXX</c> and <c>\xXX</c>. Escapes such as
/// <c>\D</c> or <c>\p{Lu}</c>, group names, inline options and comments are copied as written. A character whose folding
/// is several characters (<c>ß</c> to <c>ss</c> under CaseFold) becomes the group <c>(?:ss)</c>, so a quantifier after it
/// applies to all of them. A character class keeps what it lists and gains the folded form of each character and range;
/// the several-character foldings become alternatives, <c>[aß]</c> giving <c>(?:[aß]|ss)</c>. A negated class, or one with
/// a subtraction, cannot take alternatives and keeps only the one-character foldings. The rewriting never rejects a
/// pattern: an invalid one stays invalid and fails to compile as before.
/// </remarks>
internal static class RegexCaseFolding
{
    internal static string Apply(string pattern, bool caseFold) => new Rewriter(pattern, caseFold).Run();

    /// <summary>The casing that one character (one code point, in its UTF-16 form) has in indexed tokens.</summary>
    internal static string FoldCharacter(string character, bool caseFold)
    {
        string folded = caseFold ? TextFolder.ApplyCaseFolding(character) : character;
        return TextNormalizer.CreateNormalizedWord(folded);
    }

    private sealed class Rewriter(string pattern, bool caseFold)
    {
        private readonly StringBuilder _output = new(pattern.Length + 8);
        private int _position;

        public string Run()
        {
            while (_position < pattern.Length)
            {
                switch (pattern[_position])
                {
                    case '\\':
                        Escape();
                        break;
                    case '[':
                        Class();
                        break;
                    case '(' when Peek(1) == '?':
                        Group();
                        break;
                    default:
                        Literal(TakeCharacter());
                        break;
                }
            }

            string result = _output.ToString();
            return result == pattern ? pattern : result;
        }

        private char Peek(int offset) => _position + offset < pattern.Length ? pattern[_position + offset] : '\0';

        /// <summary>One code point: a surrogate pair, or a single UTF-16 unit (a lone surrogate too).</summary>
        private string TakeCharacter()
        {
            int length = char.IsHighSurrogate(pattern[_position]) && char.IsLowSurrogate(Peek(1)) ? 2 : 1;
            string character = pattern.Substring(_position, length);
            _position += length;
            return character;
        }

        private void Copy(int length)
        {
            length = Math.Min(length, pattern.Length - _position);
            _output.Append(pattern, _position, length);
            _position += length;
        }

        /// <summary>Copies from <paramref name="skip"/> characters on through the first <paramref name="terminator"/>
        /// (inclusive), or to the end of the pattern.</summary>
        private void CopyThrough(int skip, char terminator)
        {
            int end = pattern.IndexOf(terminator, Math.Min(_position + skip, pattern.Length));
            Copy(end < 0 ? pattern.Length - _position : end - _position + 1);
        }

        private void Literal(string character)
        {
            string folded = FoldCharacter(character, caseFold);
            if (folded == character)
                _output.Append(character);
            else if (CodePointCount(folded) == 1)
                _output.Append(folded);
            else
                _output.Append("(?:").Append(folded).Append(')');
        }

        // Outside a class.
        private void Escape()
        {
            if (!TryCodePointEscape(out int codePoint, out int length))
            {
                Copy(OtherEscapeLength());
                return;
            }

            string character = char.ConvertFromUtf32(codePoint);
            string folded = FoldCharacter(character, caseFold);
            if (folded == character)
            {
                Copy(length);
                return;
            }

            _position += length;
            bool group = CodePointCount(folded) > 1;
            if (group) _output.Append("(?:");
            AppendEscaped(_output, folded);
            if (group) _output.Append(')');
        }

        /// <summary><c>\uXXXX</c> or <c>\xXX</c> at the current position.</summary>
        private bool TryCodePointEscape(out int codePoint, out int length)
        {
            int digits = Peek(1) switch { 'u' => 4, 'x' => 2, _ => 0 };
            codePoint = 0;
            length = 2 + digits;
            return digits != 0
                   && _position + length <= pattern.Length
                   && int.TryParse(pattern.AsSpan(_position + 2, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out codePoint)
                   && !char.IsSurrogate((char)codePoint);
        }

        /// <summary>The length of an escape that is not a code point of its own and is copied as written:
        /// <c>\p{L}</c>, <c>\k&lt;name&gt;</c>, <c>\cX</c>, a backreference or octal number, or a backslash and one
        /// character (<c>\d</c>, <c>\W</c>, <c>\.</c>). A backslash that ends the pattern stays alone.</summary>
        private int OtherEscapeLength()
        {
            int end;
            switch (Peek(1))
            {
                case 'p' or 'P' when Peek(2) == '{':
                    end = pattern.IndexOf('}', _position);
                    break;
                case 'k' when Peek(2) is '<' or '\'':
                    end = pattern.IndexOf(Peek(2) == '<' ? '>' : '\'', _position + 3);
                    break;
                case 'c':
                    return Math.Min(3, pattern.Length - _position);
                case >= '0' and <= '9':
                    end = _position + 1;
                    while (end + 1 < pattern.Length && char.IsAsciiDigit(pattern[end + 1])) end++;
                    break;
                default:
                    return Math.Min(char.IsHighSurrogate(Peek(1)) && char.IsLowSurrogate(Peek(2)) ? 3 : 2, pattern.Length - _position);
            }

            return end < 0 ? pattern.Length - _position : end - _position + 1;
        }

        private void Group()
        {
            char kind = Peek(2);
            switch (kind)
            {
                case '<' when Peek(3) is not ('=' or '!'):
                    CopyThrough(3, '>'); // (?<name> and (?<name-other>
                    break;
                case '\'':
                    CopyThrough(3, '\''); // (?'name'
                    break;
                case '#':
                    CopyThrough(2, ')'); // (?#comment)
                    break;
                case '(' when Peek(3) != '?':
                    CopyThrough(3, ')'); // (?(name) and (?(1)
                    break;
                case '<':
                    Copy(4); // (?<= and (?<!
                    break;
                case '(' or ':' or '=' or '!' or '>':
                    Copy(3); // (?(?=...) has a pattern as its condition
                    break;
                default:
                    // Inline options, (?imnsx-imnsx) or (?imnsx-imnsx:
                    int end = _position + 2;
                    while (end < pattern.Length && pattern[end] is not (')' or ':')) end++;
                    Copy(end - _position + 1);
                    break;
            }
        }

        private void Class()
        {
            int start = _position;
            var parsed = ParseClass();
            if (parsed is null)
            {
                // A class that does not close: copy the rest as written and let compilation report it.
                _position = start;
                Copy(pattern.Length - start);
                return;
            }

            if (parsed.Alternatives.Count == 0)
            {
                _output.Append(parsed.Text);
                return;
            }

            _output.Append("(?:").Append(parsed.Text);
            foreach (string alternative in parsed.Alternatives)
            {
                _output.Append('|');
                AppendEscaped(_output, alternative);
            }

            _output.Append(')');
        }

        private sealed record ParsedClass(string Text, List<string> Alternatives);

        /// <summary>Parses the class at the current position (on its <c>[</c>) and returns it with the folded
        /// characters added; null when the class does not close.</summary>
        private ParsedClass? ParseClass()
        {
            var text = new StringBuilder("[");
            _position++;
            bool negated = Peek(0) == '^';
            if (negated)
            {
                text.Append('^');
                _position++;
            }

            int body = text.Length;
            var listed = new List<(int First, int Last)>();
            var added = new SortedSet<int>();
            var alternatives = new List<string>();
            string? subtraction = null;
            while (true)
            {
                if (_position >= pattern.Length)
                    return null;

                char ch = pattern[_position];
                if (ch == ']' && text.Length > body) // a ] right after [ or [^ is a literal
                {
                    _position++;
                    break;
                }

                if (ch == '-' && Peek(1) == '[' && text.Length > body)
                {
                    // A subtraction, [a-z-[aeiou]], is the last part of a class.
                    _position++;
                    var inner = ParseClass();
                    if (inner is null)
                        return null;
                    subtraction = "-" + inner.Text;
                    continue;
                }

                int elementStart = _position;
                if (ClassCharacter() is not int low)
                {
                    text.Append(pattern, elementStart, _position - elementStart);
                    continue;
                }

                int high = low;
                if (Peek(0) == '-' && Peek(1) is not ('\0' or ']' or '['))
                {
                    int dash = _position;
                    _position++;
                    if (ClassCharacter() is int last)
                        high = last;
                    else
                        _position = dash; // [a-\d]: compilation reports it
                }

                text.Append(pattern, elementStart, _position - elementStart);
                if (high < low)
                    continue; // a range in reverse order; compilation reports it

                listed.Add((low, high));
                for (int codePoint = low; codePoint <= high; codePoint++)
                {
                    if (codePoint is >= 0xD800 and <= 0xDFFF)
                        continue;
                    string character = char.ConvertFromUtf32(codePoint);
                    string folded = FoldCharacter(character, caseFold);
                    if (folded == character)
                        continue;
                    if (folded.Length == 1)
                        added.Add(folded[0]);
                    else if (!alternatives.Contains(folded))
                        alternatives.Add(folded);
                }
            }

            AppendRanges(text, added.Where(codePoint => !listed.Exists(range => range.First <= codePoint && codePoint <= range.Last)));
            if (negated || subtraction is not null)
                alternatives.Clear();
            text.Append(subtraction).Append(']');
            return new ParsedClass(text.ToString(), alternatives);
        }

        /// <summary>One character of a class as a code point, consuming it: a plain character or a code-point
        /// escape (<c>\uXXXX</c>, <c>\xXX</c>, an escaped punctuation character such as <c>\-</c>). Null for a class
        /// escape such as <c>\d</c> or <c>\p{L}</c>, which is consumed too, and for a lone surrogate.</summary>
        private int? ClassCharacter()
        {
            if (pattern[_position] != '\\')
            {
                string character = TakeCharacter();
                return char.IsSurrogate(character[0]) && character.Length == 1 ? null : char.ConvertToUtf32(character, 0);
            }

            if (TryCodePointEscape(out int codePoint, out int length))
            {
                _position += length;
                return codePoint;
            }

            char next = Peek(1);
            if (next is not '\0' && !char.IsAsciiLetterOrDigit(next) && !char.IsSurrogate(next))
            {
                _position += 2;
                return next;
            }

            _position += OtherEscapeLength();
            return null;
        }

        /// <summary>Appends ascending BMP code points to a class, consecutive ones as a range.</summary>
        private static void AppendRanges(StringBuilder text, IEnumerable<int> codePoints)
        {
            int first = -1, last = -1;
            foreach (int codePoint in codePoints)
            {
                if (first >= 0 && codePoint == last + 1)
                {
                    last = codePoint;
                    continue;
                }

                AppendRange(text, first, last);
                first = last = codePoint;
            }

            AppendRange(text, first, last);
        }

        private static void AppendRange(StringBuilder text, int first, int last)
        {
            if (first < 0)
                return;
            AppendEscaped(text, ((char)first).ToString());
            if (last == first)
                return;
            if (last > first + 1)
                text.Append('-');
            AppendEscaped(text, ((char)last).ToString());
        }

        private static int CodePointCount(string text)
        {
            int count = 0;
            for (int i = 0; i < text.Length; i++, count++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    i++;
            }

            return count;
        }

        /// <summary>Appends characters produced by folding: letters and marks as they are, anything else as
        /// <c>\uXXXX</c>, so no character is read as regex syntax.</summary>
        private static void AppendEscaped(StringBuilder output, string text)
        {
            foreach (char ch in text)
            {
                if (char.IsLetter(ch) || char.IsSurrogate(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.NonSpacingMark
                        or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                    output.Append(ch);
                else
                    output.Append(@"\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
            }
        }
    }
}
