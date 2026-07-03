using System.Text.RegularExpressions;

namespace SearchEngine.Query;

/// <summary>Small LRU cache of case-insensitive regexes (non-backtracking when supported).</summary>
internal static class RegexPatternCache
{
    private const int Capacity = 8;
    private const RegexOptions BaseOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const RegexOptions NonBacktrackingOptions =
        BaseOptions | RegexOptions.NonBacktracking;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Entries = new(StringComparer.Ordinal);
    private static readonly LinkedList<CacheEntry> Lru = new();

    internal static bool TryGet(string pattern, out Regex regex)
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(pattern, out var node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                regex = node.Value.Regex;
                return true;
            }
        }

        if (!TryCompile(pattern, out var compiled))
        {
            regex = null!;
            return false;
        }

        lock (Gate)
        {
            if (Entries.TryGetValue(pattern, out var existing))
            {
                regex = existing.Value.Regex;
                return true;
            }

            var entry = new CacheEntry(pattern, compiled);
            var newNode = Lru.AddFirst(entry);
            Entries[pattern] = newNode;

            while (Entries.Count > Capacity)
            {
                var last = Lru.Last!;
                Lru.RemoveLast();
                Entries.Remove(last.Value.Pattern);
            }

            regex = compiled;
            return true;
        }
    }

    private static bool TryCompile(string pattern, out Regex regex)
    {
        try
        {
            regex = new Regex(pattern, NonBacktrackingOptions, MatchTimeout);
            return true;
        }
        catch (NotSupportedException)
        {
            try
            {
                regex = new Regex(pattern, BaseOptions, MatchTimeout);
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

    private readonly record struct CacheEntry(string Pattern, Regex Regex);
}
