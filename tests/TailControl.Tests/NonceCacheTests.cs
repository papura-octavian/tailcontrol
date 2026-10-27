using TailControl.Shared.Security;

namespace TailControl.Tests;

public sealed class NonceCacheTests
{
    private const string Nonce = "AAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void TryAdd_SameNonceSameClient_IsRejected()
    {
        var clock = TestSupport.NewClock();
        var cache = new MemoryNonceCache(clock);

        Assert.True(cache.TryAdd("client-a", Nonce, clock.GetUtcNow().AddSeconds(31)));
        Assert.False(cache.TryAdd("client-a", Nonce, clock.GetUtcNow().AddSeconds(31)));
    }

    [Fact]
    public void TryAdd_SameNonceOtherClient_IsAccepted()
    {
        var clock = TestSupport.NewClock();
        var cache = new MemoryNonceCache(clock);

        Assert.True(cache.TryAdd("client-a", Nonce, clock.GetUtcNow().AddSeconds(31)));
        Assert.True(cache.TryAdd("client-b", Nonce, clock.GetUtcNow().AddSeconds(31)));
    }

    [Fact]
    public void ExpiredEntries_AreRemoved()
    {
        var clock = TestSupport.NewClock();
        var cache = new MemoryNonceCache(clock);
        for (var i = 0; i < 100; i++)
        {
            cache.TryAdd("client-a", $"nonce-{i}", clock.GetUtcNow().AddSeconds(31));
        }

        clock.Advance(TimeSpan.FromMinutes(2));
        cache.TryAdd("client-a", "nou", clock.GetUtcNow().AddSeconds(31));

        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void FullCache_FailsClosed()
    {
        var clock = TestSupport.NewClock();
        var cache = new MemoryNonceCache(clock, capacity: 2);

        Assert.True(cache.TryAdd("c", "1", clock.GetUtcNow().AddSeconds(31)));
        Assert.True(cache.TryAdd("c", "2", clock.GetUtcNow().AddSeconds(31)));
        Assert.False(cache.TryAdd("c", "3", clock.GetUtcNow().AddSeconds(31)));
    }
}
