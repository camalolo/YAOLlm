using System;
using System.Collections.Generic;

namespace YAOLlm;

/// <summary>
/// In-memory cache for web_scrape results. A gaming session re-scrapes the
/// same guide pages across turns — without the cache every request re-hits
/// the proxy scrape API (slow, billable). Entries live for 24h; only
/// successful results are ever stored, so "Error:" results (dead pages,
/// bot-blocks, proxy failures) are retried live on the next call.
/// Static by design: preset/provider switches recreate the scrape services,
/// and the cache should outlive them. Process-lifetime only — it does not
/// survive an app restart.
/// </summary>
internal static class ScrapeCache
{
    internal const int TtlHours = 24;
    internal const int MaxEntries = 64;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (string result, DateTime cachedAt)> Entries = new();

    // Injectable so tests can travel through time.
    internal static Func<DateTime> Clock = () => DateTime.UtcNow;

    /// <summary>
    /// Normalizes a URL into a cache key: scheme and host are case-folded,
    /// fragments are dropped (same page), path and query stay case-sensitive.
    /// Unparseable URLs fall back to the trimmed raw string — those never
    /// produce cacheable results anyway.
    /// </summary>
    public static string NormalizeKey(string url)
    {
        var trimmed = url.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
            return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{port}{uri.PathAndQuery}";
        }
        return trimmed;
    }

    public static bool TryGet(string key, out string result)
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var entry))
            {
                if (Clock() - entry.cachedAt < TimeSpan.FromHours(TtlHours))
                {
                    result = entry.result;
                    return true;
                }
                Entries.Remove(key); // expired
            }
            result = "";
            return false;
        }
    }

    public static void Store(string key, string result)
    {
        lock (Gate)
        {
            if (Entries.Count >= MaxEntries && !Entries.ContainsKey(key))
            {
                // Bounded memory: each entry can be 100+ KB of page text.
                string oldestKey = key;
                var oldest = DateTime.MaxValue;
                foreach (var (k, e) in Entries)
                {
                    if (e.cachedAt < oldest)
                    {
                        oldest = e.cachedAt;
                        oldestKey = k;
                    }
                }
                Entries.Remove(oldestKey);
            }
            Entries[key] = (result, Clock());
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
        }
    }
}
