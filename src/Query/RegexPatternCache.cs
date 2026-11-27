using System.Text.RegularExpressions;

namespace SearchEngine.Query;

/// <summary>Small LRU cache of compiled regexes (non-backtracking when supported). A pattern is compiled with the
/// casing of indexed tokens (<see cref="RegexCaseFolding"/>) and matched case-sensitively: the tokens are lowercase, or
/// case folded, already.</summary>
internal static class RegexPatternCache
{
    private const int Capacity = 8;
    private const RegexOptions BaseOptions = RegexOptions.None;
    private const RegexOptions NonBacktrackingOptions = BaseOptions | RegexOptions.NonBacktracking;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private static readonly Lock Gate = new();
    private static readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> Entries = new();
    private static readonly LinkedList<CacheEntry> Lru = new();

    /// <param name="pattern">The pattern as the caller wrote it.</param>
    /// <param name="caseFold">Whether the index applies <see cref="UnicodeMatching.CaseFold"/>.</param>
    /// <param name="regex">The compiled regex; null when the pattern is invalid.</param>
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
