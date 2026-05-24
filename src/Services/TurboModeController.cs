using ProfileMirrorSync.Models;

namespace ProfileMirrorSync.Services;

/// <summary>
/// Burst-mode bandwidth accelerator, extracted from SyncController.
///
/// When the amount of pending work crosses
/// <see cref="AppSettings.TurboThresholdFiles"/>, the rate limiter is raised to
/// <see cref="AppSettings.TurboFirstRunBandwidthMbps"/>; when the queue drains
/// it is restored to the baseline.  Both transitions are serialised by a small
/// lock so concurrent callers (the real-time FSW enqueue path and the reconcile
/// per-file callback) can't double-log or double-update the limiter.  The
/// "currently active" flag is read lock-free via Volatile on the fast path.
///
/// Behaviour is identical to the pre-refactor inline logic; only the ownership
/// moved out of SyncController.
/// </summary>
public sealed class TurboModeController
{
    private readonly AppSettings    _settings;
    private readonly Logger         _log;
    private readonly ByteRateLimiter _rateLimiter;
    private readonly Func<int>      _pendingCount;  // returns current queue depth

    private readonly object _lock = new();
    private bool _active;

    public TurboModeController(AppSettings settings, Logger log,
        ByteRateLimiter rateLimiter, Func<int> pendingCount)
    {
        _settings     = settings;
        _log          = log;
        _rateLimiter  = rateLimiter;
        _pendingCount = pendingCount;
    }

    public bool IsActive => System.Threading.Volatile.Read(ref _active);

    /// <summary>
    /// Activate turbo if (a) enabled, (b) not already active, (c) pending work
    /// crosses the configured threshold.  Callable from any thread.
    /// </summary>
    public void MaybeActivate(int pending)
    {
        if (!_settings.TurboFirstRunEnabled) return;
        if (System.Threading.Volatile.Read(ref _active)) return;
        if (pending < _settings.TurboThresholdFiles) return;

        lock (_lock)
        {
            if (_active) return;
            _active = true;
            long turboBps = (long)_settings.TurboFirstRunBandwidthMbps * 1_000_000;
            _rateLimiter.UpdateRate(turboBps);
            _log.Info($"Turbo: очередь={pending} файлов → поднимаю лимит до {_settings.TurboFirstRunBandwidthMbps} Мбит/с");
        }
    }

    /// <summary>
    /// Restore baseline bandwidth if turbo is active and the queue has drained.
    /// Re-checks pending count both lock-free and under the lock.
    /// </summary>
    public void MaybeDeactivate()
    {
        if (!System.Threading.Volatile.Read(ref _active)) return;
        if (_pendingCount() > 0) return;

        lock (_lock)
        {
            if (!_active) return;
            if (_pendingCount() > 0) return;
            _active = false;
            _rateLimiter.UpdateRate(_settings.MaxBandwidthBitsPerSecond);
            _log.Info($"Turbo завершён → возврат к {_settings.MaxBandwidthBitsPerSecond / 1_000_000.0:F1} Мбит/с");
        }
    }

    /// <summary>
    /// Force turbo off and restore baseline.  Idempotent.  Used on worker
    /// (re)entry to clear any residual turbo state from a crashed cycle.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            if (!_active) return;
            _active = false;
            _rateLimiter.UpdateRate(_settings.MaxBandwidthBitsPerSecond);
            _log.Info($"Сброс turbo-режима (возврат к {_settings.MaxBandwidthBitsPerSecond / 1_000_000.0:F1} Мбит/с).");
        }
    }
}
