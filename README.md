# ProfileMirrorSync

[![build](https://github.com/commeta/ProfileMirrorSync/actions/workflows/build.yml/badge.svg)](https://github.com/commeta/ProfileMirrorSync/actions/workflows/build.yml)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)

A lightweight, per-user Windows tray application that **one-way mirrors a user
profile to a network share**. It is built for unattended fleet use: real-time
mirroring via `FileSystemWatcher`, a gentle background reconcile, and a traffic
shaper so it never saturates a shared uplink. The user should not feel it
running.

> 🇷🇺 Русская версия: [README.ru.md](README.ru.md)

---

## Highlights

- **One-way mirror** — the source profile is replicated to
  `\\server\share\<Machine>\<User>\…`. The newest version of a file overwrites
  the server copy; files deleted at the source are removed on the destination.
  This is a *mirror*, not a versioned backup (pair it with server-side
  snapshots or the built-in post-sync archiver hook for history).
- **Real-time + reconcile** — `FileSystemWatcher` mirrors changes as they
  happen; a periodic background reconcile (default once per day) catches
  anything the watcher missed.
- **Bandwidth shaping** — a token-bucket limiter spreads I/O over time. 1 Mbit/s baseline, 3 Mbit/s turbo
  burst. All delays are configurable.
- **Background priority** — copy work runs at the lowest CPU/I/O priority and
  trims its working set, so interactive apps stay responsive.
- **Large-file resume** — interrupted copies of big files resume from where they
  stopped (byte-range sidecars).
- **Multi-user** — each Windows user gets independent settings and sync state
  under `%LocalAppData%`. The same shared executable serves every user.
- **No domain required** — runs per-user with `asInvoker` (no elevation), no
  Group Policy, no Active Directory.

## Status

`net9.0-windows` · WinForms · self-contained build optional · 150+ unit tests.
Reviewed for enterprise pilot readiness.

---

## Requirements

- Windows 10 / 11
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
  (or build self-contained)

## Build

```bat
dotnet restore src/ProfileMirrorSync.csproj
dotnet build  src/ProfileMirrorSync.csproj -c Release
dotnet test   tests/ProfileMirrorSync.Tests.csproj -c Release
dotnet publish src/ProfileMirrorSync.csproj -c Release -r win-x64 --self-contained true -o publish/
```

Or run `build.bat` (does all four steps and produces `publish/ProfileMirrorSync.exe`).

## Install (optional)

After `build.bat`, run `install.bat` **as Administrator** to deploy from the
build output:

- copies `publish\` → `%ProgramFiles%\ProfileMirrorSync` (standard, all users)
- creates a public desktop shortcut
- registers a logon task so the tray app starts for every user
- launches the program

`uninstall.bat` (also as Administrator) reverses this: it stops the task and
process, removes the binaries and shortcut, and offers to delete the current
user's settings. Each user's per-user data under `%LocalAppData%` is left in
place unless explicitly removed.

## Run

Launch `ProfileMirrorSync.exe`. It starts minimized to the tray. Right-click the
tray icon → **Параметры…** (Settings) to configure the destination share and
tuning, then **Запустить** (Start).

The executable can run from any folder. It writes:

| Data | Location |
|------|----------|
| Settings, sync state, resume sidecars, logs | `%LocalAppData%\ProfileMirrorSync` (per user) |

## Repository layout

```
src/     application sources (Program, Services, UI, Models, .csproj, manifest)
tests/   xUnit test project
docs/    architecture and design notes
.github/ CI workflow
```

---

## How it works

```
FileSystemWatcher ──debounce──> bounded queue ──> throttled copier ──> \\server\share
                                     │                  (token-bucket rate limit)
        periodic reconcile ──────────┘  (full source⇄destination sweep, catches misses)
```

- **Reconcile** runs in three passes: copy new/changed files, delete orphans,
  then propagate directory timestamps — each pass throttled with configurable
  per-file and per-batch delays.
- **Turbo** temporarily raises the bandwidth cap (and drops the artificial
  pauses) when a real-time event-storm floods the queue, then returns to the
  baseline once it drains.
- **Post-sync hook** can run an external archiver/script (7-Zip, WinRAR, ZIP,
  Robocopy, retention-prune) on its own schedule, for server-side history.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full design.

## Configuration

All tuning is in the Settings dialog (each field has an ⓘ tooltip). Key knobs:
baseline/turbo bandwidth, reconcile interval & jitter, per-file/batch delays,
queue capacity, event triggers (wake/unlock/logon), large-file resume, the
empty-source deletion guard, and the post-sync archiver presets.

## Contributing

Issues and pull requests are welcome. Please keep changes small and focused, run
the test suite (`dotnet test`) before submitting, and match the existing code
style.

## License

[GPL-3.0](LICENSE) © ProfileMirrorSync.
