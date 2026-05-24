using ProfileMirrorSync.Models;
using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Decision-logic tests for ReconcileScheduler (extracted from
/// SyncController, audit L-1).  All branches are pure functions of the supplied
/// PersistentState + clock, so they unit-test without any I/O or a controller.
/// A fixed-seed RNG makes the jitter deterministic.
/// </summary>
public class ReconcileSchedulerTests
{
    private static ReconcileScheduler Make(int intervalMin, int jitterPct, int gapMin = 60)
    {
        var settings = new AppSettings
        {
            ReconcileIntervalMinutes    = intervalMin,
            ReconcileJitterPercent      = jitterPct,
            EarlyReconcileMinGapMinutes = gapMin,
        };
        return new ReconcileScheduler(settings, new Random(12345)); // fixed seed
    }

    [Fact]
    public void NotDue_BeforeInterval()
    {
        var sched = Make(1440, 0);             // 24h, no jitter
        var now   = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);
        var state = new PersistentState { LastReconcileUtc = now.AddHours(-1) }; // 1h ago
        var d = sched.Evaluate(state, now);
        Assert.False(d.IsDue);
    }

    [Fact]
    public void DueByTime_AfterInterval()
    {
        var sched = Make(60, 0);               // 1h, no jitter
        var now   = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);
        var state = new PersistentState { LastReconcileUtc = now.AddHours(-2) }; // 2h ago
        var d = sched.Evaluate(state, now);
        Assert.True(d.DueByTime);
        Assert.True(d.IsDue);
    }

    [Fact]
    public void DueByEarly_WhenRequested_AndGapElapsed()
    {
        var sched = Make(1440, 0, gapMin: 30);
        var now   = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);
        var state = new PersistentState
        {
            LastReconcileUtc        = now.AddMinutes(-31), // gap elapsed
            EarlyReconcileRequested = true,
        };
        var d = sched.Evaluate(state, now);
        Assert.True(d.DueByEarly);
        Assert.True(d.IsDue);
    }

    [Fact]
    public void NotEarly_WhenGapNotElapsed()
    {
        var sched = Make(1440, 0, gapMin: 60);
        var now   = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);
        var state = new PersistentState
        {
            LastReconcileUtc        = now.AddMinutes(-10), // only 10 min ago
            EarlyReconcileRequested = true,
        };
        var d = sched.Evaluate(state, now);
        Assert.False(d.DueByEarly);
        Assert.False(d.IsDue);
    }

    [Fact]
    public void Early_AllowedImmediately_WhenNoPriorReconcile()
    {
        var sched = Make(1440, 0, gapMin: 60);
        var now   = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);
        var state = new PersistentState { EarlyReconcileRequested = true }; // LastReconcileUtc null
        Assert.True(sched.IsEarlyTriggerAllowed(state, now));
    }

    [Fact]
    public void Jitter_StaysWithinHalfRange()
    {
        // jitter range = interval*jitterPct%; offset ∈ [-range/2, +range/2].
        int interval = 1440, pct = 30;
        var sched = Make(interval, pct);
        int rangeSec = (int)(interval * 60.0 * pct / 100.0);
        for (int i = 0; i < 200; i++)
        {
            int off = sched.AdvanceJitter();
            Assert.InRange(off, -rangeSec / 2, rangeSec / 2);
        }
    }

    [Fact]
    public void Interval_FlooredAtFiveMinutes()
    {
        var sched = Make(1, 0);  // below floor
        Assert.Equal(5, sched.BaseIntervalMinutes);
    }

    [Fact]
    public void ZeroJitter_ProducesZeroOffset()
    {
        var sched = Make(1440, 0);
        Assert.Equal(0, sched.CurrentJitterOffsetSec);
        Assert.Equal(0, sched.AdvanceJitter());
    }
}
