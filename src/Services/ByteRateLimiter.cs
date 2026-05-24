using System.Diagnostics;

namespace ProfileMirrorSync.Services;

/// <summary>
/// Token-bucket rate limiter. Thread-safe.
/// bitsPerSecond=0 means unlimited (no throttle).
/// Call UpdateRate() to change bandwidth at runtime (turbo mode).
/// </summary>
public sealed class ByteRateLimiter
{
    private long _bytesPerSecond;
    private long _tokens;
    private long _lastTick;
    private readonly object _lock = new();

    // Constructor now accepts `long bitsPerSecond` to
    // match the signature of <see cref="UpdateRate"/> and <see cref="SetRate"/>.
    // Existing callers pass `int` (AppSettings.MaxBandwidthBitsPerSecond),
    // which converts implicitly — no source change required at call sites.
    public ByteRateLimiter(long bitsPerSecond) => SetRate(bitsPerSecond);

    /// <summary>
    /// Current effective bandwidth in bits/sec for the stats window.
    /// Returns 0 if the limiter is in unlimited mode (the sentinel value
    /// `long.MaxValue / 2` set by <see cref="SetRate"/> for bps &lt;= 0).
    /// Volatile read — same memory-model guarantees as the fast path in
    /// <see cref="WaitAsync"/>.
    /// </summary>
    public long CurrentBitsPerSecond
    {
        get
        {
            long bps = System.Threading.Volatile.Read(ref _bytesPerSecond);
            return bps >= long.MaxValue / 4 ? 0 : bps * 8;
        }
    }

    /// <summary>Update bandwidth at runtime. bitsPerSecond=0 = unlimited.</summary>
    public void UpdateRate(long bitsPerSecond)
    {
        lock (_lock) { SetRate(bitsPerSecond); }
    }

    /// <summary>
    /// Maximum number of bytes the limiter can hand out in a single
    /// <see cref="WaitAsync"/> call without livelocking.  This equals the
    /// configured 2-second burst cap (see <see cref="Refill"/>).  Callers
    /// that issue chunk-sized requests should clamp their chunk size to
    /// this value, otherwise a request larger than the burst cap can never
    /// be satisfied and <see cref="WaitAsync"/> spins forever.
    ///
    /// Returns <see cref="int.MaxValue"/> in unlimited mode (the sentinel
    /// large value set by <see cref="SetRate"/> for bps &lt;= 0); callers
    /// can use it as "no clamp needed".
    /// </summary>
    public int MaxBurstBytes
    {
        get
        {
            long bps = System.Threading.Volatile.Read(ref _bytesPerSecond);
            if (bps >= long.MaxValue / 4) return int.MaxValue;
            long burst = bps * 2;
            return burst > int.MaxValue ? int.MaxValue : (int)burst;
        }
    }

    private void SetRate(long bitsPerSecond)
    {
        _bytesPerSecond = bitsPerSecond <= 0 ? long.MaxValue / 2 : Math.Max(1, bitsPerSecond / 8);
        _tokens   = _bytesPerSecond;
        _lastTick = Stopwatch.GetTimestamp();
    }

    public async Task WaitAsync(int bytes, CancellationToken ct)
    {
        // Fast path: unlimited mode.
        // Volatile.Read so the lock-free read sees writes from
        // UpdateRate() called concurrently. On x64 the long
        // read was effectively atomic already, but the C# memory model
        // doesn't guarantee it on 32-bit.
        if (System.Threading.Volatile.Read(ref _bytesPerSecond) >= long.MaxValue / 4) return;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long waitMs;
            lock (_lock)
            {
                Refill();
                if (_tokens >= bytes) { _tokens -= bytes; return; }
                long deficit = bytes - _tokens;
                waitMs = Math.Clamp((long)Math.Ceiling(deficit * 1000.0 / _bytesPerSecond), 1, 200);
            }
            await Task.Delay((int)waitMs, ct).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        long now   = Stopwatch.GetTimestamp();
        long added = (long)((now - _lastTick) * _bytesPerSecond / (double)Stopwatch.Frequency);
        if (added <= 0) return;
        _tokens   = Math.Min(_tokens + added, _bytesPerSecond * 2);
        _lastTick = now;
    }
}
