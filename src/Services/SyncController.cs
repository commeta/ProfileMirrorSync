using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ProfileMirrorSync.Models;

namespace ProfileMirrorSync.Services;

public sealed class SyncController : IDisposable
{
    // Windows-only working-set trim, used after scheduled reconciles
    // to keep the idle memory footprint single-digit MB in Task Manager.
    // EmptyWorkingSet returns nonzero on success; we ignore the result since
    // it's strictly a hint and any failure is silently best-effort.
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern int EmptyWorkingSet(IntPtr hProcess);

    private sealed record WatchedRoot(SyncRootSpec Spec, string DestinationRoot, FileSystemWatcher Watcher);

    private readonly AppSettings _settings;
    private readonly Logger      _log;
    private readonly FileMirror  _mirror;
    private readonly ByteRateLimiter _rateLimiter;

    private readonly CancellationTokenSource _cts = new();

    // Bounded channel for back-pressure.  An unbounded channel can let
    // memory grow without limit if FSW events fire faster than we drain them
    // (e.g. unzipping a huge archive into a watched folder).  Capacity is
    // configurable in AppSettings; when full, we drop low-priority writes
    // (Created/Changed — reconcile will catch them) and briefly retry
    // high-priority ones (Delete/Rename) so we don't lose deletions.
    private readonly Channel<SyncOperation> _queue;
    private readonly int _queueCapacity;

    private readonly ConcurrentDictionary<string, byte>                    _dedupe    = new(StringComparer.OrdinalIgnoreCase);
    // Was `Dictionary<string, WatchedRoot>` which is NOT thread-safe.
    // ReconcileLoopAsync, TriggerResumeReconcile, and EnqueueAllReconcile all
    // enumerate `_roots.Values` from background threads; StopAsyncCore mutates
    // it via Clear().  Concurrent foreach+Clear would throw
    // InvalidOperationException, which the reconcile loop's outer catch
    // (OperationCanceledException only) would NOT swallow — killing the
    // background worker silently while leaving _state=Running (a silent
    // degradation).  ConcurrentDictionary.Values returns a snapshot, so
    // enumeration is safe alongside Clear().  All existing access patterns
    // (indexer write, foreach, Clear) compile unchanged.
    private readonly ConcurrentDictionary<string, WatchedRoot>             _roots     = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debouncers= new(StringComparer.OrdinalIgnoreCase);

    // Paths that must NEVER be synced — prevents self-sync loop
    private readonly HashSet<string> _hardExcludes;

    private RegistrySnapshotService? _registrySnapshot;
    private Task? _worker;
    private Task? _reconcileWorker;
    private Task? _postSyncWorker;   // independent archive-on-timer loop

    // Proper lifecycle state machine.
    //
    // Was: `private bool _started;` with `if (_started) return;` checks at the
    // top of StartAsync/StopAsync — racy because a plain bool gives no memory
    // barrier, and two callers can pass the check simultaneously.  Replaced
    // by an int + Interlocked for cross-thread visibility, plus a semaphore
    // that serializes the actual transitions (so concurrent Start/Stop calls
    // queue rather than interleave).
    //
    //   Stopped   = controller idle, safe to Start
    //   Starting  = StartAsync in flight; concurrent Start awaits, Stop awaits
    //   Running   = workers up, watchers wired, queue accepting
    //   Stopping  = StopAsync in flight; concurrent Start/Stop awaits
    private const int StateStopped  = 0;
    private const int StateStarting = 1;
    private const int StateRunning  = 2;
    private const int StateStopping = 3;
    private int _state = StateStopped;

    // Single-permit semaphore serialising StartAsync and StopAsync.  Async
    // (SemaphoreSlim is safe inside async methods, unlike `lock`).  Held for
    // the duration of the transition; released in finally so an exception in
    // the middle of Start/Stop still releases the lock.
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    // Idempotency guard for Dispose.  Concurrent
    // Dispose calls from SessionEnding-bypass and normal Stop paths now
    // safely no-op on the second call.
    private int _disposed = 0;

    // Turbo, log-mirror, reconcile-schedule and post-sync logic were
    // extracted into dedicated services.  SyncController now orchestrates them
    // rather than embedding their internals.
    private TurboModeController _turbo = null!;   // built in ctor
    private LogMirrorService    _logMirror = null!;
    private ReconcileScheduler  _scheduler = null!;

    // Dedup flag for "Сетевой путь недоступен" warning.  Without
    // this, a single network outage produced one Warn per watched folder
    // (many identical lines in the log).  Now we log once on
    // first detection and clear the flag when (a) reconciliation succeeds
    // OR (b) PowerModeChanged signals system resume (TriggerResumeReconcile).
    private int _destinationWarningLogged = 0;  // Volatile flag via Interlocked

    // Roots we've already warned the user about for the
    // "hard-excluded but explicitly added as custom" case.  Per-session
    // (no persistence) — first reconcile attempt logs the Warn, subsequent
    // attempts are silent so we don't spam the log.  See ShouldIgnore for the
    // exclude logic that triggers this.
    private readonly HashSet<string> _hardExcludedWatchedRootsWarned = new(StringComparer.OrdinalIgnoreCase);
    // dedicated lock object instead of locking the HashSet
    // itself (a collection has no synchronisation semantics; locking it is a
    // code smell even though it's technically correct in C#).
    private readonly object _hardExcludedWarnLock = new();

    // Counter for per-cycle reconcile completion tracking.
    // Set by EnqueueAllReconcile to the number of roots that were enqueued;
    // decremented by RunReconcileAsync after each root finishes.  When it
    // reaches zero we stamp LastReconcileCompletedUtc — the previous per-root
    // stamp could be set after the FIRST root and then mislead the
    // "skip-redundant-startup" gate into thinking a multi-root reconcile
    // succeeded when only one out of three had run before a crash.
    private int _rootsRemainingInCycle = 0;

    // Dedup flag for "Плановая реконсиляция пропущена: сеть
    // недоступна" warning in the reconcile loop's 60-second poll.  Without
    // this, a multi-hour outage produced one Warn per minute (= ~1500 lines
    // for a full workday).  Cleared on first successful reachability check.
    private int _reconcileLoopOutageWarned = 0;
    private int _postSyncOutageWarned = 0;   // one-warn-per-outage for the archive loop

    // Stats counters exposed to TrayApp/StatsForm.  Volatile reads
    // are sufficient — the stats window polls these at the configured
    // refresh interval and individual missed updates are acceptable for a
    // human-facing diagnostic display.
    private long _statsFilesCopiedTotal = 0;
    private long _statsFilesDeletedTotal = 0;
    private long _statsBytesCopiedTotal = 0;
    private long _statsErrorsTotal = 0;
    // DateTime? is 16 bytes (bool + DateTime, padded).  Lock-free
    // reads from the UI thread can tear on 32-bit AND on x64 in some JIT
    // configurations.  Storing as Int64 ticks lets
    // Interlocked.Read/Exchange guarantee atomic transfer of the value.
    // 0 ticks = "unset" — DateTime ticks are positive in any realistic
    // calendar year, so 0 is a safe sentinel.
    private long _statsLastReconcileStartTicks = 0;
    private long _statsLastReconcileEndTicks   = 0;

    // Volatile.Read so callers on other threads see a recent value.
    // Returns true ONLY in the Running state; Starting/Stopping are not "running".
    public bool IsRunning => System.Threading.Volatile.Read(ref _state) == StateRunning;
    public event Action<string>? FileProcessed;

    /// <summary>
    /// Lightweight read-only snapshot of runtime stats for the
    /// stats window.  All fields are computed without taking any lock and
    /// without performing I/O — safe to call from a UI timer.
    /// </summary>
    public sealed record StatsSnapshot(
        int        QueueDepth,
        int        QueueCapacity,
        int        HighWaterMark,
        long       FilesCopiedTotal,
        long       FilesDeletedTotal,
        long       BytesCopiedTotal,
        long       ErrorsTotal,
        long       CurrentBandwidthBitsPerSecond,
        bool       TurboModeActive,
        bool       Reconciling,
        DateTime?  LastReconcileStartUtc,
        DateTime?  LastReconcileEndUtc,
        DateTime?  NextScheduledReconcileUtc,
        DateTime?  LastRegistrySnapshotUtc,
        DateTime?  LastLogMirrorUtc,
        int        WatchedRoots,
        long       ProcessWorkingSetBytes,
        long       ProcessPrivateMemoryBytes);

    public StatsSnapshot GetStatsSnapshot()
    {
        var state = _stateStore.Snapshot();
        // Working set / private memory: cheap, no allocation pressure.
        long ws = 0, priv = 0;
        try { using var p = Process.GetCurrentProcess(); ws = p.WorkingSet64; priv = p.PrivateMemorySize64; }
        catch { }

        DateTime? nextSched = null;
        if (state.LastReconcileUtc is DateTime anchor)
        {
            int baseMin = Math.Max(5, _settings.ReconcileIntervalMinutes);
            nextSched   = anchor + TimeSpan.FromMinutes(baseMin);
        }

        return new StatsSnapshot(
            QueueDepth:                    _dedupe.Count,
            QueueCapacity:                 _queueCapacity,
            HighWaterMark:                 _highWaterMark,
            FilesCopiedTotal:              System.Threading.Interlocked.Read(ref _statsFilesCopiedTotal),
            FilesDeletedTotal:             System.Threading.Interlocked.Read(ref _statsFilesDeletedTotal),
            BytesCopiedTotal:              System.Threading.Interlocked.Read(ref _statsBytesCopiedTotal),
            ErrorsTotal:                   System.Threading.Interlocked.Read(ref _statsErrorsTotal),
            CurrentBandwidthBitsPerSecond: _rateLimiter.CurrentBitsPerSecond,
            TurboModeActive:               _turbo.IsActive,
            // atomic reads via Interlocked.Read on the long ticks.
            // TicksToDateTime returns null for the 0-sentinel ("never").
            Reconciling:                   IsCurrentlyReconciling(),
            LastReconcileStartUtc:         TicksToDateTime(System.Threading.Interlocked.Read(ref _statsLastReconcileStartTicks)),
            LastReconcileEndUtc:           TicksToDateTime(System.Threading.Interlocked.Read(ref _statsLastReconcileEndTicks)),
            NextScheduledReconcileUtc:     nextSched,
            LastRegistrySnapshotUtc:       state.LastRegistrySnapshotUtc,
            LastLogMirrorUtc:              state.LastLogMirrorUtc,
            WatchedRoots:                  _roots.Count,
            ProcessWorkingSetBytes:        ws,
            ProcessPrivateMemoryBytes:     priv);
    }

    /// <summary> — Convert atomic ticks value to nullable DateTime.</summary>
    private static DateTime? TicksToDateTime(long ticks) =>
        ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);

    /// <summary> — Reconciling = start stamped &amp; (end &lt; start OR end unset).</summary>
    private bool IsCurrentlyReconciling()
    {
        long startTicks = System.Threading.Interlocked.Read(ref _statsLastReconcileStartTicks);
        long endTicks   = System.Threading.Interlocked.Read(ref _statsLastReconcileEndTicks);
        if (startTicks == 0) return false;
        return endTicks == 0 || endTicks < startTicks;
    }

    private readonly PersistentStateStore _stateStore;
    private readonly ResumeStateStore     _resumeStore;

    public SyncController(AppSettings settings, Logger log, PersistentStateStore stateStore)
    {
        _settings    = settings;
        _log         = log;
        _stateStore  = stateStore;
        _resumeStore = new ResumeStateStore(log);
        _rateLimiter = new ByteRateLimiter(settings.MaxBandwidthBitsPerSecond);
        _mirror      = new FileMirror(log,
            new ThrottledFileCopier(_rateLimiter, log, _resumeStore,
                                    settings.ResumeEnabled, settings.ResumeMinFileSizeBytes,
                                    settings.LowerIoPriority, settings.PublishMode),
            settings.RetryCount,
            settings.DeletionSafetyGuardEnabled);

        // Extracted collaborators.
        _turbo     = new TurboModeController(settings, log, _rateLimiter, () => _dedupe.Count);
        _logMirror = new LogMirrorService(log, stateStore, _rateLimiter,
                                          settings.LowerIoPriority, GetMachineRoot);
        _scheduler = new ReconcileScheduler(settings);

        // BoundedChannel with FullMode.Wait — TryWrite returns false when full
        // (caller decides what to do).  WriteAsync would block instead.
        // We use TryWrite + Enqueue logic to handle priority manually.
        //
        // FullMode.Wait is deliberate, NOT dead config:
        // the Created/Changed fast path uses TryWrite (drop-on-full), but the
        // Delete/Rename slow path uses WriteAsync with a 5 s linked CTS so a
        // full queue makes it WAIT for a slot rather than silently drop a
        // deletion (which would break "delete src ⇒ delete dst").  DropWrite
        // would defeat that.
        int capacity = Math.Max(100, settings.QueueCapacity);
        _queueCapacity = capacity;
        _queue = Channel.CreateBounded<SyncOperation>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader                  = true,
                SingleWriter                  = false,
                AllowSynchronousContinuations = false,
                FullMode                      = BoundedChannelFullMode.Wait,
            });

        _hardExcludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProfileMirrorSync"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),  "ProfileMirrorSync"),
            // Never mirror the program's own install directory (would create a
            // self-sync loop if a user added it as a custom root).
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task StartAsync()
    {
        // Serialized lifecycle transition.
        //
        // The semaphore ensures only one StartAsync/StopAsync is in progress
        // at a time.  The Interlocked.CompareExchange below treats Stopped →
        // Starting as the only valid transition into a start; from any other
        // state (Starting/Running/Stopping) Start is a no-op (someone else is
        // already handling the lifecycle).
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            int prev = System.Threading.Interlocked.CompareExchange(
                ref _state, StateStarting, StateStopped);
            if (prev != StateStopped)
            {
                // Already Starting/Running/Stopping — caller's Start request is redundant.
                if (_log.TraceMode)
                    _log.Debug($"StartAsync no-op: state={StateName(prev)}");
                return;
            }

            if (string.IsNullOrWhiteSpace(_settings.DestinationRoot))
            {
                System.Threading.Volatile.Write(ref _state, StateStopped);
                throw new InvalidOperationException("Не задан путь к сетевой папке.");
            }

            try
            {
                await StartAsyncCore().ConfigureAwait(false);
                System.Threading.Volatile.Write(ref _state, StateRunning);
                _log.Info($"Синхронизация запущена. Наблюдаю {_roots.Count} папок.");
            }
            catch
            {
                // Transactional rollback.  If anything fails between
                // "worker started" and "_state=Running", undo what we did so
                // the controller is left in clean Stopped state with no
                // leaked watchers/workers.  We don't await the workers here
                // (they may still be wiring up), just signal them to abort
                // via _cts and let Dispose collect them later.
                try { _cts.Cancel(); } catch { }
                foreach (var r in _roots.Values)
                {
                    try { r.Watcher.EnableRaisingEvents = false; r.Watcher.Dispose(); } catch { }
                }
                _roots.Clear();
                foreach (var kv in _debouncers.ToArray())
                {
                    try { kv.Value.Cancel(); kv.Value.Dispose(); } catch { }
                }
                _debouncers.Clear();
                _registrySnapshot?.Dispose();
                _registrySnapshot = null;
                System.Threading.Volatile.Write(ref _state, StateStopped);
                throw;  // let caller see the exception
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// The actual start steps, extracted from StartAsync so the public method
    /// can wrap them in try/catch for transactional rollback.  This method
    /// does NOT manage _state — caller does.
    ///
    /// Changed from `async Task` to `Task` (returns
    /// <see cref="Task.CompletedTask"/>).  Previous version awaited a bogus
    /// Task.Yield() with an incorrect comment about exception propagation;
    /// the method has no actual asynchronous work.
    /// </summary>
    private Task StartAsyncCore()
    {
        // Lower process CPU priority for minimal footprint.
        //
        // reverted from process-wide PROCESS_MODE_BACKGROUND_BEGIN
        // (which caused's UI throttling regression — see CHANGELOG).
        //
        // Idle (was BelowNormal) per the "user shouldn't feel the
        // program" goal: the lowest scheduling class.  The UI stays responsive
        // because WinForms message-pump work runs on the UI thread which the
        // OS still boosts on foreground/input; the heavy work is the single
        // background copy worker, which we want at the very bottom.
        try { using var p = Process.GetCurrentProcess(); p.PriorityClass = ProcessPriorityClass.Idle; }
        catch { }

        try { Directory.CreateDirectory(GetMachineRoot()); }
        catch (Exception ex)
        {
            // Verbose error trace.  HResult identifies
            // the exact Win32 error (0x80070040 = ERROR_NETNAME_DELETED, etc.),
            // and passing `ex` to Warn lets Trace mode dump the stack.
            int hr   = ex.HResult;
            string winErr = unchecked((uint)hr).ToString("X8");
            _log.Warn($"Не удалось создать каталог '{GetMachineRoot()}': {ex.GetType().Name} " +
                      $"(HResult=0x{winErr}): {ex.Message}.  Будет повторено при следующей реконсиляции.",
                      ex);
        }

        _worker          = Task.Run(ProcessQueueAsync);
        _reconcileWorker = Task.Run(ReconcileLoopAsync);
        // Post-sync archiving runs on its own independent timer,
        // decoupled from reconcile.  Only spun up when the feature is enabled.
        if (_settings.PostSyncEnabled)
            _postSyncWorker = Task.Run(PostSyncLoopAsync);

        foreach (var spec in GetActiveRoots())
        {
            string destRoot = Path.Combine(GetMachineRoot(), spec.RelativePrefix);
            CreateWatcher(spec, destRoot);
        }

        if (_settings.MirrorRegistrySnapshots)
        {
            _registrySnapshot = new RegistrySnapshotService(_settings.RegistryPaths, GetMachineRoot(), _log);
            // Don't fire unconditionally at start.  Gate by the
            // configured RegistryBackupIntervalMinutes so a normal restart
            // (PC reboot, settings change) doesn't trigger ~30-80 MB of
            // redundant .reg uploads per machine.  If the snapshot truly is
            // overdue (last one > Interval ago), MaybeCaptureRegistrySnapshotAsync
            // will run it; otherwise it's a no-op until the reconcile loop
            // re-checks.
            _ = MaybeCaptureRegistrySnapshotAsync();
        }

        if (_settings.SyncOnStartup)
        {
            // Stop/Start atomicity.  If the previous reconcile
            // FINISHED within SkipStartupReconcileIfWithinMinutes, skip the
            // startup reconcile entirely.  Without this, a Stop→edit-settings→
            // Start cycle (seconds apart) would replay a full multi-pass scan
            // and waste server IO for no new data.  The reconcile loop's own
            // scheduling still ticks, so a real reconcile will happen at its
            // normal time.
            //
            // We deliberately key on COMPLETED (not "attempted") so a
            // mid-copy crash doesn't permanently suppress reconciles — a
            // crashed run never sets LastReconcileCompletedUtc, so the next
            // start picks up.
            var snap = _stateStore.Snapshot();
            int skipMin = Math.Max(0, _settings.SkipStartupReconcileIfWithinMinutes);
            bool recentSuccessfulReconcile =
                skipMin > 0
                && snap.LastReconcileCompletedUtc is DateTime lastDone
                && (DateTime.UtcNow - lastDone) < TimeSpan.FromMinutes(skipMin);

            if (recentSuccessfulReconcile)
            {
                var ago = DateTime.UtcNow - snap.LastReconcileCompletedUtc!.Value;
                _log.Info($"Стартовая реконсиляция пропущена: предыдущая " +
                          $"завершилась {ago.TotalMinutes:F1} мин назад " +
                          $"(порог {skipMin} мин).  Следующая — по расписанию.");
            }
            else
            {
                _stateStore.Update(s => s.LastReconcileUtc = DateTime.UtcNow);
                if (snap.LastReconcileCompletedUtc is DateTime lastDone2)
                {
                    var ago = DateTime.UtcNow - lastDone2;
                    _log.Info($"Стартовая реконсиляция: предыдущая завершилась " +
                              $"{ago.TotalMinutes:F1} мин назад, порог {skipMin} мин — запускаем.");
                }
                else
                {
                    _log.Info("Стартовая реконсиляция: первая после установки/чистого старта.");
                }
                EnqueueAllReconcile();
            }
        }

        return Task.CompletedTask;
    }

    private static string StateName(int s) => s switch
    {
        StateStopped  => "Stopped",
        StateStarting => "Starting",
        StateRunning  => "Running",
        StateStopping => "Stopping",
        _             => $"?({s})"
    };

    public async Task StopAsync()
    {
        // Serialized lifecycle transition (see StartAsync).
        //
        // We accept transitions Running → Stopping AND Starting → Stopping.
        // The latter handles "user clicked Stop while Start is still wiring
        // up watchers" — uncommon but well-defined: the Start branch will
        // see the cancelled CTS or completed semaphore wait and unwind.
        // Stopped/Stopping → return (no-op).
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            int s = System.Threading.Volatile.Read(ref _state);
            if (s == StateStopped || s == StateStopping)
            {
                if (_log.TraceMode) _log.Debug($"StopAsync no-op: state={StateName(s)}");
                return;
            }
            System.Threading.Volatile.Write(ref _state, StateStopping);
            try
            {
                await StopAsyncCore().ConfigureAwait(false);
            }
            finally
            {
                System.Threading.Volatile.Write(ref _state, StateStopped);
            }
            _log.Info("Синхронизация остановлена.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopAsyncCore()
    {
        // Stop sequence reordered so we stop ACCEPTING new work
        // before signalling workers to drain.  Previously _cts.Cancel() fired
        // first, then watcher.Dispose() — opening a small window in which
        // FSW callbacks running on ThreadPool threads could still call
        // Schedule() / Enqueue() after cancellation began.  Those tasks then
        // race against _queue.Writer.TryComplete() and produce noise (drop
        // warnings, "queue full" log spam).  Functionally harmless before;
        // cleaner now.

        // 1. Stop accepting new events.  EnableRaisingEvents=false is
        //    synchronous — after this call returns, no new FSW callbacks
        //    will be queued (in-flight ones may still finish).
        foreach (var root in _roots.Values)
        {
            try { root.Watcher.EnableRaisingEvents = false; } catch { }
            try { root.Watcher.Dispose(); } catch { }
        }
        _roots.Clear();

        // 2. Cancel all pending debouncers so their Task.Delay throws OCE
        //    and they exit without enqueuing.
        foreach (var kv in _debouncers.ToArray())
        {
            try { kv.Value.Cancel(); } catch { }
            kv.Value.Dispose();
        }
        _debouncers.Clear();

        // 3. NOW cancel workers and close the channel.
        _cts.Cancel();
        _queue.Writer.TryComplete();

        if (_worker is not null)          try { await _worker.ConfigureAwait(false); }          catch { }
        if (_reconcileWorker is not null) try { await _reconcileWorker.ConfigureAwait(false); } catch { }
        if (_postSyncWorker is not null)  try { await _postSyncWorker.ConfigureAwait(false); }  catch { }

        if (_registrySnapshot is not null)
        {
            // Bound registry capture so a wedged reg.exe
            // can't hang Stop indefinitely.  Previously this used
            // CancellationToken.None: if HKCU\Software is large or the disk
            // is slow, reg.exe could block for minutes, freezing Stop and
            // any restart-after-settings flow.  10 s is generous for a
            // single registry hive on a working machine; if it doesn't
            // finish in that time, abandon the snapshot — the next reconcile
            // will re-capture.
            //
            // Honour the RegistryBackupIntervalMinutes gate here too.
            // Without this, a Stop/Start cycle (e.g. user changed settings)
            // would always trigger a snapshot regardless of how recent the
            // last one was — defeating the whole point of the 30-day interval.
            bool intervalGate = true;
            int intervalMin = _settings.RegistryBackupIntervalMinutes;
            if (intervalMin > 0)
            {
                var snap = _stateStore.Snapshot();
                if (snap.LastRegistrySnapshotUtc is DateTime last
                    && (DateTime.UtcNow - last) < TimeSpan.FromMinutes(intervalMin))
                {
                    intervalGate = false;
                }
            }

            // also require the destination to be
            // reachable, so a Stop while the server is offline doesn't log a
            // Warning + stack trace from the failed CreateDirectory.
            if (intervalGate && !IsDestinationReachable())
            {
                intervalGate = false;
                if (_log.TraceMode)
                    _log.Debug("StopAsync: снимок реестра пропущен — сеть недоступна.");
            }

            if (intervalGate)
            {
                using var regCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await _registrySnapshot.CaptureAllAsync(regCts.Token).ConfigureAwait(false);
                    _stateStore.Update(s => s.LastRegistrySnapshotUtc = DateTime.UtcNow);
                }
                catch (OperationCanceledException)
                {
                    _log.Warn("StopAsync: снимок реестра не завершился за 10 с — отменён.");
                }
                catch (Exception ex)
                {
                    _log.Warn($"StopAsync: ошибка снимка реестра: {ex.Message}", ex);
                }
            }
            _registrySnapshot.Dispose();
            _registrySnapshot = null;
        }
    }

    /// <summary>
    /// Called by TrayApp on wake-from-sleep / session resume.
    /// Schedules a reconcile for all roots after a short delay
    /// to let the network re-establish before we probe.
    /// </summary>
    public void TriggerResumeReconcile()
    {
        if (!IsRunning) return;
        // clear the dedup flag so post-resume outages produce their
        // own warning instead of being swallowed by the pre-suspend flag.
        System.Threading.Interlocked.Exchange(ref _destinationWarningLogged, 0);
        _log.Info("Возобновление после сна/гибернации — плановая реконсиляция через 30 с...");
        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for network to come back up
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);
                if (!IsDestinationReachable())
                {
                    _log.Warn("Сеть не восстановилась после пробуждения, реконсиляция отложена.");
                    return;
                }
                _log.Info("Запускаю реконсиляцию после пробуждения.");
                EnqueueAllReconcile();
            }
            catch (OperationCanceledException) { }
        });
    }

    public string GetMachineRoot() =>
        NormalizeMachineRoot(_settings.DestinationRoot!, Environment.MachineName, Environment.UserName);

    /// <summary>
    /// Normalize a destination-root path and append
    /// per-machine and per-user segments.
    ///
    /// Critical edge case: <see cref="Path.Combine(string, string, string)"/>
    /// treats a bare drive letter ("Z:") as DRIVE-RELATIVE, not drive-rooted.
    /// "Z:" + "PCLITE" + "Den" produces "Z:PCLITE\\Den", which Windows
    /// resolves relative to Z:'s current working directory — almost
    /// certainly not what the user intended.  We restore the trailing
    /// separator for drive-letter roots before combining.
    ///
    /// Extracted as an internal static helper so the test project can
    /// cover the regression directly without spinning up a full
    /// SyncController instance.
    /// </summary>
    internal static string NormalizeMachineRoot(string destinationRoot, string machine, string user)
    {
        string root = destinationRoot
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length == 2 && root[1] == ':') root += Path.DirectorySeparatorChar;
        return Path.Combine(root, machine, user);
    }

    // ── FileSystemWatcher ─────────────────────────────────────────────────────

    private void CreateWatcher(SyncRootSpec spec, string destinationRoot)
    {
        if (!Directory.Exists(spec.SourcePath))
        {
            _log.Warn($"Пропускаю несуществующую папку: {spec.SourcePath}");
            return;
        }

        var watcher = new FileSystemWatcher(spec.SourcePath)
        {
            IncludeSubdirectories = true,
            NotifyFilter          = NotifyFilters.FileName    | NotifyFilters.DirectoryName |
                                    NotifyFilters.LastWrite   | NotifyFilters.CreationTime  |
                                    NotifyFilters.Size,
            InternalBufferSize    = 65536,
            Filter                = "*",
        };

        watcher.Created += (_, e) => EnqueueChange(SyncOperationKind.CreateOrChange, spec, destinationRoot, e.FullPath);
        watcher.Changed += (_, e) => EnqueueChange(SyncOperationKind.CreateOrChange, spec, destinationRoot, e.FullPath);
        watcher.Deleted += (_, e) => EnqueueChange(SyncOperationKind.Delete,         spec, destinationRoot, e.FullPath);
        watcher.Renamed += (_, e) => EnqueueChange(SyncOperationKind.Rename,         spec, destinationRoot, e.OldFullPath, e.FullPath);
        watcher.Error   += (_, ev) =>
        {
            _log.Warn($"FSW overflow [{spec.Name}]: {ev.GetException()?.Message}. Запускаю реконсиляцию.");
            Enqueue(new SyncOperation(SyncOperationKind.Reconcile, spec.SourcePath, destinationRoot, null, null));
        };

        // Enable AFTER subscribing — zero event loss guarantee
        watcher.EnableRaisingEvents = true;
        _roots[spec.SourcePath] = new WatchedRoot(spec, destinationRoot, watcher);
        _log.Info($"FSW [{spec.Name}]: {spec.SourcePath}  →  {destinationRoot}");
    }

    // ── Event routing ─────────────────────────────────────────────────────────

    private void EnqueueChange(SyncOperationKind kind, SyncRootSpec spec,
        string destinationRoot, string path, string? newPath = null)
    {
        string rel = Path.GetRelativePath(spec.SourcePath, path);
        if (rel.StartsWith("..", StringComparison.Ordinal)) return;
        if (ShouldIgnore(path)) return;

        string dest = Path.Combine(destinationRoot, rel);

        if (kind == SyncOperationKind.Rename && newPath is not null)
        {
            string newRel = Path.GetRelativePath(spec.SourcePath, newPath);
            if (newRel.StartsWith("..", StringComparison.Ordinal)) return;
            Schedule(new SyncOperation(kind, path, dest, newPath, Path.Combine(destinationRoot, newRel)));
            return;
        }
        Schedule(new SyncOperation(kind, path, dest, null, null));
    }

    private void Schedule(SyncOperation op)
    {
        int delay = Math.Max(0, _settings.FileDebounceMilliseconds);
        string key = DedupKey(op);
        var cts = new CancellationTokenSource();
        // Cancel previous CTS but DO NOT Dispose it from here.
        // The awaiting Task may still be inside Task.Delay(token); calling
        // Dispose() synchronously here makes that Task throw
        // ObjectDisposedException (not OperationCanceledException), which the
        // catch below doesn't handle — exception bubbles to the unobserved
        // task path. CTS without CancelAfter holds no
        // unmanaged resources, so GC will reclaim it safely after the
        // awaiting task observes cancellation.
        _debouncers.AddOrUpdate(key, cts, (_, existing) =>
        {
            try { existing.Cancel(); } catch { }
            return cts;
        });

        // trace hook: warn once when debouncer pool grows beyond 1000.
        // Each debouncer is one short-lived Task; under normal operation count
        // stays below 50.  >1000 means an event-storm — confirms the bounded
        // channel + dedupe are working as intended.
        int count = _debouncers.Count;
        if (count == 1000 || count == 5000 || count == 10000)
            _log.Warn($"Debouncer pool: {count} активных задач. Возможен event-storm в источнике.");

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > 0) await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                Enqueue(op);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { /* CTS disposed under us — treat as cancelled */ }
            finally
            {
                if (_debouncers.TryGetValue(key, out var cur) && ReferenceEquals(cur, cts))
                    _debouncers.TryRemove(key, out _);
                try { cts.Dispose(); } catch { } // best-effort; may already be disposed if reused
            }
        }, CancellationToken.None);
    }

    // Backpressure stats — for logging spikes once per period, not per event
    private long _droppedSinceLastLog = 0;
    // was DateTime _lastDropLog; FSW callbacks on multiple threads
    // could race past the 30 s gate.  Now long ticks
    // updated via Interlocked.CompareExchange.
    private long _lastDropLogTicks = 0;

    // High-water-mark tracking for queue depth.  Logs when we cross
    // 50%/80%/95% of capacity — helps validate the bounded-channel sizing in
    // real-world bursts. Reset on each scheduled reconcile.
    private int _highWaterMark = 0;
    private int _lastReportedWatermarkBucket = 0;

    private void Enqueue(SyncOperation op)
    {
        string key = DedupKey(op);
        if (!_dedupe.TryAdd(key, 0)) return;

        // Fast path: room in queue
        if (_queue.Writer.TryWrite(op))
        {
            // Track queue pressure. _dedupe.Count is our cheap counter
            // (Channel.Reader.Count would throw on unbounded; here we have
            // bounded but _dedupe is still an O(1) read).
            //
            // Both _highWaterMark and _lastReportedWatermarkBucket
            // were plain int with check‑then‑act under concurrent FSW callbacks.
            // On x64 torn read/write for int doesn't happen, but the C# memory
            // model formally doesn't guarantee ordering without a barrier — two
            // callbacks could simultaneously pass the watermark check and both
            // log identical Warn lines.  Now:
            //   • _highWaterMark uses a CAS retry loop (monotonic max).
            //   • _lastReportedWatermarkBucket uses single CAS — only the
            //     thread whose CAS wins emits the log line.
            // _lastReportedWatermarkBucket is reset in EnqueueAllReconcile, which
            // is called from a single thread (reconcile loop), so the reset
            // itself is race‑free.
            int depth = _dedupe.Count;
            int prevHwm;
            do { prevHwm = System.Threading.Volatile.Read(ref _highWaterMark); }
            while (depth > prevHwm
                   && Interlocked.CompareExchange(ref _highWaterMark, depth, prevHwm) != prevHwm);

            int pct    = depth * 100 / _queueCapacity;
            int bucket = pct >= 95 ? 95 : pct >= 80 ? 80 : pct >= 50 ? 50 : 0;
            int prevBucket = System.Threading.Volatile.Read(ref _lastReportedWatermarkBucket);
            if (bucket > prevBucket &&
                Interlocked.CompareExchange(ref _lastReportedWatermarkBucket, bucket, prevBucket) == prevBucket)
            {
                _log.Warn($"Очередь синхронизации заполнена на {pct}% ({depth}/{_queueCapacity}). " +
                          "Рассмотрите увеличение QueueCapacity или паузы между файлами.");
            }
            // Adaptive trigger — when queue pressure crosses the
            // configured threshold, request an early reconciliation.
            // The reconcile loop honours its own minimum-gap to avoid
            // thrashing if events keep arriving.
            int threshold = Math.Clamp(_settings.EarlyReconcileQueueThresholdPct, 0, 100);
            if (threshold > 0 && pct >= threshold)
            {
                _stateStore.Update(s =>
                {
                    if (!s.EarlyReconcileRequested)
                    {
                        s.EarlyReconcileRequested = true;
                        s.LastEarlyReconcileRequestUtc = DateTime.UtcNow;
                        _log.Info($"Очередь {pct}% ≥ {threshold}% — запрошена досрочная реконсиляция.");
                    }
                });
            }
            // Real-time turbo activation (now via TurboModeController).
            _turbo.MaybeActivate(depth);
            return;
        }

        // Slow path: queue is full.
        //
        // For Created/Changed: drop the event.  The periodic reconcile will
        // catch the file when it walks the tree, and the dedupe set is cleared
        // so the same file can be re-enqueued later if FSW fires again.
        //
        // For Delete/Rename: a missed event means the destination diverges
        // from the source until the next reconcile (which is a HOLE in the
        // "delete from source ⇒ delete from dest" guarantee).  We retry
        // briefly using a short Task to wait for room — bounded duration so
        // the caller (the FSW callback) doesn't block the watcher's thread.
        if (op.Kind == SyncOperationKind.Delete || op.Kind == SyncOperationKind.Rename)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var briefCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    briefCts.CancelAfter(TimeSpan.FromSeconds(5));
                    await _queue.Writer.WriteAsync(op, briefCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    _dedupe.TryRemove(key, out _);
                    Interlocked.Increment(ref _droppedSinceLastLog);
                }
            });
            return;
        }

        // Created/Changed — drop silently, log periodically
        _dedupe.TryRemove(key, out _);
        long n = Interlocked.Increment(ref _droppedSinceLastLog);
        // Replace the racy "read field → compare → write field"
        // pattern with an Interlocked CAS on a ticks value.  Two FSW
        // callbacks on different ThreadPool threads could previously both
        // satisfy the 30 s gate and both log + both reset the counter.
        // Now only the thread whose CAS wins
        // emits the log.
        long nowTicks  = DateTime.UtcNow.Ticks;
        long lastTicks = Interlocked.Read(ref _lastDropLogTicks);
        if (nowTicks - lastTicks >= TimeSpan.FromSeconds(30).Ticks &&
            Interlocked.CompareExchange(ref _lastDropLogTicks, nowTicks, lastTicks) == lastTicks)
        {
            _log.Warn($"Очередь переполнена: пропущено {n} событий за последние 30 с. " +
                      "Изменения подберёт плановая реконсиляция.");
            Interlocked.Exchange(ref _droppedSinceLastLog, 0);
        }
    }

    private void EnqueueAllReconcile()
    {
        // New reconcile cycle — reset watermark bucket so next burst
        // generates fresh warnings (e.g. on a single user dumping 50k files).
        _lastReportedWatermarkBucket = 0;
        _highWaterMark = 0;
        // Snapshot root count BEFORE enumeration to ensure the
        // per-cycle counter matches the number of Reconcile ops we enqueue.
        // ConcurrentDictionary.Values is a snapshot, so the count is stable
        // for the duration of this method.
        var rootsSnapshot = _roots.Values.ToArray();
        System.Threading.Interlocked.Exchange(ref _rootsRemainingInCycle, rootsSnapshot.Length);
        foreach (var root in rootsSnapshot)
            Enqueue(new SyncOperation(SyncOperationKind.Reconcile, root.Spec.SourcePath, root.DestinationRoot, null, null));
    }

    private static string DedupKey(SyncOperation op) =>
        op.Kind == SyncOperationKind.Rename && op.NewPath is not null
            ? $"{op.SourcePath}|{op.NewPath}"
            : op.SourcePath;

    // ── Queue worker ──────────────────────────────────────────────────────────

    private async Task ProcessQueueAsync()
    {
        // Reset turbo state on entry.  Covers the
        // crash-restart path: if a previous ProcessQueueAsync threw mid-cycle
        // with turbo active, the residual turbo bandwidth would otherwise stay
        // applied.  Reset is cheap and idempotent.
        _turbo.Reset();

        try
        {
            while (await _queue.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var op))
                {
                    // Thread-level background priority moved DOWN into
                    // ThrottledFileCopier.CopyOnceAsync, around the WriteAsync
                    // call itself.  Previous attempt wrapped DispatchAsync
                    // here, but `await DispatchAsync` may resume on a different
                    // ThreadPool thread — leaking BACKGROUND priority on the
                    // original thread (it never gets END) and applying END on
                    // a thread that was never BEGIN'd (ok=False in log).
                    // Both observed in log lines 83, 363, 519, 800.
                    try
                    {
                        await DispatchAsync(op).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        // Bump stats error counter (used by the stats window).
                        System.Threading.Interlocked.Increment(ref _statsErrorsTotal);
                        _log.Error($"Ошибка обработки [{op.Kind}] {op.SourcePath}", ex);
                    }
                    finally
                    {
                        _dedupe.TryRemove(DedupKey(op), out _);
                    }
                }
                // Inner TryRead loop drained: wind back to baseline.
                _turbo.MaybeDeactivate();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Outer safety net.  The inner catch (line ~653) already
            // handles per-op failures; this catches anything that escapes
            // WaitToReadAsync/TryRead themselves.  Before this, the worker
            // exited and _state stayed Running — events enqueued, nobody
            // reading, channel back-pressure eventually blocks producers.
            // Now we log Error and restart after a back-off.
            //
            // Re-check lifecycle state before respawn (see
            // ReconcileLoopAsync for the rationale).
            _log.Error("ProcessQueueAsync упал с неожиданным исключением — перезапуск через 30 с", ex);
            try { await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException)    { return; }
            if (System.Threading.Volatile.Read(ref _state) != StateRunning) return;
            _worker = Task.Run(ProcessQueueAsync);
        }
    }

    private async Task DispatchAsync(SyncOperation op)
    {
        switch (op.Kind)
        {
            case SyncOperationKind.CreateOrChange:
                // credit real-time stats.
                // MirrorCreateOrChangeAsync returns bytes actually copied (0 for
                // skip/dir).  Filing under FilesCopied if bytes > 0 keeps the
                // counter aligned with what the user can see was actually written.
                long bytes = await _mirror.MirrorCreateOrChangeAsync(op.SourcePath, op.DestinationPath, _cts.Token).ConfigureAwait(false);
                if (bytes > 0)
                {
                    System.Threading.Interlocked.Increment(ref _statsFilesCopiedTotal);
                    System.Threading.Interlocked.Add(ref _statsBytesCopiedTotal, bytes);
                }
                FileProcessed?.Invoke(op.SourcePath);
                break;

            case SyncOperationKind.Delete:
                // credit real-time stats.  MirrorDeleteAsync returns
                // true only when something was actually deleted.
                bool deleted = await _mirror.MirrorDeleteAsync(op.SourcePath, op.DestinationPath, _cts.Token).ConfigureAwait(false);
                if (deleted) System.Threading.Interlocked.Increment(ref _statsFilesDeletedTotal);
                break;

            case SyncOperationKind.Rename:
                if (op.NewDestinationPath is null || op.NewPath is null) break;
                await _mirror.MirrorRenameAsync(op.DestinationPath, op.NewPath, op.NewDestinationPath, _cts.Token).ConfigureAwait(false);
                break;

            case SyncOperationKind.Reconcile:
                if (!IsDestinationReachable())
                {
                    // log once per outage instead of once per folder
                    if (System.Threading.Interlocked.Exchange(ref _destinationWarningLogged, 1) == 0)
                        _log.Warn("Сетевой путь недоступен, реконсиляция отложена.");
                    break;
                }
                // Reset on first successful reachability after an outage so
                // a future outage gets its own warning.
                System.Threading.Interlocked.Exchange(ref _destinationWarningLogged, 0);
                await RunReconcileAsync(op.SourcePath, op.DestinationPath).ConfigureAwait(false);
                break;
        }
    }

    private async Task RunReconcileAsync(string srcRoot, string dstRoot)
    {
        // Detect the "user added our own install dir as a watched
        // root" case.  ShouldIgnore will reject every file under it (correct,
        // prevents self-sync loop), but the user sees "0 скопировано" and
        // doesn't know why.  Log a one-time Warn explaining the situation.
        // Once-per-session is enough — adding the same path again across a
        // restart re-logs, which is fine.
        foreach (string hard in _hardExcludes)
        {
            if (srcRoot.StartsWith(hard, StringComparison.OrdinalIgnoreCase))
            {
                lock (_hardExcludedWarnLock)
                {
                    if (_hardExcludedWatchedRootsWarned.Add(srcRoot))
                    {
                        _log.Warn($"Папка '{srcRoot}' попадает в системное исключение " +
                                  $"(каталог программы '{hard}') — реконсиляция её содержимого " +
                                  "пропускается во избежание самокопирования.  Уберите её из " +
                                  "пользовательских папок, если требуется зеркалировать другую папку.");
                    }
                }
                break;
            }
        }

        // Decide throttle options
        var opts = MakeReconcileOptions();

        // Use ReconcileSummary return value instead of the local
        // filesCopied tally.  Counter on the callback is still needed so
        // turbo activation can react mid-cycle (callback fires per-file;
        // summary arrives only after the whole pass completes).
        int filesCopiedInCallback = 0;
        var summary = await _mirror.ReconcileRootAsync(srcRoot, dstRoot, ShouldIgnore, opts, () =>
        {
            filesCopiedInCallback++;
            FileProcessed?.Invoke(srcRoot);
            // Turbo during scheduled reconcile is now opt-in
            // (TurboOnReconcile, default off).  Real-time event-storms still
            // trigger turbo via the Enqueue path; a routine nightly sweep stays
            // at the base limit so it doesn't spike the shared uplink.
            // _dedupe.Count is pending FSW events; +filesCopiedInCallback covers
            // the file currently dispatching (already removed from _dedupe).
            if (_settings.TurboOnReconcile)
                _turbo.MaybeActivate(_dedupe.Count + filesCopiedInCallback);
        }, _cts.Token).ConfigureAwait(false);

        // Wind back to baseline if the queue drained during reconcile.
        _turbo.MaybeDeactivate();

        // Record completion of THIS root for the Stop/Start atomicity
        // logic in StartAsync.
        //
        // Previously stamped LastReconcileCompletedUtc
        // here unconditionally after each root, which over-promised: a partial
        // cycle (e.g. crash between roots 1 and 2 of 3) would mark "completed
        // recently" and let the next startup skip a fresh reconcile, leaving
        // root 2 and 3's changes unsynced for up to 24 h.  Now we only stamp
        // once the whole cycle (all enqueued roots) has finished.
        bool cycleFullyComplete = false;
        if (System.Threading.Interlocked.Decrement(ref _rootsRemainingInCycle) <= 0)
        {
            cycleFullyComplete = true;
            _stateStore.Update(s => s.LastReconcileCompletedUtc = DateTime.UtcNow);
            // Stats end marker for the live stats window.
            // Stored as long ticks (no DateTime? tearing risk).
            System.Threading.Interlocked.Exchange(ref _statsLastReconcileEndTicks, DateTime.UtcNow.Ticks);
        }

        // Credit all live-stats counters from the per-root summary.
        // Previously only FilesCopied was wired; BytesCopied and FilesDeleted
        // were always zero in the stats window.
        System.Threading.Interlocked.Add(ref _statsFilesCopiedTotal,  summary.FilesCopied);
        System.Threading.Interlocked.Add(ref _statsBytesCopiedTotal,  summary.BytesCopied);
        System.Threading.Interlocked.Add(ref _statsFilesDeletedTotal, summary.OrphansDeleted);
        // Pass-1 per-file errors (filesSkipped) credit the global errors counter
        // alongside the existing DispatchAsync-outer-catch crediting.
        if (summary.FilesSkipped > 0)
            System.Threading.Interlocked.Add(ref _statsErrorsTotal, summary.FilesSkipped);

        // Once-per-day log mirror (if enabled), via LogMirrorService.
        // Triggered AFTER the last root of a cycle.  Failure-tolerant: a log
        // mirror error never blocks the regular reconcile flow.
        if (cycleFullyComplete && _settings.MirrorLogs)
        {
            try { await _logMirror.MirrorIfDueAsync(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.Warn($"MirrorLogs: {ex.Message}", ex); }
        }

        // Post-sync archiving runs on its OWN timer (PostSyncLoopAsync),
        // not coupled to reconcile completion.  See StartAsyncCore / PostSyncLoopAsync.
    }

    private ReconcileOptions MakeReconcileOptions() =>
        new(
            FileDelayMs:  _settings.ReconcileFileDelayMs,
            BatchSize:    _settings.ReconcileBatchSize,
            BatchPauseMs: _settings.ReconcileBatchPauseMs)
        {
            // while turbo is active, suppress the artificial inter-file/
            // batch pauses so the raised bandwidth cap can drain the backlog.
            // The byte-rate limiter still enforces the turbo Mbit/s cap.
            IsTurboActive = () => _turbo.IsActive,
        };

    // ── Post-sync archive loop  ───────────────────────────────────────

    /// <summary>
    /// Independent timer that runs the configured external program (archiver /
    /// script) on its OWN schedule, decoupled from reconcile completion.  The
    /// cadence is AppSettings.PostSyncIntervalMinutes (default 1440 = once/day).
    ///
    /// Why decoupled: archiving the whole destination tree (e.g. 7-Zip -mx=9) is
    /// a heavy, infrequent operation whose natural rhythm is "once a day", not
    /// "after every reconcile" (reconcile may fire many times a day via the
    /// adaptive trigger).  Running it on its own timer also means a quiet day
    /// with no file changes still produces the daily archive.
    ///
    /// Resilience mirrors ReconcileLoopAsync: poll-based, network-reachability
    /// gate with one-warn-per-outage dedup, outer catch that restarts the loop
    /// after a back-off only while the controller is still Running.  The
    /// PostSyncRunner's LastPostSyncRunUtc gate is retained as a secondary guard
    /// so a restart mid-interval doesn't double-fire.
    /// </summary>
    private async Task PostSyncLoopAsync()
    {
        int intervalMin = Math.Max(1, _settings.PostSyncIntervalMinutes);
        _log.Info($"Post-sync: отдельный таймер, интервал {intervalMin} мин " +
                  $"(программа: {(_settings.PostSyncEnabled ? _settings.PostSyncExePath : "—")}).");

        var runner = new PostSyncRunner(_settings, _log, _stateStore, GetMachineRoot());

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                // Decide how long to sleep until the next eligible run, based on
                // the persisted last-run stamp so the schedule survives restarts.
                TimeSpan wait = ComputePostSyncWait(intervalMin);
                await Task.Delay(wait, _cts.Token).ConfigureAwait(false);

                if (!_settings.PostSyncEnabled) return;   // disabled mid-run

                if (!IsDestinationReachable())
                {
                    if (System.Threading.Interlocked.Exchange(ref _postSyncOutageWarned, 1) == 0)
                        _log.Warn("Post-sync пропущен: сеть недоступна. Повтор после восстановления.");
                    // Short retry cadence during an outage (don't wait a full day).
                    await Task.Delay(TimeSpan.FromMinutes(5), _cts.Token).ConfigureAwait(false);
                    continue;
                }
                System.Threading.Interlocked.Exchange(ref _postSyncOutageWarned, 0);

                try { await runner.MaybeRunAsync(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.Warn($"Post-sync: {ex.Message}", ex); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Error("PostSyncLoopAsync упал с неожиданным исключением — перезапуск через 60 с", ex);
            try { await Task.Delay(TimeSpan.FromSeconds(60), _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException)    { return; }
            if (System.Threading.Volatile.Read(ref _state) != StateRunning) return;
            _postSyncWorker = Task.Run(PostSyncLoopAsync);
        }
    }

    /// <summary>
    /// Time until the next post-sync run is due, derived from the persisted
    /// LastPostSyncRunUtc.  Clamped to [1 min, interval] so a never-run state
    /// fires shortly after startup and an overdue state fires promptly.
    ///
    /// the never-run case adds a 1–10 min jitter so a
    /// fleet deployed together doesn't all launch the archiver at exactly
    /// +1 min and spike the server.  NOTE: in .NET 5+ string.GetHashCode() is
    /// randomized per-process, so this jitter is effectively random per restart
    /// (not stable per machine) — which is fine, arguably better, for desync.
    /// </summary>
    private TimeSpan ComputePostSyncWait(int intervalMin)
    {
        var snap = _stateStore.Snapshot();
        if (snap.LastPostSyncRunUtc is not DateTime last)
        {
            int jitterMin = 1 + Math.Abs(Environment.MachineName.GetHashCode()) % 10; // 1..10 min (random per process restart)
            return TimeSpan.FromMinutes(jitterMin);  // never run → soon, desynced across the fleet
        }

        DateTime due = last + TimeSpan.FromMinutes(intervalMin);
        TimeSpan remaining = due - DateTime.UtcNow;
        if (remaining < TimeSpan.FromMinutes(1)) return TimeSpan.FromMinutes(1);
        return remaining;
    }

    // ── Registry snapshot  ───────────────────────────────────────────

    /// <summary>
    /// Captures a registry snapshot at most once per RegistryBackupIntervalMinutes
    /// (default: 30 days).  Decoupled from the reconcile loop in to
    /// avoid sending ~30-80 MB of .reg dumps per machine per day, when the
    /// registry contents barely change.  Set RegistryBackupIntervalMinutes=0
    /// to fall back to the legacy "snapshot every reconcile" behaviour.
    /// </summary>
    private async Task MaybeCaptureRegistrySnapshotAsync()
    {
        if (!_settings.MirrorRegistrySnapshots || _registrySnapshot is null) return;

        // Skip quietly when the destination is offline.
        // Without this, every Start/Stop while the server is unreachable logged
        // a full Warning + stack trace from Directory.CreateDirectory deep in
        // CaptureAllAsync.  The reconcile/post-sync loops already gate on
        // reachability; do the same here so an outage doesn't spam the log.
        if (!IsDestinationReachable())
        {
            if (_log.TraceMode)
                _log.Debug("Снимок реестра пропущен: сеть недоступна.");
            return;
        }

        int intervalMin = _settings.RegistryBackupIntervalMinutes;
        if (intervalMin > 0)
        {
            var snap = _stateStore.Snapshot();
            if (snap.LastRegistrySnapshotUtc is DateTime last
                && (DateTime.UtcNow - last) < TimeSpan.FromMinutes(intervalMin))
            {
                if (_log.TraceMode)
                {
                    var ago = DateTime.UtcNow - last;
                    _log.Debug($"Снимок реестра пропущен: предыдущий {ago.TotalHours:F1} ч назад, " +
                               $"интервал {intervalMin} мин ({intervalMin / 1440.0:F1} сут).");
                }
                return;
            }
        }

        try
        {
            await _registrySnapshot.CaptureAllAsync(_cts.Token).ConfigureAwait(false);
            _stateStore.Update(s => s.LastRegistrySnapshotUtc = DateTime.UtcNow);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Warn($"Снимок реестра: ошибка — {ex.Message}", ex);
        }
    }

    // ── Reconcile loop (timer) ────────────────────────────────────────────────

    /// <summary>
    /// Scheduled reconciliation loop,.
    ///
    /// Anchor for the schedule is <c>PersistentState.LastReconcileUtc</c>:
    ///   • If null (first run) → record "now" and wait one full interval.
    ///     The startup reconciliation (when SyncOnStartup=true) already
    ///     handles the initial catch-up.
    ///   • Next-due = LastReconcileUtc + Interval + ±(jitterPct/2)% jitter
    ///   • If <c>EarlyReconcileRequested</c> is set (from queue-pressure
    ///     detection in Enqueue), reconcile sooner — but never more often
    ///     than EarlyReconcileMinGapMinutes.
    ///
    /// We poll every minute to react quickly to the early-reconcile flag.
    /// One minute is cheap — just a sleep + a state Snapshot() (no I/O).
    /// </summary>
    private async Task ReconcileLoopAsync()
    {
        // scheduling (anchor + jitter + due/early decision) now lives
        // in ReconcileScheduler. Per-PC jitter desync is preserved
        // (the scheduler seeds its RNG from the machine-name hash).
        _log.Info($"Реконсиляция: базовый интервал {_scheduler.BaseIntervalMinutes} мин, " +
                  $"jitter {_scheduler.JitterPercent}% (±{_scheduler.JitterPercent / 2}% от интервала), " +
                  $"adaptive trigger при {_settings.EarlyReconcileQueueThresholdPct}% очереди.");

        // Anchor: if no last-reconcile is recorded, set it to "now".  The
        // startup reconcile (if SyncOnStartup=true) does the initial scan;
        // the loop then waits a full interval before the next scheduled one.
        _stateStore.Update(s => s.LastReconcileUtc ??= DateTime.UtcNow);

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                // Poll every 60 s — cheap; lets us react quickly to early flag.
                await Task.Delay(TimeSpan.FromSeconds(60), _cts.Token).ConfigureAwait(false);

                var state = _stateStore.Snapshot();
                DateTime now = DateTime.UtcNow;
                var decision = _scheduler.Evaluate(state, now);
                if (!decision.IsDue) continue;

                // ReconcileEnabled is the master switch for the
                // PERIODIC sweep.  When off, we still honour a queue-pressure
                // (early) trigger as a data-safety valve — an FSW event-storm
                // that overflows the queue must be reconciled regardless — but
                // we skip the plain time-based sweep.
                if (!_settings.ReconcileEnabled && !decision.DueByEarly)
                {
                    // Advance the anchor so we don't re-evaluate every minute;
                    // the next check happens one interval later.
                    _stateStore.Update(s => s.LastReconcileUtc = now);
                    continue;
                }

                if (!IsDestinationReachable())
                {
                    // Dedup the outage warn to one per outage (not one/minute).
                    if (System.Threading.Interlocked.Exchange(ref _reconcileLoopOutageWarned, 1) == 0)
                        _log.Warn("Плановая реконсиляция пропущена: сеть недоступна. Дальнейшие попытки будут логированы только при восстановлении.");
                    continue;  // don't advance the anchor — retry next minute
                }
                System.Threading.Interlocked.Exchange(ref _reconcileLoopOutageWarned, 0);

                string reason = decision.DueByEarly
                    ? "досрочная (queue pressure)"
                    : $"плановая (прошло {(now - decision.Anchor).TotalHours:F1} ч)";
                _log.Info($"Реконсиляция: {reason}.");

                // Update state BEFORE running so a crash during reconcile
                // doesn't cause an infinite retry loop.
                _stateStore.Update(s =>
                {
                    s.LastReconcileUtc = now;
                    s.EarlyReconcileRequested = false;
                });

                // Stats start marker (atomic long ticks).
                System.Threading.Interlocked.Exchange(ref _statsLastReconcileStartTicks, DateTime.UtcNow.Ticks);

                EnqueueAllReconcile();

                // Registry snapshot on its own interval (default 30 days).
                _ = MaybeCaptureRegistrySnapshotAsync();

                // Clean orphan resume sidecars (older than configured days).
                if (_settings.ResumeEnabled)
                    _ = Task.Run(() => _resumeStore.CleanupOrphans(_settings.ResumeSidecarMaxAgeDays));

                // Trim working set ~2 min after the cycle settles so the idle
                // footprint stays single-digit MB.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(2), _cts.Token).ConfigureAwait(false);
                        try { using var p = Process.GetCurrentProcess(); EmptyWorkingSet(p.Handle); } catch { }
                    }
                    catch (OperationCanceledException) { }
                });

                // Roll a fresh jitter offset for the next interval.
                int nextJitter = _scheduler.AdvanceJitter();
                if (_log.TraceMode)
                {
                    DateTime nextAt = DateTime.UtcNow + TimeSpan.FromMinutes(_scheduler.BaseIntervalMinutes)
                                                      + TimeSpan.FromSeconds(nextJitter);
                    _log.Debug($"Jitter: следующая реконсиляция через " +
                               $"{(_scheduler.BaseIntervalMinutes * 60 + nextJitter) / 60.0:F1} мин " +
                               $"(offset {nextJitter:+#;-#;0}с, в {nextAt:HH:mm:ss} UTC)");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Outer safety net: any non-OCE exception would otherwise terminate
            // the worker SILENTLY while _state stayed Running.  Log + restart
            // after a back-off, but only if still Running (avoid racing Dispose).
            _log.Error("ReconcileLoopAsync упал с неожиданным исключением — перезапуск через 60 с", ex);
            try { await Task.Delay(TimeSpan.FromSeconds(60), _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException)    { return; }
            if (System.Threading.Volatile.Read(ref _state) != StateRunning) return;
            _reconcileWorker = Task.Run(ReconcileLoopAsync);
        }
    }

    // ── Filters ───────────────────────────────────────────────────────────────

    private bool IsDestinationReachable()
    {
        if (string.IsNullOrWhiteSpace(_settings.DestinationRoot)) return false;
        try
        {
            // Directory.Exists on a dead/slow SMB share can
            // block until the OS SMB timeout (~tens of seconds).  This is called
            // from the reconcile/post-sync loops, so a wedged server would stall
            // them and slow Stop.  Bound the probe: run it on the thread pool and
            // wait at most 5 s; treat a timeout as "not reachable" (the next tick
            // retries).  The orphaned probe task completes harmlessly on its own.
            var probe = Task.Run(() => Directory.Exists(_settings.DestinationRoot));
            if (probe.Wait(TimeSpan.FromSeconds(5)))
                return probe.Result;

            if (_log.TraceMode)
                _log.Debug($"IsDestinationReachable('{_settings.DestinationRoot}'): " +
                           "проба не завершилась за 5 с — считаю недоступным.");
            return false;
        }
        catch (Exception ex)
        {
            // Don't silently swallow.  In trace mode, capture the
            // exact error so operators can distinguish DNS resolution
            // failures from permission denials from SMB session timeouts.
            // (AggregateException from the probe Task unwraps to its inner.)
            var real = ex is AggregateException ae && ae.InnerException is not null ? ae.InnerException : ex;
            if (_log.TraceMode)
                _log.Debug($"IsDestinationReachable('{_settings.DestinationRoot}'): " +
                           $"{real.GetType().Name} (HResult=0x{unchecked((uint)real.HResult):X8}): {real.Message}");
            return false;
        }
    }

    // ── System-file names that are NEVER useful to mirror ─────────────────
    // These Windows shell / build-tool artefacts waste bandwidth and can
    // contain absolute paths that are meaningless on the target machine.
    private static readonly HashSet<string> _alwaysIgnoreFileNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "desktop.ini",       // Shell folder customisation — machine-specific
            "Thumbs.db",         // Explorer thumbnail cache
            "ehthumbs.db",       // WMP / Media Centre thumbnail cache
            "ehthumbs_vista.db", // Vista variant
        };

    // Extension suffixes that must never be copied
    private static readonly string[] _alwaysIgnoreExtensions =
    {
        ".pms_tmp",    // Our own in-flight temp files
        ".lnk",        // Windows shortcuts — contain absolute local paths
        ".tmp",        // Generic temp files
    };

    // Path segments: if any component of the path matches, skip the whole subtree
    private static readonly string[] _alwaysIgnoreSegments =
    {
        // These appear on the Desktop and inside project folders
        @"\$RECYCLE.BIN\",
        @"\System Volume Information\",
    };

    private bool ShouldIgnore(string path)
    {
        // 1. Hard-exclude: our own installation directory
        foreach (string hard in _hardExcludes)
            if (path.StartsWith(hard, StringComparison.OrdinalIgnoreCase)) return true;

        // 2. Always-ignore: shell/system filenames (e.g. desktop.ini)
        string fileName = Path.GetFileName(path);
        if (_alwaysIgnoreFileNames.Contains(fileName)) return true;

        // 3. Always-ignore: specific extensions
        string ext = Path.GetExtension(path);
        foreach (string ignoredExt in _alwaysIgnoreExtensions)
            if (ext.Equals(ignoredExt, StringComparison.OrdinalIgnoreCase)) return true;

        // 4. Always-ignore: certain path segments
        foreach (string seg in _alwaysIgnoreSegments)
            if (path.Contains(seg, StringComparison.OrdinalIgnoreCase)) return true;

        // 5. User-configurable exclusions from settings.json
        //
        // Pattern boundary matching, corrected.
        //
        //'s IsSegmentMatch did `TrimEnd('\\')` on both the path and
        // the pattern, then required a `\` on each side of the match.  That
        // silently disabled every pattern with a leading `\` — `\bin\`,
        // `\obj\`, `\.vs\`, `\node_modules\`, `\.git\` — because for a path
        // like `proj\bin\Release\file.dll`, the char BEFORE the `\` of
        // `\bin` is `j` (last char of `proj`), not another `\`.  So the
        // pattern would never match.  Production log confirmed: bin/obj
        // build artefacts leaking to the destination.
        //
        // The corrected logic: patterns may carry their OWN boundary
        // markers (leading and/or trailing `\`).  If they do, we do NOT
        // require an additional separator outside them — the pattern's own
        // slash IS the boundary.  This preserves the over-match protection
        // for short patterns (`bin` still won't match `binary`) while
        // honouring the documented `\bin\` syntax users actually had in
        // their settings.json files.
        string normalized = path.Replace('/', '\\');
        foreach (string exclude in _settings.ExcludedRelativePaths)
        {
            if (string.IsNullOrWhiteSpace(exclude)) continue;
            string pat = exclude.Replace('/', '\\');
            if (pat.Length == 0) continue;
            if (IsSegmentMatch(normalized, pat)) return true;
        }
        return false;
    }

    /// <summary>
    /// True if <paramref name="pat"/> appears in <paramref name="path"/>
    /// as a complete path segment.  A leading/trailing `\` in the pattern
    /// supplies its own boundary; otherwise the surrounding char in
    /// <paramref name="path"/> must be `\` or string end.  Case-insensitive.
    ///
    /// Examples (pattern → matches):
    ///   `\bin\`               → `proj\bin\file` YES, `proj\binary\file` NO
    ///   `AppData\Local\Temp`  → `Users\X\AppData\Local\Temp\foo` YES,
    ///                           `Users\X\AppData\LocalLow\bar`   NO
    ///   `\.git\`              → `proj\.git\HEAD` YES, `proj\my.gitignore` NO
    ///
    /// Marked <c>internal</c> instead of <c>private</c> so the test project
    /// (via InternalsVisibleTo) can cover the regression directly.
    /// </summary>
    internal static bool IsSegmentMatch(string path, string pat)
    {
        if (string.IsNullOrEmpty(pat)) return false;
        bool patHasLeadSep  = pat[0] == '\\';
        bool patHasTrailSep = pat[pat.Length - 1] == '\\';

        int idx = 0;
        while ((idx = path.IndexOf(pat, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool leftOk  = patHasLeadSep
                          || idx == 0
                          || path[idx - 1] == '\\';
            bool rightOk = patHasTrailSep
                          || idx + pat.Length >= path.Length
                          || path[idx + pat.Length] == '\\';
            if (leftOk && rightOk) return true;
            idx++;
        }
        return false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private List<SyncRootSpec> GetActiveRoots()
    {
        var list = ProfileRoots.GetDefaultRoots(
            _settings.MirrorDesktop,        _settings.MirrorDocuments,
            _settings.MirrorDownloads,      _settings.MirrorPictures,
            _settings.MirrorVideos,         _settings.MirrorMusic,
            _settings.MirrorFavorites,      _settings.MirrorContacts,
            _settings.MirrorLinks,          _settings.MirrorSearches,
            _settings.MirrorSavedGames,     _settings.MirrorAppDataRoaming,
            _settings.MirrorAppDataLocal,   _settings.MirrorAppDataLocalLow);

        list.AddRange(ProfileRoots.GetCustomRoots(_settings.CustomFolderPaths));
        return list;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <summary>
    /// Disposes the controller. — Contract:
    ///
    /// <list type="bullet">
    /// <item>The caller MUST first <c>await StopAsync()</c> (or never have
    /// called <c>StartAsync</c>) before calling Dispose.  Calling Dispose
    /// while StartAsync/StopAsync is in-flight on the SAME instance is not
    /// supported.</item>
    /// <item> — Concurrent Dispose calls FROM SEPARATE PATHS (e.g.
    /// normal lifecycle Stop + OnSessionEnding bypass) ARE supported: the
    /// second call is an Interlocked-guarded no-op.</item>
    /// <item>After Dispose, this instance is permanently dead.  Internal CTS
    /// is cancelled and disposed; the bounded channel is completed.  A new
    /// <c>SyncController</c> must be constructed to resume sync — which is
    /// already the pattern <c>TrayApp.DoStartAsync</c> uses.</item>
    /// </list>
    /// </summary>
    public void Dispose()
    {
        // Idempotent: first caller wins, all subsequent calls no-op.
        // OnSessionEnding bypasses
        // the lifecycle lock for shutdown speed, and may race with an in-
        // flight DoStopAsync that's also about to call Dispose.  Without this
        // guard, the second Dispose would re-Cancel an already-disposed CTS
        // (idempotent per CTS contract, but produces a noisy ObjectDisposed-
        // Exception that the catch around Cancel just barely swallows) and
        // re-Dispose an already-disposed SemaphoreSlim.  Now there is no
        // second pass.
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
        foreach (var root in _roots.Values)
        {
            try { root.Watcher.EnableRaisingEvents = false; } catch { }
            try { root.Watcher.Dispose(); } catch { }
        }
        try { _lifecycleLock.Dispose(); } catch { }
    }

    // ── Internal types ────────────────────────────────────────────────────────

    private sealed record SyncOperation(
        SyncOperationKind Kind,
        string SourcePath,
        string DestinationPath,
        string? NewPath,
        string? NewDestinationPath);

    private enum SyncOperationKind { CreateOrChange, Delete, Rename, Reconcile }
}
