using ProfileMirrorSync.Models;

namespace ProfileMirrorSync.Services;

/// <summary>
/// Reconcile-schedule decision logic, extracted from SyncController.
///
/// This class owns the *when* of scheduled reconciliation:
///   • the jittered next-due time computed from an anchor + base interval,
///   • the "due by time" / "due by early queue-pressure" decision,
///   • the minimum-gap guard for early triggers.
///
/// It deliberately holds NO I/O and NO mutable cross-thread state beyond the
/// per-instance jitter RNG and the current jitter offset (mutated only from the
/// single reconcile-loop thread).  PersistentState is passed in by the caller,
/// so the decision functions are pure and unit-testable.
/// </summary>
public sealed class ReconcileScheduler
{
    private readonly AppSettings _settings;
    private readonly Random      _rng;

    public int BaseIntervalMinutes { get; }
    public int JitterPercent       { get; }
    private int _currentJitterOffsetSec;

    public ReconcileScheduler(AppSettings settings)
        : this(settings, new Random(Environment.MachineName.GetHashCode())) { }

    /// <summary>Test seam — inject a deterministic RNG.</summary>
    public ReconcileScheduler(AppSettings settings, Random rng)
    {
        _settings = settings;
        _rng      = rng;
        BaseIntervalMinutes = Math.Max(5, settings.ReconcileIntervalMinutes);
        JitterPercent       = Math.Clamp(settings.ReconcileJitterPercent, 0, 100);
        _currentJitterOffsetSec = ComputeJitterOffsetSec(_rng, BaseIntervalMinutes, JitterPercent);
    }

    /// <summary>The next scheduled reconcile time given the current anchor.</summary>
    public DateTime NextDue(DateTime anchor) =>
        anchor + TimeSpan.FromMinutes(BaseIntervalMinutes)
               + TimeSpan.FromSeconds(_currentJitterOffsetSec);

    /// <summary>Roll a fresh jitter offset for the next interval.</summary>
    public int AdvanceJitter()
    {
        _currentJitterOffsetSec = ComputeJitterOffsetSec(_rng, BaseIntervalMinutes, JitterPercent);
        return _currentJitterOffsetSec;
    }

    public int CurrentJitterOffsetSec => _currentJitterOffsetSec;

    /// <summary>
    /// Decide whether a reconcile is due right now.  Pure function of the
    /// supplied state + clock — no side effects.
    /// </summary>
    public ReconcileDecision Evaluate(PersistentState state, DateTime now)
    {
        DateTime anchor = state.LastReconcileUtc ?? now;
        bool dueByTime  = now >= NextDue(anchor);
        bool dueByEarly = state.EarlyReconcileRequested && IsEarlyTriggerAllowed(state, now);
        return new ReconcileDecision(dueByTime, dueByEarly, anchor);
    }

    public bool IsEarlyTriggerAllowed(PersistentState state, DateTime now)
    {
        int minGap = Math.Max(1, _settings.EarlyReconcileMinGapMinutes);
        if (state.LastReconcileUtc is null) return true;
        return (now - state.LastReconcileUtc.Value) >= TimeSpan.FromMinutes(minGap);
    }

    internal static int ComputeJitterOffsetSec(Random rng, int baseMin, int jitterPct)
    {
        int jitterRangeSec = (int)(baseMin * 60.0 * jitterPct / 100.0);
        if (jitterRangeSec <= 0) return 0;
        return rng.Next(-jitterRangeSec / 2, jitterRangeSec / 2 + 1);
    }
}

/// <summary>Outcome of <see cref="ReconcileScheduler.Evaluate"/>.</summary>
public readonly record struct ReconcileDecision(bool DueByTime, bool DueByEarly, DateTime Anchor)
{
    public bool IsDue => DueByTime || DueByEarly;
}
