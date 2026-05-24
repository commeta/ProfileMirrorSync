using System.Text.Json;
using ProfileMirrorSync.Models;

namespace ProfileMirrorSync.Services;

/// <summary>
/// Owns %ProgramData%\ProfileMirrorSync\monitor_state.json — a small JSON file
/// holding SyncController's reconcile/log-mirror/registry/post-sync timestamps
/// and the early-reconcile request flag.  (Filename kept from earlier versions
/// for upgrade continuity; despite the name it no longer relates to the removed
/// security monitor.)
///
/// Design notes:
///   • Thread-safe via internal lock.  Read/Update calls fast (single load
///     per process, kept in memory; saves on Update).
///   • Atomic save via temp+rename (same pattern as SettingsStore).
///   • The file is small (a few hundred bytes); rewriting on each change is
///     fine — happens at most a few times per hour.
/// </summary>
public sealed class PersistentStateStore
{
    private readonly string _path;
    private readonly Logger _log;
    private readonly object _lock = new();
    private PersistentState _state;

    public PersistentStateStore(Logger log)
    {
        _log = log;
        // per-user state (was shared %ProgramData%).
        _path = Path.Combine(AppPaths.DataDirectory, "monitor_state.json");
        _state = LoadFromDisk();
    }

    /// <summary>Read a snapshot of the current state.  Safe to call from any thread.</summary>
    public PersistentState Snapshot()
    {
        lock (_lock)
        {
            return new PersistentState
            {
                LastReconcileUtc             = _state.LastReconcileUtc,
                LastReconcileCompletedUtc    = _state.LastReconcileCompletedUtc,
                EarlyReconcileRequested      = _state.EarlyReconcileRequested,
                LastEarlyReconcileRequestUtc = _state.LastEarlyReconcileRequestUtc,
                LastLogMirrorUtc             = _state.LastLogMirrorUtc,
                LastRegistrySnapshotUtc      = _state.LastRegistrySnapshotUtc,
                LastPostSyncRunUtc           = _state.LastPostSyncRunUtc,
            };
        }
    }

    /// <summary>
    /// Apply a mutation under the lock and atomically persist.
    /// The mutator receives the in-memory state and may modify it in place.
    ///
    /// Disk I/O was moved OUTSIDE the lock to keep
    /// concurrent <see cref="Snapshot"/> calls non-blocking.
    ///
    /// REVERTED: disk I/O is now inside the lock again.
    /// The L-2 optimisation opened a tiny window where two concurrent Update()
    /// calls could race at the file level, allowing the loser's JSON (older
    /// snapshot of state) to overwrite the winner's on disk.  Memory state
    /// stays correct, but a process crash between the loser's Move and the
    /// next Update would lose the winner's mutation on disk.  The cost is
    /// trivial — Update() is called a few times an hour, JSON write ≈1 ms on
    /// local SSD; Snapshot() is called once a minute by the reconcile loop
    /// and a few times by the stats window — the lock contention is nil in
    /// practice.  Correctness over micro-optimisation.
    /// </summary>
    public void Update(Action<PersistentState> mutator)
    {
        lock (_lock)
        {
            mutator(_state);
            string json = JsonSerializer.Serialize(_state, JsonOpts);
            SaveJsonToDisk(json);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private PersistentState LoadFromDisk()
    {
        if (!File.Exists(_path)) return new PersistentState();
        try
        {
            string json = File.ReadAllText(_path);
            // New format: PersistentState object.  Any legacy "Cooldowns" key
            // from pre- files is silently ignored (the property no longer
            // exists on the model).
            var parsed = JsonSerializer.Deserialize<PersistentState>(json);
            if (parsed is not null) return parsed;

            _log.Warn($"PersistentStateStore: не удалось разобрать {_path}, используются значения по умолчанию");
            return new PersistentState();
        }
        catch (Exception ex)
        {
            _log.Warn($"PersistentStateStore: ошибка чтения {_path}: {ex.Message}", ex);
            return new PersistentState();
        }
    }

    private void SaveJsonToDisk(string json)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp  = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Debug($"PersistentStateStore: ошибка записи {_path}: {ex.Message}");
        }
    }
}
