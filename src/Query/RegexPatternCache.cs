using System.Text.RegularExpressions;

namespace SearchEngine.Query;

// Small LRU cache of regexes rewritten by RegexCaseFolding. A pattern runs on NonBacktracking, or on the backtracking
// engine when NonBacktracking does not support it; an invalid pattern gives no regex. The caller treats an invalid
// pattern and a match timeout alike: an empty result.
internal static class RegexPatternCache
{
    private const int Capacity = 8;
    private const RegexOptions BaseOptions = RegexOptions.None;
    private const RegexOptions NonBacktrackingOptions = BaseOptions | RegexOptions.NonBacktracking;

    // Stated to users in docs/query-semantics.md, Regular expressions.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private static readonly Lock Gate = new();
    private static readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> Entries = new();
    private static readonly LinkedList<CacheEntry> Lru = new();

    // pattern: as the caller wrote it. caseFold: whether the index applies UnicodeMatching.CaseFold. regex: null when
    // the pattern is invalid (false).
    internal static bool TryGet(string pattern, bool caseFold, out Regex regex)
    {
        var key = new CacheKey(pattern, caseFold);
        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                regex = node.Value.Regex;
                return true;
            }
        }

        if (!TryCompile(RegexCaseFolding.Apply(pattern, caseFold), out var compiled))
        {
            regex = null!;
            return false;
        }

        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var existing))
            {
                regex = existing.Value.Regex;
                return true;
            }

            var entry = new CacheEntry(key, compiled);
            var newNode = Lru.AddFirst(entry);
            Entries[key] = newNode;

            while (Entries.Count > Capacity)
            {
                var last = Lru.Last!;
                Lru.RemoveLast();
                Entries.Remove(last.Value.Key);
            }

            regex = compiled;
            return true;
        }
    }

    private static bool TryCompile(string pattern, out Regex regex)
    {
        try
        {
            regex = new(pattern, NonBacktrackingOptions, MatchTimeout);
            return true;
        }
        catch (NotSupportedException)
        {
            try
            {
                regex = new(pattern, BaseOptions, MatchTimeout);
                return true;
            }
            catch (Exception ex) when (ex is RegexParseException or ArgumentException)
            {
                regex = null!;
                return false;
            }
        }
        catch (Exception ex) when (ex is RegexParseException or ArgumentException)
        {
            regex = null!;
            return false;
        }
    }

    private readonly record struct CacheKey(string Pattern, bool CaseFold);

    private readonly record struct CacheEntry(CacheKey Key, Regex Regex);
}
