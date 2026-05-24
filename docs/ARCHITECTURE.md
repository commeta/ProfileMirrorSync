# Architecture

ProfileMirrorSync is a single-process, per-user Windows tray application
(`net9.0-windows`, WinForms). This document describes the system as it currently
stands.

## Data flow

```
                 debounce            bounded channel          token-bucket
FileSystemWatcher ────────> dedupe ───────────────> worker ──────────────> \\server\share
   (real time)              queue    (backpressure)  (throttled copier)

ReconcileScheduler ─── periodic full sweep (source ⇄ destination) ── catches missed events
```

- **Real-time path.** `FileSystemWatcher` raises change events. They are
  debounced (a file is only copied once it stops changing), deduplicated, and
  pushed onto a bounded channel. On overflow, `Created`/`Changed` events are
  dropped (the next reconcile will pick them up) while `Delete`/`Rename` are
  retained so deletions are never lost.
- **Reconcile path.** On a timer (default 24 h, with per-machine jitter to
  desynchronise a fleet) a full sweep walks the source and destination in three
  passes: (1) copy new/changed files, (2) delete orphans, (3) propagate
  directory timestamps. Every I/O-producing loop is throttled with configurable
  per-file and per-batch delays. A queue-pressure "early" reconcile acts as a
  data-safety valve during event-storms even when the periodic sweep is
  disabled.
- **Copier.** A token-bucket rate limiter caps bandwidth; the copy runs on a
  background-priority thread (low CPU and I/O). Large files use byte-range
  resume sidecars so an interrupted copy continues from where it stopped.

## Components (`Services/`)

| Component | Responsibility |
|-----------|----------------|
| `SyncController` | Orchestrator: lifecycle state machine, queue, background loops |
| `FileMirror` | The three-pass reconcile and per-file mirror logic |
| `ThrottledFileCopier` | Bandwidth-limited copy with resume support |
| `ByteRateLimiter` | Token-bucket bandwidth shaper (runtime-adjustable) |
| `ReconcileScheduler` | Computes when the next reconcile is due (interval + jitter) |
| `TurboModeController` | Raises the rate cap during a real-time backlog, restores it after |
| `LogMirrorService` | Copies closed log files to the destination once a day |
| `RegistrySnapshotService` | Periodic `reg.exe` export of selected HKCU keys |
| `PostSyncRunner` + `PostSyncPresets` | Runs an external archiver/script on its own schedule |
| `SettingsStore` | Loads/saves `settings.json`, migration, atomic writes |
| `PersistentStateStore` | Small JSON of reconcile/log/registry/post-sync timestamps |
| `ResumeStateStore` | Byte-range resume sidecars for large files |
| `AppPaths` | Single source of truth for data and log locations |
| `Logger` | File logger with levels and retention |

UI (`UI/`): `TrayApp` (tray icon, menu, lifecycle), `SettingsForm`,
`StatsForm`, `LogViewerForm`.

## File locations

| Data | Location |
|------|----------|
| `settings.json`, `monitor_state.json`, `resume/`, `Logs/` | `%LocalAppData%\ProfileMirrorSync` (per user) |

Each Windows user runs the same shared executable but keeps independent
settings and state. On first run, an existing shared `%ProgramData%` settings
file (from older single-location builds) is adopted into the per-user location.

## Concurrency model

- A four-state lifecycle (`Stopped → Starting → Running → Stopping`) guarded by
  `Interlocked` CAS and a `SemaphoreSlim`, with transactional rollback if a
  start fails. Each Start creates a fresh controller instance; its
  `CancellationTokenSource` is single-use.
- Background loops (reconcile, post-sync, log-mirror) are independent tasks that
  poll, respect cancellation, and restart themselves on unexpected exceptions
  only while the controller is running.
- Counters (high-water marks, drop counts) use lock-free CAS; the dedupe set is
  a `ConcurrentDictionary`.

## Reliability

- Settings and state are written atomically (temp file + rename) to survive
  power loss mid-write; a corrupt `settings.json` is backed up and replaced with
  defaults rather than lost silently.
- The destination is probed for reachability before reconcile, post-sync, and
  registry snapshots; an outage logs a single warning instead of repeating.
- An optional empty-source deletion guard refuses to delete destination files
  when the source unexpectedly enumerates as empty (profile not loaded,
  placeholder files, AV quarantine), protecting the only server-side copy.

## Tuning defaults

Baseline 1 Mbit/s, turbo 3 Mbit/s engaging at a 1000-file backlog, 24 h
reconcile interval with 30–60 % jitter, per-file/batch delays enabled in every
reconcile pass, lowest CPU/I/O priority. All values are adjustable in Settings.
