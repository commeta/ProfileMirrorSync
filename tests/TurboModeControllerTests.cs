using ProfileMirrorSync.Models;
using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Unit tests for TurboModeController.  Verify the
/// activate/deactivate/reset transitions and the rate-limiter effects.  The
/// pending-count is supplied by an injectable delegate so we drive the
/// drain-detection deterministically without a real queue.
/// </summary>
public class TurboModeControllerTests : IDisposable
{
    private readonly string _logDir;
    private readonly Logger _log;

    public TurboModeControllerTests()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "PMSTurbo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_logDir);
        _log = new Logger(_logDir, AppLogLevel.Warning);
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_logDir, recursive: true); } catch { }
    }

    private static AppSettings Settings(bool enabled, int threshold, int baseMbps, int turboMbps) =>
        new()
        {
            MaxBandwidthBitsPerSecond  = baseMbps * 1_000_000,
            TurboFirstRunEnabled       = enabled,
            TurboThresholdFiles        = threshold,
            TurboFirstRunBandwidthMbps = turboMbps,
        };

    [Fact]
    public void DoesNotActivate_WhenDisabled()
    {
        var s   = Settings(enabled: false, threshold: 100, baseMbps: 1, turboMbps: 3);
        var rl  = new ByteRateLimiter(s.MaxBandwidthBitsPerSecond);
        int pending = 0;
        var turbo = new TurboModeController(s, _log, rl, () => pending);

        turbo.MaybeActivate(10_000);   // way over threshold, but disabled

        Assert.False(turbo.IsActive);
        Assert.Equal(1_000_000, rl.CurrentBitsPerSecond);
    }

    [Fact]
    public void DoesNotActivate_BelowThreshold()
    {
        var s   = Settings(enabled: true, threshold: 1000, baseMbps: 1, turboMbps: 3);
        var rl  = new ByteRateLimiter(s.MaxBandwidthBitsPerSecond);
        var turbo = new TurboModeController(s, _log, rl, () => 0);

        turbo.MaybeActivate(999);      // one below threshold

        Assert.False(turbo.IsActive);
        Assert.Equal(1_000_000, rl.CurrentBitsPerSecond);
    }

    [Fact]
    public void Activates_AtThreshold_RaisesRate()
    {
        var s   = Settings(enabled: true, threshold: 1000, baseMbps: 1, turboMbps: 3);
        var rl  = new ByteRateLimiter(s.MaxBandwidthBitsPerSecond);
        var turbo = new TurboModeController(s, _log, rl, () => 0);

        turbo.MaybeActivate(1000);     // exactly at threshold

        Assert.True(turbo.IsActive);
        Assert.Equal(3_000_000, rl.CurrentBitsPerSecond);
    }

    [Fact]
    public void Deactivate_RestoresBaseline_WhenQueueDrained()
    {
        var s   = Settings(enabled: true, threshold: 1000, baseMbps: 1, turboMbps: 3);
        var rl  = new ByteRateLimiter(s.MaxBandwidthBitsPerSecond);
        int pending = 1000;
        var turbo = new TurboModeController(s, _log, rl, () => pending);

        turbo.MaybeActivate(1000);
        Assert.True(turbo.IsActive);

        pending = 0;                   // queue drained
        turbo.MaybeDeactivate();

        Assert.False(turbo.IsActive);
        Assert.Equal(1_000_000, rl.CurrentBitsPerSecond);
    }

    [Fact]
    public void Deactivate_NoOp_WhenQueueStillBusy()
    {
        var s   = Settings(enabled: true, threshold: 1000, baseMbps: 1, turboMbps: 3);
        var rl  = new ByteRateLimiter(s.MaxBandwidthBitsPerSecond);
        int pending = 1000;
        var turbo = new TurboModeController(s, _log, rl, () => pending);

        turbo.MaybeActivate(1000);
        turbo.MaybeDeactivate();       // queue NOT drained → stay in turbo

        Assert.True(turbo.IsActive);
        Assert.Equal(3_000_000, rl.CurrentBitsPerSecond);
    }

    [Fact]
    public void Reset_ForcesBaseline_Idempotent()
    {
        var s   = Settings(enabled: true, threshold: 1000, baseMbps: 1, turboMbps: 3);
        var rl  = new ByteRateLimiter(s.MaxBandwidthBitsPerSecond);
        var turbo = new TurboModeController(s, _log, rl, () => 5);

        turbo.MaybeActivate(1000);
        Assert.True(turbo.IsActive);

        turbo.Reset();
        Assert.False(turbo.IsActive);
        Assert.Equal(1_000_000, rl.CurrentBitsPerSecond);

        // Second reset is a harmless no-op.
        turbo.Reset();
        Assert.False(turbo.IsActive);
    }

    [Fact]
    public void DefaultSettings_AreTunedForTenPcThirtyMbit()
    {
        // Guards the requirement: base 1 Mbit, turbo 3 Mbit, threshold
        // 1000 files, turbo-on-reconcile off.
        var s = new AppSettings();
        Assert.Equal(1_000_000, s.MaxBandwidthBitsPerSecond);
        Assert.Equal(3,    s.TurboFirstRunBandwidthMbps);
        Assert.Equal(1000, s.TurboThresholdFiles);
        Assert.False(s.TurboOnReconcile);
    }
}
