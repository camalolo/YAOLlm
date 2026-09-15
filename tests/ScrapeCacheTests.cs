using System;
using Xunit;

namespace YAOLlm.Tests;

public class ScrapeCacheTests : IDisposable
{
    private readonly DateTime _now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    public ScrapeCacheTests()
    {
        ScrapeCache.Clear();
        ScrapeCache.Clock = () => _now;
    }

    public void Dispose()
    {
        ScrapeCache.Clear();
        ScrapeCache.Clock = () => DateTime.UtcNow;
    }

    [Fact]
    public void StoreThenTryGet_ReturnsSameResult()
    {
        ScrapeCache.Store("https://x.com/page", "page content");

        Assert.True(ScrapeCache.TryGet("https://x.com/page", out var result));
        Assert.Equal("page content", result);
    }

    [Fact]
    public void Miss_ReturnsFalse()
    {
        Assert.False(ScrapeCache.TryGet("https://x.com/never-stored", out var result));
        Assert.Equal("", result);
    }

    [Fact]
    public void Entry_LivesUntilTtl()
    {
        ScrapeCache.Store("k", "v");

        ScrapeCache.Clock = () => _now.AddHours(24).AddSeconds(-1);
        Assert.True(ScrapeCache.TryGet("k", out _));

        ScrapeCache.Clock = () => _now.AddHours(24);
        Assert.False(ScrapeCache.TryGet("k", out _));
    }

    [Fact]
    public void ExpiredEntry_IsPurged()
    {
        ScrapeCache.Store("k", "v");

        ScrapeCache.Clock = () => _now.AddHours(30);
        Assert.False(ScrapeCache.TryGet("k", out _));

        // Purged, not just stale — a fresh store must not resurrect the old value.
        ScrapeCache.Clock = () => _now.AddHours(31);
        ScrapeCache.Store("k", "fresh");
        Assert.True(ScrapeCache.TryGet("k", out var result));
        Assert.Equal("fresh", result);
    }

    [Fact]
    public void Store_SameKey_RefreshesTtl()
    {
        ScrapeCache.Store("k", "old");

        ScrapeCache.Clock = () => _now.AddHours(23);
        ScrapeCache.Store("k", "new");

        // 24h after the *refresh* the entry must still be alive.
        ScrapeCache.Clock = () => _now.AddHours(46);
        Assert.True(ScrapeCache.TryGet("k", out var result));
        Assert.Equal("new", result);
    }

    [Fact]
    public void NormalizeKey_FoldsSchemeHostCase_AndDropsFragment()
    {
        var a = ScrapeCache.NormalizeKey("HTTPS://WWW.IGN.com/wikis/Page#section");
        var b = ScrapeCache.NormalizeKey("https://www.ign.com/wikis/Page");

        Assert.Equal(a, b);
    }

    [Fact]
    public void NormalizeKey_PathStaysCaseSensitive()
    {
        Assert.NotEqual(
            ScrapeCache.NormalizeKey("https://x.com/wiki/Treasure_Map_A4"),
            ScrapeCache.NormalizeKey("https://x.com/wiki/treasure_map_a4"));
    }

    [Fact]
    public void NormalizeKey_QueryIsSignificant()
    {
        Assert.NotEqual(
            ScrapeCache.NormalizeKey("https://x.com/p?page=1"),
            ScrapeCache.NormalizeKey("https://x.com/p?page=2"));
    }

    [Fact]
    public void NormalizeKey_NonHttpScheme_KeepsRawString()
    {
        Assert.Equal("ftp://x.com/f", ScrapeCache.NormalizeKey("  ftp://x.com/f  "));
    }

    [Fact]
    public void Store_EvictsOldest_WhenFull()
    {
        for (int i = 0; i < ScrapeCache.MaxEntries; i++)
        {
            var time = _now.AddMinutes(i);
            ScrapeCache.Clock = () => time;
            ScrapeCache.Store($"k{i}", $"v{i}");
        }

        ScrapeCache.Clock = () => _now.AddHours(1);
        ScrapeCache.Store("new", "v-new"); // cache is full → evicts k0 (oldest)

        Assert.False(ScrapeCache.TryGet("k0", out _));
        Assert.True(ScrapeCache.TryGet("new", out _));
        Assert.True(ScrapeCache.TryGet("k1", out _));
    }

    [Fact]
    public void Store_ExistingKey_AtCapacity_DoesNotEvict()
    {
        for (int i = 0; i < ScrapeCache.MaxEntries; i++)
        {
            var time = _now.AddMinutes(i);
            ScrapeCache.Clock = () => time;
            ScrapeCache.Store($"k{i}", $"v{i}");
        }

        ScrapeCache.Clock = () => _now.AddHours(1);
        ScrapeCache.Store("k5", "v5-refreshed"); // replace, no growth → no eviction

        Assert.True(ScrapeCache.TryGet("k0", out _));
        Assert.True(ScrapeCache.TryGet("k5", out var v));
        Assert.Equal("v5-refreshed", v);
    }
}
