using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace SearchEngine.Profiling;

/// <summary>
/// Name predicate with the same semantics as SearchEngine.Sharp under SearchTokenization.FileMask:
/// whole lowercased name is the token; query split on space/CR/LF; AND/OR/NOT (ASCII, any case),
/// implicit AND, NOT &gt; AND &gt; OR; terms with * or ? are anchored globs; "*" alone matches all;
/// other terms are whole-name equality. Regex: unanchored IsMatch on the lowercased name,
/// IgnoreCase | CultureInvariant, NonBacktracking when supported.
/// Parentheses are not separators in FileMask, so they stay part of a term (as in the package).
/// </summary>
public sealed class NamePredicate
{
    enum Op { None, And, Or, Not }
    readonly record struct Tok(string Word, Op Op);

    readonly Tok[]? _postfix;
    readonly Regex? _regex;
    readonly bool _never;

    NamePredicate(Tok[]? postfix, Regex? regex, bool never) { _postfix = postfix; _regex = regex; _never = never; }

    public static NamePredicate Compile(string expression, bool regex)
    {
        if (regex)
        {
            try { return new(null, new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)), false); }
            catch (NotSupportedException) { return new(null, new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), false); }
            catch (ArgumentException) { return new(null, null, true); }
        }

        var tokens = new List<Tok>();
        foreach (var raw in expression.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string lower = Lower(raw);
            tokens.Add(lower switch { "and" => new("", Op.And), "or" => new("", Op.Or), "not" => new("", Op.Not), _ => new(lower, Op.None) });
        }

        // CorrectTokens, as in QueryExpressionEvaluator.
        while (tokens.Count > 0 && tokens[0].Op is Op.Or or Op.And) tokens.RemoveAt(0);
        while (tokens.Count > 0 && tokens[^1].Op is not Op.None) tokens.RemoveAt(tokens.Count - 1);
        try
        {
            for (int i = 0; i < tokens.Count - 1; i++)
            {
                // CorrectOperationSequence
                var a = tokens[i].Op; var b = tokens[i + 1].Op;
                if (a != Op.None && b != Op.None)
                {
                    if (a != Op.Not && b == Op.Not) { }
                    else if (a == Op.Not && b == Op.Not) tokens.RemoveAt(i + 1);
                    else { tokens[i] = new("", Op.And); tokens.RemoveAt(i + 1); i--; }
                }
                // InsertDefaultAnd (same indexing as the package, including i == -1)
                if (i >= tokens.Count - 1) continue;
                if (tokens[i].Op == Op.None && tokens[i + 1].Op is Op.None or Op.Not)
                    tokens.Insert(i + 1, new("", Op.And));
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return new(null, null, true); // the package throws here; see report
        }
        if (tokens.Count == 0) return new(null, null, true);

        // Shunting-yard to postfix (left-associative, NOT highest).
        var output = new List<Tok>();
        var ops = new Stack<Op>();
        static int Prec(Op o) => o switch { Op.Or => 1, Op.And => 2, Op.Not => 3, _ => -1 };
        foreach (var t in tokens)
        {
            if (t.Op == Op.None) { output.Add(t); continue; }
            while (ops.Count > 0 && Prec(ops.Peek()) >= Prec(t.Op)) output.Add(new("", ops.Pop()));
            ops.Push(t.Op);
        }
        while (ops.Count > 0) output.Add(new("", ops.Pop()));
        return new([.. output], null, false);
    }

    static string Lower(ReadOnlySpan<char> s)
    {
        return string.Create(s.Length, s, static (d, src) => { for (int i = 0; i < src.Length; i++) d[i] = char.ToLowerInvariant(src[i]); });
    }

    /// <summary>Matches a raw (not yet lowercased) file name.</summary>
    public bool IsMatch(ReadOnlySpan<char> name)
    {
        if (_never) return false;
        Span<char> buffer = name.Length <= 512 ? stackalloc char[name.Length] : new char[name.Length];
        for (int i = 0; i < name.Length; i++) buffer[i] = char.ToLowerInvariant(name[i]);
        ReadOnlySpan<char> lower = buffer;
        if (_regex is not null) return _regex.IsMatch(lower);

        Span<bool> stack = stackalloc bool[_postfix!.Length];
        int sp = 0;
        foreach (var t in _postfix)
        {
            switch (t.Op)
            {
                case Op.None: stack[sp++] = Term(t.Word, lower); break;
                case Op.Not: if (sp < 1) return false; stack[sp - 1] = !stack[sp - 1]; break;
                case Op.And: if (sp < 2) return false; sp--; stack[sp - 1] &= stack[sp]; break;
                case Op.Or: if (sp < 2) return false; sp--; stack[sp - 1] |= stack[sp]; break;
            }
        }
        return sp > 0 && stack[sp - 1];
    }

    static bool Term(string pattern, ReadOnlySpan<char> word)
    {
        if (pattern == "*") return true;
        if (pattern.AsSpan().IndexOfAny('*', '?') < 0) return word.SequenceEqual(pattern);
        return Glob(pattern, word);
    }

    // Same algorithm as SearchEngine.Query.GlobMatcher.IsWholeWordMatch.
    internal static bool Glob(ReadOnlySpan<char> pattern, ReadOnlySpan<char> word)
    {
        int p = 0, w = 0, starP = -1, starW = 0;
        while (w < word.Length)
        {
            if (p < pattern.Length)
            {
                char pc = pattern[p];
                if (pc == '*') { starP = p; starW = w; p++; while (p < pattern.Length && pattern[p] == '*') p++; continue; }
                if (pc == '?' || pc == word[w]) { p++; w++; continue; }
            }
            if (starP >= 0) { starW++; w = starW; p = starP + 1; while (p < pattern.Length && pattern[p] == '*') p++; continue; }
            return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}

/// <summary>Facet predicates of a request evaluated on enumeration data (no extra stat).</summary>
public readonly struct EntryFacets(FindRequest r)
{
    readonly long _min = r.SizeMin ?? 0, _max = r.SizeMax ?? long.MaxValue;
    readonly bool _size = r.SizeMin is not null || r.SizeMax is not null;
    readonly long _cutoff = r.NotOlderThanDays is { } d and > 0 ? CommanderPattern.Cutoff(d) : long.MinValue;
    readonly long _mustHave = r.AttributeMustHave;

    public bool Matches(ref FileSystemEntry e)
    {
        if (_size) { long len = e.Length; if (len < _min || len > _max) return false; }
        if (_cutoff != long.MinValue && e.LastWriteTimeUtc.UtcTicks < _cutoff) return false;
        if (_mustHave != 0 && ((long)e.Attributes & _mustHave) != _mustHave) return false;
        return true;
    }
}

/// <summary>Variant C (single-threaded) and D (parallel directories) streaming scans without an index.</summary>
public static class StreamingScan
{
    static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false,
    };
    static readonly EnumerationOptions Flat = new()
    {
        RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false,
    };

    /// <summary>C: one FileSystemEnumerable pass; name first, stat-backed facets only for name matches.</summary>
    public static IEnumerable<string> Find(FindRequest r)
    {
        var (expr, method, _) = CommanderPattern.BuildQuery(r);
        var name = NamePredicate.Compile(expr, method == WordMatchMethod.Regex);
        var facets = new EntryFacets(r);
        return new FileSystemEnumerable<string>(Path.GetFullPath(r.Root), static (ref FileSystemEntry e) => e.ToFullPath(), Recursive)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && name.IsMatch(e.FileName) && facets.Matches(ref e),
        };
    }

    /// <summary>D: directories processed by <paramref name="threads"/> workers; hits streamed through a channel.</summary>
    public static async IAsyncEnumerable<string> FindParallel(FindRequest r, int threads)
    {
        var (expr, method, _) = CommanderPattern.BuildQuery(r);
        var name = NamePredicate.Compile(expr, method == WordMatchMethod.Regex);
        var facets = new EntryFacets(r);
        var hits = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var pending = new BlockingCollection<string>(new ConcurrentStack<string>()); // LIFO: depth-first like Commander
        int outstanding = 1;
        pending.Add(Path.GetFullPath(r.Root));

        var workers = new Task[threads];
        for (int t = 0; t < threads; t++)
        {
            workers[t] = Task.Factory.StartNew(() =>
            {
                foreach (var dir in pending.GetConsumingEnumerable())
                {
                    try
                    {
                        var en = new FileSystemEnumerable<string?>(dir, (ref FileSystemEntry e) =>
                        {
                            if (e.IsDirectory)
                            {
                                if ((e.Attributes & FileAttributes.ReparsePoint) == 0) { Interlocked.Increment(ref outstanding); pending.Add(e.ToFullPath()); }
                                return null;
                            }
                            return name.IsMatch(e.FileName) && facets.Matches(ref e) ? e.ToFullPath() : null;
                        }, Flat);
                        foreach (var hit in en)
                            if (hit is not null) hits.Writer.TryWrite(hit);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    if (Interlocked.Decrement(ref outstanding) == 0) pending.CompleteAdding();
                }
            }, TaskCreationOptions.LongRunning);
        }
        _ = Task.WhenAll(workers).ContinueWith(_ => hits.Writer.TryComplete());

        await foreach (var h in hits.Reader.ReadAllAsync())
            yield return h;
    }
}
