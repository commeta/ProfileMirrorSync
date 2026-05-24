# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and the project aims to follow
[Semantic Versioning](https://semver.org/).

## [2.5.3]

### Added
- Multi-user support: per-user settings and sync state under
  `%LocalAppData%\ProfileMirrorSync`; the same shared executable serves every
  Windows user. Existing shared settings are migrated on first run.

### Changed
- Logs are now written next to the executable (`Logs\`), with an automatic
  per-user fallback when the program directory is not writable.
- Turbo mode now suppresses the artificial inter-file/batch delays while active,
  so the raised bandwidth cap is actually usable (the rate cap still applies).
- Removed the "open logs folder" item from the tray menu.
- Settings tooltips word-wrap so long hints no longer stretch off-screen.

### Fixed
- Registry snapshots are skipped quietly when the destination is offline (no
  more warning/stack-trace spam on every start/stop during an outage).
- Reconcile pass 3 no longer rewrites a directory's timestamp when it already
  matches the source, eliminating wasted metadata round-trips on a stable tree.

## [2.5.2]

### Added
- Per-parameter ⓘ tooltips in Settings (replacing inline help text).
- Option to disable the scheduled reconcile (real-time mirroring and the
  data-safety early reconcile continue to run).
- Post-sync archiver presets: 7-Zip, WinRAR, ZIP, Robocopy mirror,
  retention-prune, and archive-then-prune.

### Changed
- Renamed the "Archive" settings tab.

## [2.5.1]

### Changed
- Retuned turbo defaults for the 10-PC / 30-Mbit scenario (3 Mbit/s at a
  1000-file backlog) and made turbo react to real-time event-storms rather than
  the scheduled reconcile by default.
- Event-driven reconcile triggers (wake / unlock / logon) are now configurable.
- Lowered the process priority class for a lighter footprint.

### Fixed
- Empty-orphan-directory removal is now throttled like every other reconcile
  loop.
- Post-sync child process output is drained to prevent a pipe-buffer deadlock.
- `IsDestinationReachable` is bounded so a dead share can't stall the loops.

## [2.5.0]

### Added
- Post-sync external program hook (archiver/script) on an independent schedule.
- Large-file byte-range resume.
- Optional empty-source deletion guard.

### Changed
- Decomposed the orchestrator into focused services (rate limiter, scheduler,
  turbo controller, log mirror, post-sync runner).
- Removed the legacy security-monitoring subsystem entirely.

### Notes
- Pre-2.5.0 history (the 2.4.x hardening series: concurrency fixes, atomic
  state writes, queue backpressure, throttling, decoupled heavy operations) is
  preserved in version control and summarised above where still relevant.

[2.5.3]: https://github.com/commeta/ProfileMirrorSync/releases
[2.5.2]: https://github.com/commeta/ProfileMirrorSync/releases
[2.5.1]: https://github.com/commeta/ProfileMirrorSync/releases
[2.5.0]: https://github.com/commeta/ProfileMirrorSync/releases
