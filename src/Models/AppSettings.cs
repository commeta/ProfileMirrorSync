namespace ProfileMirrorSync.Models;

/// <summary>Minimum log level written to file and shown in the log viewer.</summary>
public enum AppLogLevel
{
    Debug   = 0,
    Info    = 1,
    Warning = 2,
}

/// <summary>
/// How <see cref="ProfileMirrorSync.Services.ThrottledFileCopier"/> publishes a
/// freshly-copied file to the destination.
///
/// <list type="bullet">
/// <item><b>DirectWrite</b> — open the destination file in place and stream
/// into it (legacy behaviour: "no tmp file, no rename").  Maximally compatible
/// with shares that reject MoveFileEx (vboxsf, some NAS firmwares), but a copy
/// interrupted below the resume threshold leaves a truncated destination until
/// the next reconcile.</item>
/// <item><b>TempThenRename</b> — stream into a sibling <c>*.pms_tmp</c> file,
/// then delete-then-rename it over the destination (same delete+rename pattern
/// as MirrorRenameAsync, avoiding MOVEFILE_REPLACE_EXISTING).  The destination
/// is only ever replaced atomically by a fully-written file.  Falls back to
/// DirectWrite automatically when the rename is rejected by the share.</item>
/// </list>
/// </summary>
public enum FilePublishMode
{
    DirectWrite    = 0,
    TempThenRename = 1,
}

public sealed class AppSettings
{
    public string? DestinationRoot { get; set; } = @"";
    // Default tuned for the canonical "corporate uplink" scenario. 
    public int MaxBandwidthBitsPerSecond { get; set; } = 1_000_000;
    public AppLogLevel LogLevel { get; set; } = AppLogLevel.Info;
    public bool SyncOnStartup        { get; set; } = true;
    public bool StartMinimizedToTray { get; set; } = true;

    // Profile folder flags. Conservative defaults for the scenario. 
    // Only Desktop, Documents, Downloads are on by default.
    public bool MirrorDesktop        { get; set; } = true;
    public bool MirrorDocuments      { get; set; } = true;
    public bool MirrorDownloads      { get; set; } = true;
    public bool MirrorPictures       { get; set; } = false;
    public bool MirrorVideos         { get; set; } = false;
    public bool MirrorMusic          { get; set; } = false;
    public bool MirrorFavorites      { get; set; } = false;
    public bool MirrorContacts       { get; set; } = false;
    public bool MirrorLinks          { get; set; } = false;
    public bool MirrorSearches       { get; set; } = false;
    public bool MirrorSavedGames     { get; set; } = false;
    public bool MirrorAppDataRoaming    { get; set; } = false;
    public bool MirrorAppDataLocal      { get; set; } = false;
    public bool MirrorAppDataLocalLow   { get; set; } = false;

    // Arbitrary user-defined source folders (full absolute paths)
    public List<string> CustomFolderPaths { get; set; } = new();

    public bool MirrorRegistrySnapshots { get; set; } = false;
    public List<string> RegistryPaths   { get; set; } = new() { @"HKCU\Software" };

    // Copy the current application log file to the destination once per day.
    // Destination: {DestinationRoot}\{Machine}\{User}\Logs\pms-YYYY-MM-DD.log
    // Disabled by default.
    public bool MirrorLogs { get; set; } = false;

    public List<string> ExcludedRelativePaths { get; set; } = new()
    {
        @"AppData\Local\Temp",
        @"AppData\Local\Microsoft\Windows\INetCache",
        @"AppData\Local\Microsoft\Windows\INetCookies",
        @"AppData\Local\Packages",
        @"AppData\Local\CrashDumps",
        @"AppData\Local\Google\Chrome\User Data\Default\Cache",
        @"AppData\Local\Microsoft\Edge\User Data\Default\Cache",
        @"AppData\Roaming\Spotify\Storage",
        @"AppData\Local\Discord\Cache",
        // Build artifact directories - never useful to mirror
        @"\obj\",
        @"\bin\",
        @"\.vs\",
        @"\node_modules\",
        @"\.git\",
    };

    public int FileDebounceMilliseconds { get; set; } = 700;
    // 24h default. Background reconcile catches events FSW missed; real-time
    // mirroring continues via FileSystemWatcher. Anchor persisted in state.json.
    public int ReconcileIntervalMinutes { get; set; } = 1440;
    public int RetryCount               { get; set; } = 5;

    // Master switch for the SCHEDULED (timer-based) reconcile.  When
    // false, the background reconcile loop still runs but never fires the
    // periodic full sweep; real-time FileSystemWatcher mirroring, startup sync,
    // and event-driven (wake/unlock) reconciles are unaffected.  Default ON.
    // Lets an admin who fully trusts FSW + startup-sync eliminate the periodic
    // server scan entirely.
    public bool ReconcileEnabled { get; set; } = true;

    // Reconcile throttling: pause between files + batch pauses. Spread all
    // reconcile I/O across time so neither server nor uplink sees a spike.
    public int ReconcileFileDelayMs  { get; set; } = 20;   // ms between each file
    public int ReconcileBatchSize    { get; set; } = 50;   // files per batch
    public int ReconcileBatchPauseMs { get; set; } = 500;  // ms pause after each batch

    // Log file retention
    public int LogRetentionDays { get; set; } = 30;

    // Turbo mode ("event-storm" acceleration): raise bandwidth when a real-time
    // burst of pending work crosses the threshold. Field name keeps "FirstRun"
    // for settings.json compatibility.
    //
    // Defaults tuned for: turbo is 3 Mbit/s (so even several
    // PCs in turbo at once stay within the uplink) and only engages on a large
    // real-time event-storm (≥1000 pending files — e.g. the user just unpacked
    // a big archive into a watched folder), NOT on a routine scheduled reconcile.
    public bool TurboFirstRunEnabled       { get; set; } = true;
    public int  TurboThresholdFiles        { get; set; } = 1000;
    public int  TurboFirstRunBandwidthMbps { get; set; } = 3;

    // When false (default), turbo reacts ONLY to real-time event-storms (the
    // FileSystemWatcher enqueue path). When true, the scheduled reconcile may
    // also raise turbo once its per-cycle file count crosses the threshold.
    // Off by default so a nightly multi-thousand-file sweep stays at the base
    // 1 Mbit limit and does not spike the shared uplink.
    public bool TurboOnReconcile           { get; set; } = false;

    // Trace mode: Warning/Error entries include full stack trace. Default off.
    public bool EnableTraceMode { get; set; } = false;

    // Random jitter added to scheduled reconcile intervals (% of base interval)
    // to desync a fleet of PCs hitting the same server.
    public int ReconcileJitterPercent { get; set; } = 30;

    // Max ops queued from FSW events before back-pressure. Created/Changed are
    // dropped (reconcile picks them up); Delete/Rename block briefly.
    public int QueueCapacity { get; set; } = 10_000;

    // Adaptive reconcile: queue beyond this % (50-95) schedules an early
    // reconcile. 0 disables.
    public int EarlyReconcileQueueThresholdPct { get; set; } = 80;
    // Minimum gap between two queue-pressure-triggered reconciliations.
    public int EarlyReconcileMinGapMinutes     { get; set; } = 60;

    // Lower worker-thread IO+CPU priority via THREAD_MODE_BACKGROUND_BEGIN
    // around each write (no admin needed). Applied per-dispatch so the UI
    // thread is never throttled. User shouldn't feel the program.
    public bool LowerIoPriority { get; set; } = true;

    // Byte-range resume for large files. Copies above ResumeMinFileSizeBytes
    // save progress to a sidecar so a mid-copy shutdown continues, not restarts.
    public bool ResumeEnabled            { get; set; } = true;
    public long ResumeMinFileSizeBytes   { get; set; } = 50L * 1024 * 1024;  // 50 MB
    public int  ResumeSidecarMaxAgeDays  { get; set; } = 7;

    // When SyncOnStartup=true, skip the initial reconcile if the previous run
    // FINISHED within this many minutes. 0 = always run startup reconcile.
    public int SkipStartupReconcileIfWithinMinutes { get; set; } = 5;

    // Event-driven reconcile triggers (beyond the scheduled interval).
    //   • Wake-from-sleep/hibernate: FSW is dead while suspended and misses
    //     events, so a reconcile on resume is genuinely useful — default ON.
    //   • Unlock / logon: FSW keeps running while the screen is merely locked,
    //     so files do not diverge — a reconcile here is usually redundant.
    //     Default OFF to avoid frequent unnecessary reconciliation.
    public bool ReconcileOnWake   { get; set; } = true;
    public bool ReconcileOnUnlock { get; set; } = false;
    public bool ReconcileOnLogon  { get; set; } = false;

    // Registry backup decoupled from reconcile (default 30 days). 0 = legacy
    // "snapshot on every reconcile".
    public int RegistryBackupIntervalMinutes { get; set; } = 43_200; // 30 days

    // Statistics window (real-time diagnostics). Does nothing until opened.
    public bool StatsWindowEnabled    { get; set; } = true;
    public int  StatsRefreshIntervalMs{ get; set; } = 1000;

    // ── - File publish mode (atomic temp+rename, opt-in) ──────────────
    // DirectWrite (legacy) writes in place; TempThenRename publishes atomically
    // via a sibling *.pms_tmp + delete-then-rename, with automatic fallback to
    // DirectWrite when the share rejects the rename. See FilePublishMode.
    public FilePublishMode PublishMode { get; set; } = FilePublishMode.DirectWrite;

    // ── - Post-sync external program (e.g. 7-Zip archiving) ───────────
    // After a full reconcile cycle completes, optionally launch an external
    // program (archiver, custom script). Gated to run at most once per
    // PostSyncIntervalMinutes (default 1440 = once/day) via
    // PersistentState.LastPostSyncRunUtc, independent of reconcile frequency.
    // Argument placeholders expanded before launch:
    //   {dest}    -> {DestinationRoot}\{Machine}\{User}
    //   {backup}  -> {dest}\backup (created if missing; recommended archive dir)
    //   {machine} -> Environment.MachineName
    //   {user}    -> Environment.UserName
    //   {date}    -> yyyy-MM-dd (local date at launch)
    //   {time}    -> HH-mm-ss   (local time at launch)
    public bool   PostSyncEnabled         { get; set; } = false;
    public string PostSyncExePath         { get; set; } = "";
    // Default example: 7-Zip on maximum settings, archiving the whole current
    // destination folder into the backup subfolder with a dated archive name.
    // -xr!backup excludes the backup\ subfolder itself (which lives inside
    // {dest}) so repeated runs don't nest old archives into new ones.
    public string PostSyncArguments       { get; set; } =
        "a -t7z -mx=9 -mmt=on -xr!backup \"{backup}\\{machine}_{user}_{date}.7z\" \"{dest}\\*\"";
    public string PostSyncWorkingDir      { get; set; } = "";
    // How often (minutes) the post-sync program may run. Default once per day.
    public int    PostSyncIntervalMinutes { get; set; } = 1440;
    // Hard timeout (minutes) for the external program. 0 = wait until it exits.
    public int    PostSyncTimeoutMinutes  { get; set; } = 60;
    // Run the external program at BelowNormal priority.
    public bool   PostSyncLowPriority      { get; set; } = true;

    // ── - Empty-source deletion safety guard (opt-in) ─────────────────
    //
    // When ON: Pass 2 / Pass 2b skip orphan deletion if the source root exists
    // but yields ZERO files while the destination has files (profile not loaded
    // at logon, OneDrive placeholders dehydrated, AV quarantine, ACL slip, …) —
    // prevents wiping the only server-side copy.  A Warn is logged and the next
    // reconcile retries once the source is readable again.
    //
    // OFF by default: the classic one-way-mirror behaviour (an empty source
    // means an empty destination).  Administrators who treat the destination as
    // the last line of defence against an unreadable source should enable it.
    public bool DeletionSafetyGuardEnabled { get; set; } = false;

    // Backwards compat with v2.0
    public bool? DebugLoggingEnabled { get; set; } = null;
    public int SettingsVersion { get; set; } = 15;
}
