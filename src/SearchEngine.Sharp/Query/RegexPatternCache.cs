using System.Text.RegularExpressions;

namespace SearchEngine.Query;

/// <summary>Small LRU cache of anchored, case-insensitive, non-backtracking regexes.</summary>
internal static class RegexPatternCache
{
    private const int Capacity = 8;
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

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

        Regex compiled;
        try
        {
            compiled = new Regex($"^(?:{pattern})$", Options, MatchTimeout);
        }
        catch (Exception ex) when (ex is RegexParseException or ArgumentException or NotSupportedException)
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

    private readonly record struct CacheEntry(string Pattern, Regex Regex);
}
