using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Basic invariants for the token-bucket rate limiter.
/// These don't try to measure exact timing (flaky on shared CI); they pin
/// behaviour observable without wall-clock dependency.
/// </summary>
public class ByteRateLimiterTests
{
    [Fact]
    public async Task UnlimitedRate_ReturnsImmediately()
    {
        var limiter = new ByteRateLimiter(bitsPerSecond: 0);
        // 0 = unlimited.  WaitAsync with any byte count should return
        // immediately even for huge amounts.
        await limiter.WaitAsync(int.MaxValue, CancellationToken.None);
    }

    [Fact]
    public async Task NegativeRate_TreatedAsUnlimited()
    {
        var limiter = new ByteRateLimiter(bitsPerSecond: -1);
        // Negative interpretation: defensive, should not block.
        await limiter.WaitAsync(1_000_000, CancellationToken.None);
    }

    [Fact]
    public async Task SmallRequest_WithinInitialBudget_ReturnsImmediately()
    {
        // 1 Mbit/s = 125_000 bytes/s; initial tokens = 125_000.
        // Asking for 1000 bytes should consume from initial budget without delay.
        var limiter = new ByteRateLimiter(bitsPerSecond: 1_000_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await limiter.WaitAsync(1000, CancellationToken.None);
        sw.Stop();
        // Generous bound — we just want to verify it didn't block for seconds.
        Assert.True(sw.ElapsedMilliseconds < 200,
            $"Small request within budget took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Cancellation_PropagatesQuickly()
    {
        var limiter = new ByteRateLimiter(bitsPerSecond: 8); // 1 byte/s — very slow
        using var cts = new CancellationTokenSource();
        // Ask for far more than the burst budget so the limiter has to wait.
        var task = limiter.WaitAsync(10_000, cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task UpdateRate_AppliesToSubsequentWaits()
    {
        // Start unlimited
        var limiter = new ByteRateLimiter(bitsPerSecond: 0);
        await limiter.WaitAsync(1_000_000, CancellationToken.None); // immediate

        // Switch to slow rate — token bucket resets
        limiter.UpdateRate(bitsPerSecond: 8); // 1 byte/s
        using var cts = new CancellationTokenSource();
        var task = limiter.WaitAsync(10_000, cts.Token);
        await Task.Delay(50);
        // Should still be waiting (10000 bytes at 1 B/s = unfeasible)
        Assert.False(task.IsCompleted,
            "After UpdateRate to 1 B/s, large request must not complete instantly.");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    // ── — CurrentBitsPerSecond exposed for the stats window ──────────

    [Fact]
    public void CurrentBitsPerSecond_ReturnsZeroForUnlimited()
    {
        var limiter = new ByteRateLimiter(bitsPerSecond: 0);
        Assert.Equal(0, limiter.CurrentBitsPerSecond);
    }

    [Fact]
    public void CurrentBitsPerSecond_ReturnsConfiguredRate()
    {
        var limiter = new ByteRateLimiter(bitsPerSecond: 1_000_000);
        // Rate is stored as bytes/sec internally; the property re-multiplies by 8.
        // We tolerate floor-rounding (1_000_000 / 8 = 125_000 bytes; back = 1_000_000).
        Assert.Equal(1_000_000, limiter.CurrentBitsPerSecond);
    }

    [Fact]
    public void CurrentBitsPerSecond_TracksRuntimeUpdates()
    {
        var limiter = new ByteRateLimiter(bitsPerSecond: 1_000_000);
        Assert.Equal(1_000_000, limiter.CurrentBitsPerSecond);

        limiter.UpdateRate(bitsPerSecond: 10_000_000); // turbo mode
        Assert.Equal(10_000_000, limiter.CurrentBitsPerSecond);

        limiter.UpdateRate(bitsPerSecond: 0); // back to unlimited
        Assert.Equal(0, limiter.CurrentBitsPerSecond);
    }
}
