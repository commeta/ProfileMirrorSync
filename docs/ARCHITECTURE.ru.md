# Архитектура ProfileMirrorSync

> Версия документа соответствует кодовой базе **v2.5.3**  
> Платформа: `net9.0-windows` · WinForms · one-file WinExe · `win-x64`  
> Нет сторонних NuGet-зависимостей

---

## Содержание

1. [Назначение и контекст](#1-назначение-и-контекст)  
2. [Птичий взгляд — обзорная схема слоёв](#2-птичий-взгляд--обзорная-схема-слоёв)  
3. [Компонентная диаграмма — модули и зависимости](#3-компонентная-диаграмма--модули-и-зависимости)  
4. [Диаграмма классов по ключевым связям](#4-диаграмма-классов-по-ключевым-связям)  
5. [Потоки данных в реальном времени (ASCII)](#5-потоки-данных-в-реальном-времени-ascii)  
6. [Sequence diagram — один файл: FSW → сетевая папка](#6-sequence-diagram--один-файл-fsw--сетевая-папка)  
7. [Sequence diagram — цикл плановой реконсиляции](#7-sequence-diagram--цикл-плановой-реконсиляции)  
8. [State machine — жизненный цикл контроллера](#8-state-machine--жизненный-цикл-контроллера)  
9. [Deployment diagram — топология развёртывания](#9-deployment-diagram--топология-развёртывания)  
10. [Модель параллелизма](#10-модель-параллелизма)  
11. [Подсистема копирования файлов (ThrottledFileCopier)](#11-подсистема-копирования-файлов-throttledfilecopier)  
12. [Алгоритм реконсиляции (три прохода)](#12-алгоритм-реконсиляции-три-прохода)  
13. [Планировщик реконсиляции и адаптивный триггер](#13-планировщик-реконсиляции-и-адаптивный-триггер)  
14. [Расположение файлов и пути данных](#14-расположение-файлов-и-пути-данных)  
15. [Надёжность и обработка ошибок](#15-надёжность-и-обработка-ошибок)  
16. [Фильтрация и исключение файлов](#16-фильтрация-и-исключение-файлов)  
17. [Периодические фоновые задачи](#17-периодические-фоновые-задачи)  
18. [Многопользовательская модель](#18-многопользовательская-модель)  
19. [Настройки и значения по умолчанию](#19-настройки-и-значения-по-умолчанию)  

---

## 1. Назначение и контекст

**ProfileMirrorSync** — однопроцессное Windows-приложение в системном трее. Его задача: непрерывно зеркалировать папки профиля пользователя (`Desktop`, `Documents`, `Downloads`, `AppData/…` и произвольные папки) на сетевой ресурс (SMB-шара, mapped drive), работая незаметно для пользователя.

Два взаимодополняющих механизма защиты данных:

```
┌──────────────────────────────────────────────────────────────┐
│  Реальное время                   Плановая реконсиляция      │
│  (FileSystemWatcher)              (периодический обход)      │
│                                                              │
│  • Реагирует на каждое изменение  • Ловит пропущенные        │
│  • Debounce + deduplicate         • Ликвидирует «сироты»     │
│  • Bounded channel ← back-press.  • Распространяет mtime     │
│  • Turbo при event-storm          • Работает ≥ раз/сутки     │
└──────────────────────────────────────────────────────────────┘
```

**Ключевые нефункциональные цели:**

| Цель | Механизм |
|------|----------|
| Нет ощутимой нагрузки на ПК | `ProcessPriorityClass.Idle`, IO-поток на фоне |
| Нет спайков на сетевом аплинке | Token-bucket лимитер, turbo только при шторме |
| Устойчивость к перебоям сети | Проба `Directory.Exists` с таймаутом 5 с, retry-with-backoff |
| Атомарные обновления конфигурации | Запись в `.tmp` → rename |
| Продолжение прерванных копий | Byte-range resume сайдкары (SHA-256 головы файла) |
| Один экземпляр на сессию | `Local\` именованный мьютекс |

---

## 2. Птичий взгляд — обзорная схема слоёв

```mermaid
flowchart TB
    subgraph UI["🖥  UI Layer  (UI/)"]
        direction LR
        TrayApp["TrayApp<br>(ApplicationContext)"]
        SettingsForm["SettingsForm"]
        StatsForm["StatsForm"]
        LogViewerForm["LogViewerForm"]
    end

    subgraph Orchestration["⚙  Orchestration  (Services/)"]
        direction TB
        SC["SyncController<br>[lifecycle · queue · FSW · dispatch]"]
    end

    subgraph RealTime["⚡  Real-time pipeline"]
        direction LR
        FSW["FileSystemWatcher<br>×N корневых папок"]
        DEB["Debounce<br>(Task.Delay + CTS)"]
        DEDUP["ConcurrentDictionary<br> dedupe set"]
        CH["BoundedChannel<br>&#60;SyncOperation&#62;<br>(back-pressure)"]
        WORKER["Queue worker<br>ProcessQueueAsync()"]
    end

    subgraph BackgroundLoops["🔄  Background loops  (Tasks)"]
        direction LR
        RECON["ReconcileLoopAsync<br>(60 s poll)"]
        POSTSYNC["PostSyncLoopAsync<br>(independent timer)"]
        LOGMIRROR["LogMirrorService<br>(1×/day)"]
        REGSNAPSHOT["RegistrySnapshotService<br>(configurable interval)"]
    end

    subgraph CoreServices["🔧  Core services"]
        direction LR
        FM["FileMirror<br>(3-pass reconcile)"]
        TFC["ThrottledFileCopier<br>(async chunk + resume)"]
        BRL["ByteRateLimiter<br>(token-bucket)"]
        TURBO["TurboModeController"]
        RSCHED["ReconcileScheduler<br>(jitter + early trigger)"]
    end

    subgraph State["💾  State & persistence"]
        direction LR
        SS["SettingsStore<br>(settings.json)"]
        PSS["PersistentStateStore<br>(monitor_state.json)"]
        RSS["ResumeStateStore<br>(resume/*.json)"]
        LOG["Logger<br>(daily rolling)"]
    end

    subgraph FileSystem["📁  File system"]
        direction LR
        SRC["Source folders<br>%USERPROFILE%\\…"]
        DST["Network share<br>\\\\server\\share\\<br>Machine\\User\\"]
    end

    TrayApp -->|"owns"| SC
    TrayApp -->|"opens"| SettingsForm
    TrayApp -->|"opens"| StatsForm
    TrayApp -->|"opens"| LogViewerForm
    TrayApp -->|"GetStatsSnapshot()"| SC

    SC --> FSW
    FSW -->|"events"| DEB
    DEB --> DEDUP
    DEDUP --> CH
    CH --> WORKER

    SC --> RECON
    SC --> POSTSYNC
    SC --> LOGMIRROR
    SC --> REGSNAPSHOT

    WORKER -->|"DispatchAsync()"| FM
    RECON -->|"EnqueueAllReconcile()"| CH
    FM --> TFC
    TFC --> BRL
    TURBO -->|"UpdateRate()"| BRL
    RSCHED -->|"Evaluate()"| RECON

    SC --> TURBO
    SC --> RSCHED

    FM -->|"reads/writes"| SRC
    FM -->|"reads/writes"| DST
    TFC -->|"sidecar"| RSS

    SC --> PSS
    SC --> SS
    SC --> LOG
```

---

## 3. Компонентная диаграмма — модули и зависимости

```mermaid
graph LR
    subgraph "UI/"
        TA[TrayApp]
        SF[SettingsForm]
        STF[StatsForm]
        LVF[LogViewerForm]
    end

    subgraph "Services/ — Orchestration"
        SC[SyncController]
    end

    subgraph "Services/ — I/O pipeline"
        FM[FileMirror]
        TFC[ThrottledFileCopier]
        BRL[ByteRateLimiter]
    end

    subgraph "Services/ — Scheduling & control"
        TMC[TurboModeController]
        RS[ReconcileScheduler]
        LMS[LogMirrorService]
        RSS2[RegistrySnapshotService]
        PSR[PostSyncRunner]
        PSP[PostSyncPresets]
    end

    subgraph "Services/ — State"
        APATHS[AppPaths]
        SETS[SettingsStore]
        PSS[PersistentStateStore]
        RSStore[ResumeStateStore]
        LOG2[Logger]
    end

    subgraph "Services/ — Roots"
        PR[ProfileRoots]
    end

    subgraph "Models/"
        AS[AppSettings]
        SM[StateModels<br>PersistentState<br>ResumeState]
    end

    TA --> SC
    TA --> SETS
    TA --> PSS
    TA --> LOG2
    SF --> SETS
    SF --> PSP
    STF --> SC
    LVF --> LOG2

    SC --> FM
    SC --> BRL
    SC --> TMC
    SC --> RS
    SC --> LMS
    SC --> RSS2
    SC --> PSR
    SC --> PSS
    SC --> RSStore
    SC --> LOG2
    SC --> PR
    SC --> SETS

    FM --> TFC
    FM --> LOG2
    TFC --> BRL
    TFC --> RSStore
    TFC --> LOG2

    TMC --> BRL
    LMS --> TFC
    LMS --> PSS

    SETS --> APATHS
    PSS --> APATHS
    RSStore --> APATHS
    LOG2 --> APATHS

    SC --> AS
    SETS --> AS
    PSS --> SM
    RSStore --> SM

    classDef core fill:#2d4a7a,color:#fff,stroke:#1a3055
    classDef ui fill:#4a6741,color:#fff,stroke:#2d4a24
    classDef state fill:#7a4a2d,color:#fff,stroke:#5a3020
    classDef model fill:#4a4a4a,color:#fff,stroke:#2d2d2d
    class SC,FM,TFC,BRL core
    class TA,SF,STF,LVF ui
    class SETS,PSS,RSStore,LOG2,APATHS state
    class AS,SM model
```

---

## 4. Диаграмма классов по ключевым связям

```mermaid
classDiagram
    class SyncController {
        -AppSettings _settings
        -Logger _log
        -FileMirror _mirror
        -ByteRateLimiter _rateLimiter
        -Channel~SyncOperation~ _queue
        -ConcurrentDictionary _dedupe
        -ConcurrentDictionary _roots
        -ConcurrentDictionary _debouncers
        -int _state [0..3]
        -SemaphoreSlim _lifecycleLock
        -TurboModeController _turbo
        -LogMirrorService _logMirror
        -ReconcileScheduler _scheduler
        -PersistentStateStore _stateStore
        -ResumeStateStore _resumeStore
        +bool IsRunning
        +StartAsync() Task
        +StopAsync() Task
        +GetStatsSnapshot() StatsSnapshot
        +TriggerResumeReconcile()
        -ProcessQueueAsync() Task
        -ReconcileLoopAsync() Task
        -PostSyncLoopAsync() Task
        -Schedule(op)
        -Enqueue(op)
        -EnqueueAllReconcile()
        -IsDestinationReachable() bool
        -ShouldIgnore(path) bool
        +IsSegmentMatch(path,pat)$ bool
    }

    class FileMirror {
        -Logger _log
        -ThrottledFileCopier _copier
        -int _retryCount
        -bool _deletionSafetyGuard
        +MirrorCreateOrChangeAsync() Task~long~
        +MirrorDeleteAsync() Task~bool~
        +MirrorRenameAsync() Task
        +ReconcileRootAsync() Task~ReconcileSummary~
        -SafeEnumerateFiles(root) IEnumerable
        -IsUpToDate(src,dst) bool
        -TryPropagateDirectoryTimestamps()
    }

    class ThrottledFileCopier {
        -ByteRateLimiter _limiter
        -Logger _log
        -ResumeStateStore _resume
        -bool _resumeEnabled
        -long _resumeMinBytes
        -bool _lowerIoPriority
        -FilePublishMode _publishMode
        +CopyAsync(src,dst,ct) Task
        -CopyWithRetryAsync() Task
        -CopyOnceAsync() Task
        -TryPublishRename() bool
    }

    class ByteRateLimiter {
        -long _bytesPerSecond
        -long _tokens
        -long _lastTick
        +long CurrentBitsPerSecond
        +int MaxBurstBytes
        +UpdateRate(bitsPerSecond)
        +WaitAsync(bytes,ct) Task
        -Refill()
        -SetRate(bitsPerSecond)
    }

    class TurboModeController {
        -AppSettings _settings
        -ByteRateLimiter _rateLimiter
        -Func~int~ _pendingCount
        -bool _active
        +bool IsActive
        +MaybeActivate(pending)
        +MaybeDeactivate()
        +Reset()
    }

    class ReconcileScheduler {
        -AppSettings _settings
        -Random _rng
        +int BaseIntervalMinutes
        +int JitterPercent
        +NextDue(anchor) DateTime
        +Evaluate(state,now) ReconcileDecision
        +AdvanceJitter() int
        +IsEarlyTriggerAllowed() bool
        +ComputeJitterOffsetSec()$ int
    }

    class PersistentStateStore {
        -string _path
        -PersistentState _state
        -object _lock
        +Snapshot() PersistentState
        +Update(mutator)
        -SaveJsonToDisk(json)
        -LoadFromDisk() PersistentState
    }

    class ResumeStateStore {
        -string _dir
        +SidecarPath(srcPath) string
        +TryLoad(srcPath) ResumeState?
        +Save(state, emitTrace)
        +Clear(srcPath)
        +CleanupOrphans(maxAgeDays)
    }

    class SettingsStore {
        -string _path
        +Load() AppSettings
        +Save(settings)
        -Migrate(loaded) AppSettings
    }

    class Logger {
        -string _logDirectory
        -AppLogLevel _minLevel
        -volatile bool _traceMode
        -Queue~LogEntry~ _history
        +Debug(msg)
        +Info(msg)
        +Warn(msg, ex?)
        +Error(msg, ex)
        +Flush()
        +string LogDirectory
    }

    class AppSettings {
        +string DestinationRoot
        +int MaxBandwidthBitsPerSecond
        +bool ReconcileEnabled
        +int ReconcileIntervalMinutes
        +int ReconcileJitterPercent
        +bool TurboFirstRunEnabled
        +int TurboThresholdFiles
        +bool ResumeEnabled
        +FilePublishMode PublishMode
        +bool PostSyncEnabled
        +List~string~ ExcludedRelativePaths
        <<model>>
    }

    class PersistentState {
        +DateTime? LastReconcileUtc
        +DateTime? LastReconcileCompletedUtc
        +bool EarlyReconcileRequested
        +DateTime? LastEarlyReconcileRequestUtc
        +DateTime? LastLogMirrorUtc
        +DateTime? LastRegistrySnapshotUtc
        +DateTime? LastPostSyncRunUtc
        <<model>>
    }

    SyncController --> FileMirror : owns
    SyncController --> ByteRateLimiter : owns
    SyncController --> TurboModeController : owns
    SyncController --> ReconcileScheduler : owns
    SyncController --> PersistentStateStore : owns
    SyncController --> ResumeStateStore : owns
    SyncController --> Logger : injects
    SyncController --> AppSettings : reads
    FileMirror --> ThrottledFileCopier : owns
    ThrottledFileCopier --> ByteRateLimiter : borrows ref
    ThrottledFileCopier --> ResumeStateStore : borrows ref
    TurboModeController --> ByteRateLimiter : calls UpdateRate
    ReconcileScheduler --> PersistentState : reads (snapshot)
    PersistentStateStore --> PersistentState : owns
    SettingsStore --> AppSettings : produces
```

---

## 5. Потоки данных в реальном времени (ASCII)

```
╔══════════════════════════════════════════════════════════════════════════╗
║  ИСТОЧНИК (локальный профиль)     БУФЕР            НАЗНАЧЕНИЕ (SMB)      ║
╠══════════════════════════════════════════════════════════════════════════╣
║                                                                          ║
║  %USERPROFILE%\Desktop            ┌─ debounce     \\server\share\        ║
║  %USERPROFILE%\Documents     FSW  │  700 ms CTS   MACHINE\USER\          ║
║  %USERPROFILE%\Downloads ────────►│               Desktop\               ║
║  %APPDATA%\…              events  │  dedupe set   Documents\             ║
║  <CustomFolders>                  │  (ConcDict)   Downloads\             ║
║                                   │               …                      ║
║  ── REAL-TIME PATH ──────────────►│◄──────────────── priority drop ──    ║
║                                   │  BoundedChannel                      ║
║                                   │  capacity=N (default 1000)           ║
║                                   │                                      ║
║  Created  → CreateOrChange ───────┤  FULL? → drop + log 30s              ║
║  Changed  → CreateOrChange ───────┤  Delete/Rename → retry 5s write      ║
║  Deleted  → Delete ───────────────┤                                      ║
║  Renamed  → Rename ───────────────┤                                      ║
║  Error    → Reconcile ────────────┤                                      ║
║                                   ▼                                      ║
║                          ProcessQueueAsync()                             ║
║                          [single Task.Run worker]                        ║
║                                   │                                      ║
║                          DispatchAsync(op)                               ║
║                          ┌────────┴───────────────────────┐              ║
║                          │  CreateOrChange → FileMirror   │              ║
║                          │  Delete         → FileMirror   │              ║
║                          │  Rename         → FileMirror   │              ║
║                          │  Reconcile      → RunReconcile │              ║
║                          └────────────────────────────────┘              ║
║                                                                          ║
║  ── RECONCILE PATH ──────────────────────────────────────────────────    ║
║                                                                          ║
║  ReconcileLoopAsync() ──poll 60s──► ReconcileScheduler.Evaluate()        ║
║  IsDue?                                                                  ║
║  └── YES → EnqueueAllReconcile() ──► BoundedChannel (kind=Reconcile)     ║
║                                                                          ║
║  FileMirror.ReconcileRootAsync()                                         ║
║  ├── Pass 1: copy new/changed  (rate-limited, batched)                   ║
║  ├── Pass 2: delete orphans    (rate-limited, batched)                   ║
║  └── Pass 3: propagate dir timestamps  (bottom-up)                       ║
║                                                                          ║
╠══════════════════════════════════════════════════════════════════════════╣
║  TOKEN-BUCKET   baseline 1 Mbit/s  ─── turbo 3 Mbit/s (≥1000 pending)    ║
╚══════════════════════════════════════════════════════════════════════════╝
```

### Анатомия `SyncOperation`

```
SyncOperation {
  Kind:              CreateOrChange | Delete | Rename | Reconcile
  SourcePath:        полный путь источника
  DestinationPath:   полный путь назначения (pre-computed)
  NewPath?:          только для Rename (новое имя в источнике)
  NewDestinationPath?: только для Rename (новый путь в назначении)
}
```

Ключ дедупликации:
- Rename: `"{SourcePath}|{NewPath}"`
- Всё остальное: `"{SourcePath}"`

---

## 6. Sequence diagram — один файл: FSW → сетевая папка

```mermaid
sequenceDiagram
    autonumber
    participant OS as Windows OS
    participant FSW as FileSystemWatcher
    participant SC as SyncController
    participant DEB as Debouncer<br/>(Task.Delay)
    participant CH as BoundedChannel
    participant WRK as QueueWorker
    participant FM as FileMirror
    participant TFC as ThrottledFileCopier
    participant BRL as ByteRateLimiter
    participant NET as SMB Share<br/>\\server\share\...

    OS->>FSW: Changed(path)
    FSW->>SC: EnqueueChange(CreateOrChange, spec, destRoot, path)
    SC->>SC: ShouldIgnore(path)? → NO
    SC->>DEB: Schedule(op) — CancellationTokenSource создан
    Note over DEB: Предыдущий CTS для этого пути отменяется<br/>(debounce reset при rapid-fire)
    DEB->>DEB: await Task.Delay(700ms, cts.Token)
    DEB->>SC: Enqueue(op)
    SC->>SC: _dedupe.TryAdd(key)? → YES (новый)
    SC->>CH: TryWrite(op) → OK (очередь не полна)
    SC->>SC: _highWaterMark update (CAS loop)
    SC->>SC: TurboModeController.MaybeActivate(depth)

    WRK->>CH: WaitToReadAsync() → op ready
    WRK->>FM: MirrorCreateOrChangeAsync(src, dst, ct)
    FM->>FM: Directory.Exists(src)? → NO (файл)
    FM->>FM: File.Exists(src)? → YES
    FM->>FM: IsUpToDate(src, dst)? → NO (mtime/size diff)
    FM->>TFC: CopyAsync(src, dst, ct)
    TFC->>TFC: Snapshot src timestamps (CreationTime, LastWriteTime)
    TFC->>TFC: PublishMode == DirectWrite?
    TFC->>TFC: ClearReadOnly(dst)
    TFC->>TFC: CopyWithRetryAsync(src, dst, ct, allowResume=true)
    TFC->>TFC: CopyOnceAsync → ArrayPool.Rent(64KB)
    TFC->>TFC: resume sidecar check (TryLoad)
    loop Каждый chunk (≤64KB)
        TFC->>BRL: WaitAsync(chunkSize, ct)
        Note over BRL: Token-bucket: если токенов достаточно<br/>→ списать и вернуть немедленно<br/>иначе await Task.Delay(waitMs)
        BRL-->>TFC: ready
        TFC->>NET: WriteAsync(buffer, offset, count)
        TFC->>TFC: FlushAsync()
        TFC->>TFC: resume sidecar save (каждые ~1 MB)
    end
    TFC->>NET: SetCreationTimeUtc / SetLastWriteTimeUtc
    TFC->>TFC: resume sidecar Clear (copy complete)
    TFC-->>FM: completed
    FM-->>WRK: bytes copied
    WRK->>SC: Interlocked.Increment(_statsFilesCopiedTotal)
    WRK->>SC: FileProcessed?.Invoke(path)
    WRK->>SC: _dedupe.TryRemove(key)
    WRK->>SC: TurboModeController.MaybeDeactivate()
```

---

## 7. Sequence diagram — цикл плановой реконсиляции

```mermaid
sequenceDiagram
    autonumber
    participant RCL as ReconcileLoopAsync
    participant SCHED as ReconcileScheduler
    participant PSS as PersistentStateStore
    participant SC as SyncController
    participant CH as BoundedChannel
    participant WRK as QueueWorker
    participant FM as FileMirror
    participant TFC as ThrottledFileCopier
    participant NET as SMB Share
    participant LMS as LogMirrorService
    participant REG as RegistrySnapshotService

    loop каждые 60 секунд
        RCL->>PSS: Snapshot()
        RCL->>SCHED: Evaluate(state, now)
        Note over SCHED: NextDue = LastReconcileUtc<br/>+ BaseInterval + jitterSec
        alt не пора
            SCHED-->>RCL: IsDue=false, continue
        else пора (по времени или early-trigger)
            SCHED-->>RCL: IsDue=true, DueByEarly=?
            RCL->>RCL: IsDestinationReachable()?
            alt сеть недоступна
                RCL->>RCL: log Warn (dedup per-outage)
                RCL-->>RCL: continue (без advance anchor)
            else сеть доступна
                RCL->>PSS: Update(s => LastReconcileUtc=now, EarlyReconcileRequested=false)
                RCL->>SC: Interlocked.Exchange(_statsLastReconcileStartTicks)
                RCL->>SC: EnqueueAllReconcile()
                Note over SC: rootsRemaining = roots.Count<br/>Push Reconcile op per root
                SC->>CH: Write(Reconcile, root1, destRoot1)
                SC->>CH: Write(Reconcile, root2, destRoot2)
                RCL->>REG: MaybeCaptureRegistrySnapshotAsync()
                RCL->>RCL: schedule WS-trim (Task.Delay 2min -> EmptyWorkingSet)
                RCL->>SCHED: AdvanceJitter()
            end
        end
    end

    WRK->>FM: ReconcileRootAsync(srcRoot, dstRoot, shouldIgnore, opts)
    rect rgb(40, 60, 40)
        Note over FM: Pass 1: copy new/changed files
        FM->>FM: SafeEnumerateFiles(srcRoot)
        loop каждый файл в источнике
            FM->>FM: shouldIgnore(file)? -> skip
            FM->>FM: IsUpToDate(src, dst)? -> skip
            FM->>TFC: CopyAsync(src, dst, ct)
            TFC->>NET: chunk-by-chunk write
            FM->>FM: filesCopied++ / bytesCopied+=
            FM->>FM: per-file delay (если не turbo)
            FM->>FM: per-batch pause (BatchSize=50, 500ms)
        end
    end
    rect rgb(60, 40, 40)
        Note over FM: Pass 2: delete orphans (в dst нет в src)
        FM->>FM: SafeEnumerateFiles(dstRoot)
        loop каждый файл в назначении
            FM->>FM: File.Exists(srcEquiv)? -> NO -> Delete
            FM->>NET: File.Delete / Directory.Delete(recursive)
        end
    end
    rect rgb(40, 40, 60)
        Note over FM: Pass 3: propagate dir timestamps (bottom-up)
        FM->>FM: SafeEnumerateDirectories(srcRoot, bottom-up)
        loop каждая директория
            FM->>FM: timestamps match? -> skip
            FM->>NET: SetLastWriteTimeUtc(dstDir, srcLastWrite)
        end
    end
    FM-->>WRK: ReconcileSummary

    WRK->>SC: Interlocked.Add(_statsFilesCopiedTotal, summary.FilesCopied)
    WRK->>SC: Interlocked.Decrement(_rootsRemainingInCycle)
    alt все корни завершены (rootsRemaining <= 0)
        WRK->>PSS: Update(s => LastReconcileCompletedUtc = now)
        WRK->>SC: Interlocked.Exchange(_statsLastReconcileEndTicks)
        WRK->>LMS: MirrorIfDueAsync(ct)
    end
```

---

## 8. State machine — жизненный цикл контроллера

```mermaid
stateDiagram-v2
    [*] --> Stopped : ctor()

    Stopped --> Starting : StartAsync()<br>Interlocked.CAS(0→1)<br>_lifecycleLock.Wait()

    Starting --> Running : StartAsyncCore() OK<br>Volatile.Write(state=2)
    Starting --> Stopped : StartAsyncCore() throw<br>(transactional rollback:<br>watchers disposed,<br>CTS cancelled)

    Running --> Stopping : StopAsync()<br>Interlocked.Write(state=3)<br>_lifecycleLock.Wait()
    Starting --> Stopping : StopAsync() во время старта<br>(редкий кейс)

    Stopping --> Stopped : StopAsyncCore() done<br>Volatile.Write(state=0)

    Running --> Running : TriggerResumeReconcile()<br>(сон/пробуждение)
    Running --> Running : FSW events → queue
    Running --> Running : ReconcileLoopAsync poll

    note right of Starting
        ProcessQueueAsync запущен
        ReconcileLoopAsync запущен
        PostSyncLoopAsync запущен (если включён)
        FileSystemWatcher × N создан
        RegistrySnapshotService init
    end note

    note right of Stopping
        FSW.EnableRaisingEvents = false
        Debouncers cancelled
        _cts.Cancel()
        Channel.Writer.TryComplete()
        await _worker / _reconcileWorker / _postSyncWorker
        RegistrySnapshot (если interval gate и сеть OK)
    end note
```

### Гарантии state machine

```
┌────────────┬──────────────────────────────────────────────────────────┐
│  Инвариант │  Реализация                                              │
├────────────┼──────────────────────────────────────────────────────────┤
│ Только     │  SemaphoreSlim(1,1) _lifecycleLock сериализует           │
│ один       │  Start/Stop — конкурентные вызовы ждут в очереди         │
│ переход    │                                                          │
├────────────┼──────────────────────────────────────────────────────────┤
│ Атомарная  │  Interlocked.CompareExchange(Stopped→Starting)           │
│ CAS-защита │  Concurrent Start/Stop видят актуальное состояние        │
├────────────┼──────────────────────────────────────────────────────────┤
│ Rollback   │  StartAsyncCore() обёрнута в try/catch                   │
│ при краше  │  Partial state fully unwound → Stopped                   │
│ в старте   │                                                          │
├────────────┼──────────────────────────────────────────────────────────┤
│ Idempotent │  Dispose() охраняется Interlocked.Exchange(_disposed)    │
│ Dispose    │  Concurrent SessionEnding + Stop path → no-op вторым     │
└────────────┴──────────────────────────────────────────────────────────┘
```

---

## 9. Deployment diagram — топология развёртывания

```mermaid
C4Context
    title Deployment — ProfileMirrorSync v2.5.3

    Enterprise_Boundary(corp, "Корпоративная инфраструктура") {
        System_Ext(smb, "SMB File Server", "Windows Server / NAS<br>\\\\server\\share")
    }

    Enterprise_Boundary(pc, "Рабочий ПК — Windows 10/11 x64") {
        Container_Boundary(user_session, "Пользовательская сессия (Local\\)") {
            Component(exe, "ProfileMirrorSync.exe", ".NET 9 WinExe<br>Self-contained=false<br>PublishSingleFile=true", "Системный трей")
            Component(settings, "settings.json", "JSON", "%LocalAppData%\\ProfileMirrorSync\\")
            Component(state, "monitor_state.json", "JSON", "%LocalAppData%\\ProfileMirrorSync\\")
            Component(resume, "resume\\*.json", "JSON sidecars", "%LocalAppData%\\ProfileMirrorSync\\resume\\")
            Component(logs, "Logs\\pms-YYYY-MM-DD.log", "Daily rolling log", "%LocalAppData%\\ProfileMirrorSync\\Logs\\")
        }

        Container_Boundary(profile, "Профиль пользователя") {
            Component(desktop, "Desktop", "Watched folder", "%USERPROFILE%\\Desktop")
            Component(docs, "Documents", "Watched folder", "%USERPROFILE%\\Documents")
            Component(dl, "Downloads", "Watched folder", "%USERPROFILE%\\Downloads")
            Component(appdata, "AppData\\…", "Watched folder (opt-in)", "%APPDATA% / %LOCALAPPDATA%")
            Component(custom, "Custom folders", "Watched folder (opt-in)", "Произвольный путь")
        }

        Container_Boundary(legacy, "Legacy (read-only migration)") {
            Component(legacydata, "ProgramData legacy", "JSON (read-once)", "%ProgramData%\\ProfileMirrorSync\\")
        }
    }
```

### Файловая топология назначения

```
\\server\share\
└── {MachineName}\                   ← NormalizeMachineRoot()
    └── {UserName}\                  ← Environment.UserName
        ├── Desktop\
        ├── Documents\
        ├── Downloads\
        ├── AppData\
        │   ├── Roaming\
        │   └── Local\
        ├── Custom\{C_Users_Den_Work}\  ← SanitizeName()
        ├── Logs\                       ← MirrorLogs=true
        │   └── pms-YYYY-MM-DD.log
        ├── Registry\                   ← MirrorRegistrySnapshots=true
        │   └── HKCU_Software.reg
        └── backup\                     ← PostSync archiver output
```

### Риски по правам и производительности в deployment

```
┌─────────────────────────┬───────────────────────────────────────────────────┐
│  Точка риска            │  Проявление / митигация                           │
├─────────────────────────┼───────────────────────────────────────────────────┤
│  SMB share latency      │  Directory.Exists() с 5-секундным таймаутом;      │
│  или недоступность      │  один Warn per outage (Interlocked flag)          │
├─────────────────────────┼───────────────────────────────────────────────────┤
│  vboxsf / NAS firmware  │  MoveFileEx(REPLACE_EXISTING) → отклонён;         │
│  не поддерживает rename │  автоматический fallback: Delete + Move           │
├─────────────────────────┼───────────────────────────────────────────────────┤
│  Права на запись в      │  %LocalAppData% всегда доступен пользователю;     │
│  директорию программы   │  exe может лежать в %ProgramFiles% (read-only)    │
├─────────────────────────┼───────────────────────────────────────────────────┤
│  SetFileTime на share   │  NotSupportedException ряда NAS;                  │
│  не поддерживается      │  перехвачен try/catch, non-fatal                  │
├─────────────────────────┼───────────────────────────────────────────────────┤
│  AV-сканер блокирует    │  Retry-with-backoff (3 атт., 1/2/4 с);            │
│  файл при копировании   │  Delete-failures для Rename → defer до reconcile  │
├─────────────────────────┼───────────────────────────────────────────────────┤
│  Большой HKCU для       │  reg.exe запускается с таймаутом 10 с при Stop;   │
│  reg-snapshot           │  убивается entireProcessTree при OCE              │
├─────────────────────────┼───────────────────────────────────────────────────┤
│  Много ПК в флоте       │  Jitter (±30–60% от интервала, seed=MachineName)  │
│ запускаются одновременно│  десинхронизирует нагрузку на сервер              │
└─────────────────────────┴───────────────────────────────────────────────────┘
```

---

## 10. Модель параллелизма

### Нити и задачи

```
┌──────────────────────────────────────────────────────────────────┐
│  UI Thread (WinForms message pump)                               │
│  ┌──────────────────────────────────────────────────────────┐    │
│  │ TrayApp · SettingsForm · StatsForm · LogViewerForm       │    │
│  │ PostToUi(BeginInvoke) ← все обратные вызовы              │    │
│  └──────────────────────────────────────────────────────────┘    │
├──────────────────────────────────────────────────────────────────┤
│  ThreadPool Tasks (Task.Run)                                     │
│  ┌─────────────────┐  ┌──────────────────┐  ┌─────────────────┐  │
│  │ProcessQueueAsync│  │ReconcileLoopAsync│  │PostSyncLoopAsync│  │
│  │ (single reader) │  │ (60s poll)       │  │(PostSync timer) │  │
│  └─────────────────┘  └──────────────────┘  └─────────────────┘  │
│  ┌───────────────────────────────────────────────────────────┐   │
│  │ FSW callbacks × N (OS-managed threads)                    │   │
│  │ → EnqueueChange() → Schedule() → Task.Delay debouncer     │   │
│  └───────────────────────────────────────────────────────────┘   │
│  ┌───────────────────────────────────────────────────────────┐   │
│  │ One-shot tasks:                                           │   │
│  │  MaybeCaptureRegistrySnapshotAsync()                      │   │
│  │  ResumeStateStore.CleanupOrphans()                        │   │
│  │  EmptyWorkingSet (2 min after reconcile)                  │   │
│  │  IsDestinationReachable probe (5s timeout)                │   │
│  └───────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────┘
```

### Разграничение доступа к разделяемым структурам

```
┌──────────────────────────────┬───────────────────────────────────────────────┐
│  Структура                   │  Механизм thread-safety                       │
├──────────────────────────────┼───────────────────────────────────────────────┤
│  _state (int)                │  Interlocked.CompareExchange / Volatile.Write │
│  _disposed (int)             │  Interlocked.Exchange                         │
│  _destinationWarningLogged   │  Interlocked.Exchange                         │
│  _statsFilesCopiedTotal      │  Interlocked.Increment / Add                  │
│  _statsLastReconcileTicks    │  Interlocked.Exchange / Read                  │
│  _highWaterMark              │  CAS retry loop (monotonic max)               │
│  _lastReportedWatermark      │  Interlocked.CompareExchange (one wins)       │
│  _lastDropLogTicks           │  Interlocked.CompareExchange                  │
│  _droppedSinceLastLog        │  Interlocked.Increment / Exchange             │
├──────────────────────────────┼───────────────────────────────────────────────┤
│  _dedupe (ConcDict)          │  ConcurrentDictionary (lock-free fast path)   │
│  _roots (ConcDict)           │  ConcurrentDictionary (.Values = snapshot)    │
│  _debouncers (ConcDict)      │  ConcurrentDictionary + per-key CTS           │
├──────────────────────────────┼───────────────────────────────────────────────┤
│  BoundedChannel              │  Designed for multi-writer / single-reader    │
│                              │  SingleReader=true, AllowSyncCont=false       │
├──────────────────────────────┼───────────────────────────────────────────────┤
│  PersistentStateStore        │  object _lock (все операции под локом)        │
│  ByteRateLimiter             │  object _lock (Refill + token subtract)       │
│  TurboModeController         │  object _lock + Volatile.Read на fast path    │
│  Logger                      │  object _lock (StreamWriter + history queue)  │
│  _hardExcludedWarned (HashSet│  _hardExcludedWarnLock (выделенный object)    │
├──────────────────────────────┼───────────────────────────────────────────────┤
│  _lifecycleLock              │  SemaphoreSlim(1,1) — async-safe              │
└──────────────────────────────┴───────────────────────────────────────────────┘
```

### Back-pressure в bounded channel

```
    Очередь заполнена?
         │
    ┌────▼────┐
    │ op.Kind │
    └────┬────┘
         ├── CreateOrChange ──► TryWrite (try fast-path)
         │                          │
         │                     полна → DROP + log(30s dedup)
         │                          │
         │                     _dedupe.TryRemove (повторная постановка
         │                     в очередь при следующем FSW-событии)
         │
         └── Delete / Rename ──► WriteAsync с linked CTS (5 s)
                                      │
                                 OK → удаление будет доставлено
                                      │
                                 timeout → DROP + _dedupe.Remove
                                 (reconcile подберёт следующим циклом)
```

---

## 11. Подсистема копирования файлов (ThrottledFileCopier)

```mermaid
flowchart TD
    A([CopyAsync]) --> B{PublishMode?}
    B -- TempThenRename --> C[Копировать в .pms_tmp<br>allowResume=false]
    C --> D{TryPublishRename<br>delete+rename}
    D -- OK --> E([Done])
    D -- NotSupported --> F[Warn, Delete .pms_tmp<br>Fallback → DirectWrite]
    F --> G
    B -- DirectWrite --> G[ClearReadOnly dst]
    G --> H[CopyWithRetryAsync<br>allowResume=true]

    H --> I[CopyOnceAsync]
    I --> J[ArrayPool.Rent 64KB]
    J --> K{ResumeEnabled<br>& size ≥ threshold?}
    K -- YES --> L[ComputeHeadHash<br>SHA-256 первых 4KB]
    L --> M[ResumeStateStore.TryLoad]
    M --> N{sidecar match?}
    N -- strictMatch<br>orstrictGrowMatch --> O[startOffset = sidecar.BytesCopied<br>SetLength dst если dst > sidecar]
    N -- NO match --> P[startOffset = 0<br>sidecar.Clear]
    K -- NO --> P

    O --> Q
    P --> Q[Open src FileStream<br>Open dst FileStream<br>startOffset]

    Q --> R{chunk loop}
    R --> S[Read src chunk<br>≤ limiter.MaxBurstBytes]
    S --> T[ByteRateLimiter.WaitAsync<br>bytes=chunkSize]
    T --> U[WriteAsync dst<br>FlushAsync]
    U --> V{every ~1MB?}
    V -- YES --> W[ResumeStateStore.Save<br>emitTrace=каждые 10MB]
    W --> R
    V -- NO --> R
    R -- EOF --> X[ArrayPool.Return]
    X --> Y[SetCreationTimeUtc<br>SetLastWriteTimeUtc]
    Y --> Z[ResumeStateStore.Clear]
    Z --> E
```

### Retry-with-backoff

```
CopyWithRetryAsync (максимум RetryCount = 5 попыток):

Попытка 1 ──► IOException/SocketException ──► delay 1s
Попытка 2 ──► IOException/SocketException ──► delay 2s
Попытка 3 ──► IOException/SocketException ──► delay 4s
Попытка 4 ──► OK ──► возврат
            (базовый delay = 200ms; удваивается, retryCount из AppSettings)

OperationCanceledException — НЕ перехватывается, propagates up
```

---

## 12. Алгоритм реконсиляции (три прохода)

```
ReconcileRootAsync(srcRoot, dstRoot, shouldIgnore, opts, onFileCopied, ct)
│
├── Проверка: Directory.Exists(srcRoot) → если нет — вернуть Empty
│
├── ══ PASS 1 ══ Copy new / changed ════════════════════════════════════
│   foreach file in SafeEnumerateFiles(srcRoot):
│     skip reparse points (junctions, symlinks)
│     skip UnauthorizedAccess directories
│     shouldIgnore(file)?  ──YES──► skip
│     IsUpToDate(file, dstFile)?  ──YES──► skip
│       IsUpToDate: File.Exists(dst)
│                  && dst.LastWriteTimeUtc == src.LastWriteTimeUtc
│                  && dst.Length == src.Length
│     CopyAsync(file, dstFile, ct)
│     filesCopied++, bytesCopied += srcLen
│     onFileCopied()   ← callback для turbo-mode + stats
│     if !turboActive && FileDelayMs > 0:
│       await Task.Delay(FileDelayMs)   ← только при реальном копировании
│     batchCount++; if batchCount % BatchSize == 0:
│       await Task.Delay(BatchPauseMs)
│
├── ══ PASS 2 ══ Delete orphans ═══════════════════════════════════════
│   EmptySourceGuard: если SafeEnumerateFiles(srcRoot) вернул 0
│     файлов и DeletionSafetyGuard=true → ABORT (защита от AV/placeholder)
│   foreach file in SafeEnumerateFiles(dstRoot):
│     srcEquiv = Path.Combine(srcRoot, relPath)
│     File.Exists(srcEquiv)?  ──YES──► skip (файл есть)
│     shouldIgnore(dstFile)?  ──YES──► skip
│     File.Delete(dstFile)  +  orphansDeleted++
│     [аналогично для пустых директорий]
│
└── ══ PASS 3 ══ Propagate directory timestamps (bottom-up) ══════════
    foreach dir in SafeEnumerateDirectories(srcRoot, bottom-up):
      srcMtime = dir.LastWriteTimeUtc
      dstMtime = dstDir.LastWriteTimeUtc (если существует)
      if srcMtime != dstMtime:
        Directory.SetLastWriteTimeUtc(dstDir, srcMtime)
        dirsTouched++
    (bottom-up: дочерние изменения не сбрасывают mtime родителя)
```

### ReconcileSummary — возвращаемые метрики

```
ReconcileSummary {
  FilesCopied:    int     // кредитуется _statsFilesCopiedTotal
  BytesCopied:    long    // кредитуется _statsBytesCopiedTotal
  OrphansDeleted: int     // кредитуется _statsFilesDeletedTotal
  FilesSkipped:   int     // кредитуется _statsErrorsTotal
  DirsTouched:    int     // информационно
}
```

---

## 13. Планировщик реконсиляции и адаптивный триггер

```mermaid
flowchart LR
    subgraph "ReconcileScheduler.Evaluate(state, now)"
        A[anchor = LastReconcileUtc ?? now]
        B["NextDue = anchor<br>+ BaseIntervalMin<br>+ currentJitterOffsetSec"]
        C{now >= NextDue?}
        D{EarlyReconcileRequested<br>&& IsEarlyTriggerAllowed?}
        E[IsDue=true<br>DueByTime=true]
        F[IsDue=true<br>DueByEarly=true]
        G[IsDue=false]
    end
    A --> B --> C
    C -- YES --> E
    C -- NO --> D
    D -- YES --> F
    D -- NO --> G
```

### Jitter-стратегия

```
BaseIntervalMinutes = max(5, ReconcileIntervalMinutes)   default = 1440 мин (24 ч)
JitterPercent       = clamp(ReconcileJitterPercent, 0, 100)  default = 30%

jitterRangeSec = BaseIntervalMinutes × 60 × JitterPercent / 100
               = 1440 × 60 × 0.30 = 25 920 сек (±12 960 сек = ±3.6 ч)

currentJitterOffsetSec ∈ [−12960, +12960]
seed RNG = Environment.MachineName.GetHashCode()  ← стабильный per-machine

В флоте из 10 ПК реконсиляции равномерно разбросаны по 24-часовому окну
вместо одновременного шторма на сервер.
```

### Адаптивный (early) триггер

```
В Enqueue() при TryWrite (fast path):

  pct = _dedupe.Count × 100 / _queueCapacity
  if pct >= EarlyReconcileQueueThresholdPct (default = 80):
    PersistentStateStore.Update(s => {
      if (!s.EarlyReconcileRequested) {
        s.EarlyReconcileRequested = true
        s.LastEarlyReconcileRequestUtc = now
        log.Info("Очередь N% ≥ threshold% — запрошена досрочная реконсиляция")
      }
    })

IsEarlyTriggerAllowed:
  (now - LastReconcileUtc) >= EarlyReconcileMinGapMinutes (default = 5)
  Предотвращает thrashing при непрерывном event-storm.
```

---

## 14. Расположение файлов и пути данных

### Файлы программы

```
 Компонент                     Путь
─────────────────────────────────────────────────────────────────────────
 Исполняемый файл              Любой, напр. %ProgramFiles%\PMS\
                               (read-only — не требует прав)
 Per-user data root            %LocalAppData%\ProfileMirrorSync\
 settings.json                 %LocalAppData%\ProfileMirrorSync\settings.json
 monitor_state.json            %LocalAppData%\ProfileMirrorSync\monitor_state.json
 resume sidecars               %LocalAppData%\ProfileMirrorSync\resume\<hash16>.json
 Логи                          %LocalAppData%\ProfileMirrorSync\Logs\pms-YYYY-MM-DD.log
 crash.log                     %LocalAppData%\ProfileMirrorSync\Logs\crash.log
 Legacy (migration source)     %ProgramData%\ProfileMirrorSync\settings.json  (read-once)
```

### Файлы назначения (SMB)

```
 Тип данных                    Путь на шаре
─────────────────────────────────────────────────────────────────────────
 Профиль пользователя          {DestinationRoot}\{MachineName}\{UserName}\
 Desktop                       …\Desktop\
 Documents                     …\Documents\
 Downloads                     …\Downloads\
 AppData\Roaming               …\AppData\Roaming\
 AppData\Local                 …\AppData\Local\
 AppData\LocalLow              …\AppData\LocalLow\
 Произвольная папка            …\Custom\{SanitizedPath}\
 Логи (MirrorLogs=true)        …\Logs\pms-YYYY-MM-DD.log
 Реестр (MirrorReg=true)       …\Registry\HKCU_Software.reg
 Архив (PostSync)              …\backup\  (аргументы конфигурируются)
```

### Атомарность записи

```
                  ┌────────────────────────────────────────────┐
 settings.json    │  1. Serialize → string json                │
 monitor_state    │  2. File.WriteAllText(path + ".tmp", json) │
 resume sidecar   │  3. File.Move(tmp, path, overwrite:true)   │
                  └────────────────────────────────────────────┘
 Гарантия: читатель всегда видит либо старый, либо новый файл целиком.
 Повреждённый settings.json → backup + reset to defaults (не молчащая потеря).
```

---

## 15. Надёжность и обработка ошибок

### Граф отказоустойчивости

```mermaid
flowchart TD
    subgraph "Transient errors"
        E1["IOException<br>(сеть, AV-лок)"]
        E2["SocketException"]
        E3["UnauthorizedAccessException"]
    end

    subgraph "Recovery mechanisms"
        R1["RetryAsync<br>3 попытки, backoff 1/2/4s"]
        R2["Reconcile<br>(поймает пропущенное)"]
        R3["SafeEnumerateFiles<br>skip on exception"]
        R4["Loop restart<br>30–60s backoff"]
        R5["Drop + log<br>(30s dedup)"]
    end

    E1 --> R1 --> |"max retries"| R2
    E2 --> R1
    E3 --> R3
    subgraph "Channel overflow"
        EF[Queue full]
    end
    EF --> |"CreateOrChange"| R5
    EF --> |"Delete/Rename"| R1

    subgraph "Worker crash"
        WC[ProcessQueueAsync<br>ReconcileLoopAsync<br>PostSyncLoopAsync]
    end
    WC --> R4
    Note1["Outer try/catch<br>log Error<br>check state==Running<br>Task.Run(self)"]
```

### Защита от пустого источника (DeletionSafetyGuard)

```
Сценарий риска:
  Профиль не смонтирован / AV поместил файлы в карантин /
  placeholder-файлы OneDrive = "источник пустой".
  Без защиты Pass 2 удалит ВСЁ с сервера.

Защита (DeletionSafetyGuardEnabled = true):
  if SafeEnumerateFiles(srcRoot).Count() == 0:
    log.Warn("Источник пуст — удаление на сервере заблокировано")
    return ReconcileSummary.Empty
```

### Защита от self-sync loop

```
_hardExcludes = {
  %ProgramData%\ProfileMirrorSync,
  %LocalAppData%\ProfileMirrorSync,   ← наши собственные данные
  AppContext.BaseDirectory,            ← директория exe
}

ShouldIgnore: любой путь, начинающийся с одного из hardExcludes → true
Если пользователь явно добавил директорию программы — один Warn в лог.
```

---

## 16. Фильтрация и исключение файлов

### Порядок проверок в `ShouldIgnore`

```
1. HardExcludes (HashSet)              — app dir, own data dir
2. AlwaysIgnoreFileNames (HashSet)     — desktop.ini, Thumbs.db, ehthumbs.db
3. AlwaysIgnoreExtensions (string[])  — .pms_tmp, .lnk, .tmp
4. AlwaysIgnoreSegments (string[])    — \$RECYCLE.BIN\, \System Volume Information\
5. UserExcludedRelativePaths          — из settings.json, через IsSegmentMatch()
```

### Алгоритм `IsSegmentMatch(path, pattern)`

Исправленный алгоритм обеспечивает точное граничное совпадение сегмента пути:

```
pattern `\bin\`      → match `proj\bin\Release\…`     YES
                     → match `proj\binary\…`          NO
pattern `AppData\Local\Temp` → match `C:\Users\x\AppData\Local\Temp\` YES
                              → match `C:\Users\x\AppData\LocalLow\`   NO
pattern `\.git\`     → match `proj\.git\HEAD`         YES
                     → match `proj\my.gitignore`       NO

Алгоритм:
  patHasLeadSep  = pat[0] == '\\'
  patHasTrailSep = pat[^1] == '\\'
  idx = 0; while (idx = path.IndexOf(pat, idx, OrdinalIgnoreCase)) >= 0:
    leftOk  = patHasLeadSep  || idx==0 || path[idx-1]=='\\'
    rightOk = patHasTrailSep || idx+len>=path.Length || path[idx+len]=='\\'
    if leftOk && rightOk: return true
    idx++
```

### Встроенные исключения по умолчанию

```
AppData\Local\Temp
AppData\Local\Microsoft\Windows\INetCache
AppData\Local\Microsoft\Windows\INetCookies
AppData\Local\Packages
AppData\Local\CrashDumps
AppData\Local\Google\Chrome\User Data\Default\Cache
AppData\Local\Microsoft\Edge\User Data\Default\Cache
AppData\Roaming\Spotify\Storage
AppData\Local\Discord\Cache
\obj\        ← build artifacts
\bin\
\.vs\
\node_modules\
\.git\
```

---

## 17. Периодические фоновые задачи

```mermaid
gantt
    title Временная шкала фоновых задач (типичный рабочий день)
    dateFormat HH:mm
    axisFormat %H:%M

    section ReconcileLoop
    Poll 60s intervals   : active, 00:00, 24:00

    section Reconcile (пример)
    Startup reconcile    : milestone, 08:00, 0h
    Scheduled +jitter    : milestone, 08:24, 0h
    Next + jitter        : milestone, 12:00, 0h

    section PostSync (1440min default)
    PostSync run         : milestone, 09:00, 0h

    section LogMirror (23h gate)
    Log mirror           : milestone, 08:25, 0h

    section RegistrySnapshot (30d gate)
    Registry snapshot    : milestone, 08:26, 0h
```

### Таблица фоновых задач

```
┌────────────────────────────┬──────────────────┬──────────────────────────────┐
│  Задача                    │  Интервал        │  Триггеры / примечания       │
├────────────────────────────┼──────────────────┼──────────────────────────────┤
│  ReconcileLoopAsync        │  poll каждые 60с │  Плановый + early trigger    │
│  (основной sweep)          │  интервал 24ч    │  queue pressure ≥ threshold  │
│                            │  ± jitter        │  мастер-ключ ReconcileEnabled│
├────────────────────────────┼──────────────────┼──────────────────────────────┤
│  PostSyncLoopAsync         │  PostSyncInterval│  Независимый таймер;         │
│  (внешний архиватор)       │  (default 1440m) │  1–10 min jitter при first   │
│                            │                  │  run; gate LastPostSyncUtc   │
├────────────────────────────┼──────────────────┼──────────────────────────────┤
│  LogMirrorService          │  ≤ 1 раз/23ч     │  После полного цикла         │
│  (копия лога на шару)      │                  │  реконсиляции всех корней    │
├────────────────────────────┼──────────────────┼──────────────────────────────┤
│  RegistrySnapshotService   │  RegistryBackup  │  При старте + по плановому   │
│  (reg.exe export HKCU)     │  IntervalMinutes │  reconcile; 10s timeout      │
│                            │  default 43200m  │  при Stop                    │
│                            │  (30 дней)       │                              │
├────────────────────────────┼──────────────────┼──────────────────────────────┤
│  EmptyWorkingSet           │  +2 мин после    │  psapi.EmptyWorkingSet()     │
│  (memory trim)             │ каждого reconcile│  best-effort, silent fail    │
├────────────────────────────┼──────────────────┼──────────────────────────────┤
│  ResumeStateStore.Cleanup  │  После каждого   │  Удаляет сайдкары старше     │
│  (orphan sidecar GC)       │  плановогорecon. │  ResumeSidecarMaxAgeDays     │
└────────────────────────────┴──────────────────┴──────────────────────────────┘
```

### Триггеры от системных событий (TrayApp)

```
SystemEvents.PowerModeChanged(Resume)  →  TriggerResumeReconcile()
                                           ждёт 30с → проверяет сеть → EnqueueAllReconcile()

SessionSwitch(SessionLogon)            →  TriggerResumeReconcile()  (если WakeOnSessionEvents)
SessionSwitch(SessionUnlock)           →  TriggerResumeReconcile()  (если WakeOnUnlock)

OnSessionEnding                        →  DoStopAsync() (минуя lifecycle lock)
                                           гарантирует чистое завершение при logoff/shutdown
```

---

## 18. Многопользовательская модель

```
┌──────────────────────────────────────────────────────────────────────┐
│  Один исполняемый файл (shared install, read-only %ProgramFiles%)    │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  Сессия User_A                    Сессия User_B                      │
│  ┌──────────────────────────┐    ┌──────────────────────────┐        │
│  │ %LocalAppData%\PMS\      │    │ %LocalAppData%\PMS\      │        │
│  │  settings.json           │    │  settings.json           │        │
│  │  monitor_state.json      │    │  monitor_state.json      │        │
│  │  resume\*.json           │    │  resume\*.json           │        │
│  │  Logs\*.log              │    │  Logs\*.log              │        │
│  └───────────┬──────────────┘    └───────────┬──────────────┘        │
│              │                               │                       │
│              ▼                               ▼                       │
│  \\server\share\PC01\User_A\    \\server\share\PC01\User_B\          │
│                                                                      │
│  Single-instance mutex: Local\ProfileMirrorSync_SingleInstance       │
│  (Local\ = per-session, Global\ = all sessions)                      │
│  → каждая RDP/VDI-сессия может запустить свой экземпляр              │
└──────────────────────────────────────────────────────────────────────┘

Migration (одноразовая при первом запуске):
  if File.Exists(%ProgramData%\PMS\settings.json)
  && !File.Exists(%LocalAppData%\PMS\settings.json):
    File.Copy(%ProgramData%\PMS\settings.json, %LocalAppData%\PMS\settings.json)
```

---

## 19. Настройки и значения по умолчанию

### Основные параметры `AppSettings`

```
┌──────────────────────────────────┬──────────────────┬────────────────────────────┐
│  Параметр                        │  Default         │  Назначение                │
├──────────────────────────────────┼──────────────────┼────────────────────────────┤
│  DestinationRoot                 │  ""              │  SMB-шара (обязательно)    │
│  MaxBandwidthBitsPerSecond       │  1 000 000 bps   │  Базовый лимит (1 Мбит/с)  │
│  LogLevel                        │  Info            │  Debug / Info / Warning    │
│  SyncOnStartup                   │  true            │  Reconcile при старте      │
│  StartMinimizedToTray            │  true            │  Без окна настроек         │
├──────────────────────────────────┼──────────────────┼────────────────────────────┤
│  MirrorDesktop / Documents /     │  true / true /   │  Профильные папки          │
│  Downloads                       │  true            │  (остальные = false)       │
├──────────────────────────────────┼──────────────────┼────────────────────────────┤
│  FileDebounceMilliseconds        │  700 мс          │  Ожидание "тишины" после   │
│                                  │                  │  последнего FSW-события    │
│  ReconcileIntervalMinutes        │  1440 (24ч)      │  Плановый интервал         │
│  ReconcileJitterPercent          │  30%             │  ±15% от интервала         │
│  ReconcileEnabled                │  true            │  Мастер-ключ планового     │
├──────────────────────────────────┼──────────────────┼────────────────────────────┤
│  ReconcileFileDelayMs            │  20 мс           │  Пауза между файлами       │
│  ReconcileBatchSize              │  50 файлов       │  Размер батча              │
│  ReconcileBatchPauseMs           │  500 мс          │  Пауза после батча         │
├──────────────────────────────────┼──────────────────┼────────────────────────────┤
│  TurboFirstRunEnabled            │  true            │  Включить burst-режим      │
│  TurboThresholdFiles             │  1000            │  Порог очереди для turbo   │
│  TurboFirstRunBandwidthMbps      │  3 Мбит/с        │  Лимит в turbo-режиме      │
│  TurboOnReconcile                │  false           │  Turbo в плановом цикле    │
├──────────────────────────────────┼──────────────────┼────────────────────────────┤
│  QueueCapacity                   │  1000            │  Размер bounded channel    │
│  EarlyReconcileQueueThreshold    │  80%             │  Порог early trigger       │
│  EarlyReconcileMinGapMinutes     │  5 мин           │  Мин. между early triggers │
├──────────────────────────────────┼──────────────────┼────────────────────────────┤
│  RetryCount                      │  5               │  Попытки копирования       │
│  ResumeEnabled                   │  true            │  Byte-range resume         │
│  ResumeMinFileSizeBytes          │  10 MB           │  Порог для resume          │
│  ResumeSidecarMaxAgeDays         │  7 дней          │  TTL сайдкара              │
│  PublishMode                     │  DirectWrite     │  DirectWrite/TempThenRename│
├──────────────────────────────────┼──────────────────┼────────────────────────────┤
│  LowerIoPriority                 │  true            │  IO background priority    │
│  DeletionSafetyGuardEnabled      │  false           │  Защита от пустого src     │
│  LogRetentionDays                │  30 дней         │  Ротация логов             │
├──────────────────────────────────┼──────────────────┼────────────────────────────┤
│  PostSyncEnabled                 │  false           │  Внешний архиватор         │
│  PostSyncIntervalMinutes         │  1440 (24ч)      │  Независимый интервал      │
│  PostSyncExePath / Arguments     │  ""              │  Путь и аргументы exe      │
├──────────────────────────────────┼──────────────────┼────────────────────────────┤
│  MirrorRegistrySnapshots         │  false           │  reg.exe export HKCU       │
│  RegistryBackupIntervalMinutes   │  43200 (30 дней) │  Интервал reg-снимка       │
│  MirrorLogs                      │  false           │  Зеркалировать лог-файлы   │
│  SkipStartupReconcileIfWithin    │  5 мин           │  Пропуск startup reconcile │
│  Minutes                         │                  │  если недавно завершился   │
└──────────────────────────────────┴──────────────────┴────────────────────────────┘
```

### Token-bucket: ёмкость и эффективный диапазон

```
В режиме:           Burst cap (2s):     Минимальный chunk:   Минимальный rate:
─────────────────────────────────────────────────────────────────────────────
Baseline 1 Mbit/s   250 000 байт        4 096 байт           ~16 Kbit/s (UI min)
Turbo    3 Mbit/s   750 000 байт        4 096 байт           ~16 Kbit/s
Unlimited (0)       int.MaxValue        64 KB                 ∞
```

---
