using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProfileMirrorSync.Models;

namespace ProfileMirrorSync.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented         = true,
        AllowTrailingCommas   = true,
        ReadCommentHandling   = JsonCommentHandling.Skip,
        Converters            = { new JsonStringEnumConverter() }  // store enums as strings
    };

    public string SettingsDirectory { get; }
    public string SettingsPath      { get; }
    public string LogsDirectory     { get; }

    public SettingsStore()
        : this(AppPaths.DataDirectory)
    {
    }

    /// <summary>
    /// Test seam.  Lets the test project point the store at an isolated
    /// temp directory instead of the real per-user data dir, so atomic-save and
    /// corrupt-file-recovery paths can be exercised without touching the machine.
    /// Production always uses the parameterless constructor.
    ///
    /// Settings live per-user under %LocalAppData%\ProfileMirrorSync, and so do
    /// logs (AppPaths.LogsDirectory).  When a custom baseDirectory is supplied
    /// (tests), logs stay under it for isolation.
    /// </summary>
    internal SettingsStore(string baseDirectory)
    {
        SettingsDirectory = baseDirectory;
        SettingsPath      = Path.Combine(baseDirectory, "settings.json");
        LogsDirectory     = baseDirectory == AppPaths.DataDirectory
            ? AppPaths.LogsDirectory
            : Path.Combine(baseDirectory, "Logs");
    }

    /// <summary>
    /// Optional logger for diagnostic messages. May be null when SettingsStore
    /// is used before Logger is constructed (Program.cs bootstrap order).
    /// </summary>
    public Logger? Log { get; set; }

    public AppSettings Load()
    {
        Directory.CreateDirectory(SettingsDirectory);
        Directory.CreateDirectory(LogsDirectory);

        // Primary location
        if (File.Exists(SettingsPath))
        {
            var s = TryDeserialize(SettingsPath);
            MigrateIfNeeded(s);
            return s;
        }

        // Fallback / one-time migration: a pre-2.5.3 build kept settings in the
        // SHARED %ProgramData%\ProfileMirrorSync\settings.json.  If the per-user
        // file doesn't exist yet but a shared one does, adopt it (and copy it
        // into the per-user location via Save) so existing installs keep their
        // configuration.  Only the CURRENT user adopts it; other users start
        // from defaults, which is the correct multi-user behaviour.
        string legacyShared = Path.Combine(AppPaths.LegacySharedDirectory, "settings.json");
        if (File.Exists(legacyShared))
        {
            var s = TryDeserialize(legacyShared);
            MigrateIfNeeded(s);
            Save(s);
            Log?.Info($"Настройки перенесены из общего расположения ({legacyShared}) " +
                      $"в пользовательское ({SettingsPath}).");
            return s;
        }

        return new AppSettings();
    }

    /// <summary>
    /// Atomic save: write temp file → rename over destination.
    /// Prevents settings loss on power failure / BSOD mid-write.
    /// On rename failure, attempts a direct File.WriteAllText as a last resort.
    ///
    /// Logs a snapshot of key tunables BEFORE writing and AFTER the
    /// rename completes.  Surfaces "I changed X but Y didn't happen" claims
    /// in the log so they can be diff'd against the next save and against
    /// the SyncController's runtime config dump.
    /// </summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);

        // Trace what we're about to write.  Captured BEFORE the file
        // hits disk so a crash between the trace and the rename still leaves
        // an audit trail of the operator's intent.
        Log?.Info($"Settings save (before write): {SummariseSettings(settings)}");

        string json = JsonSerializer.Serialize(settings, JsonOptions);
        string tmp  = SettingsPath + ".tmp";

        try
        {
            File.WriteAllText(tmp, json, Encoding.UTF8);
            // File.Move overwrite:true is atomic on Windows when src and dst are
            // on the same volume (uses MoveFileEx with MOVEFILE_REPLACE_EXISTING).
            // For local %ProgramData% writes this is always true.
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log?.Warn($"Atomic settings save failed: {ex.Message}. Falling back to direct write.", ex);
            try { File.WriteAllText(SettingsPath, json, Encoding.UTF8); }
            catch (Exception ex2) { Log?.Error("Settings save failed completely.", ex2); throw; }
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }

        // Re-read from disk and trace what the file ACTUALLY contains.
        // Closes the loop: if serialization stripped fields or migration
        // misbehaved, the diff between before/after will show it.
        try
        {
            var persisted = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(SettingsPath, Encoding.UTF8), JsonOptions);
            if (persisted is not null)
                Log?.Info($"Settings save (after write):  {SummariseSettings(persisted)}");
        }
        catch (Exception ex)
        {
            Log?.Warn($"Settings post-save verify read failed: {ex.Message}");
        }
    }

    /// <summary>One-line summary of the fields operators actually tune.
    /// Keep stable: the changelog promises this format for diffing.</summary>
    public static string Summarise(AppSettings s) => SummariseSettings(s);

    private static string SummariseSettings(AppSettings s)
    {
        // Order: network/bandwidth → schedule → throttle → folders → publish/archive.
        return
            $"BW={s.MaxBandwidthBitsPerSecond / 1_000_000.0:F2}Mbit " +
            $"Turbo={(s.TurboFirstRunEnabled ? $"{s.TurboFirstRunBandwidthMbps}Mbit@{s.TurboThresholdFiles}{(s.TurboOnReconcile ? "+recon" : "")}" : "off")} " +
            $"EvtTrig=[{(s.ReconcileOnWake?"W":"-")}{(s.ReconcileOnUnlock?"U":"-")}{(s.ReconcileOnLogon?"L":"-")}] " +
            $"Interval={s.ReconcileIntervalMinutes}min{(s.ReconcileEnabled ? "" : "(off)")} " +
            $"Jitter={s.ReconcileJitterPercent}% " +
            $"SkipStartupReconcile<{s.SkipStartupReconcileIfWithinMinutes}min " +
            $"Delay/Batch/Pause={s.ReconcileFileDelayMs}/{s.ReconcileBatchSize}/{s.ReconcileBatchPauseMs}ms " +
            $"Debounce={s.FileDebounceMilliseconds}ms " +
            $"Queue={s.QueueCapacity} " +
            $"EarlyTrig={s.EarlyReconcileQueueThresholdPct}%/{s.EarlyReconcileMinGapMinutes}min " +
            $"Folders=[{(s.MirrorDesktop?"D":"-")}{(s.MirrorDocuments?"o":"-")}{(s.MirrorDownloads?"l":"-")}" +
            $"{(s.MirrorPictures?"P":"-")}{(s.MirrorVideos?"V":"-")}{(s.MirrorMusic?"M":"-")}" +
            $"{(s.MirrorFavorites?"F":"-")}{(s.MirrorContacts?"C":"-")}{(s.MirrorSavedGames?"S":"-")}" +
            $"{(s.MirrorAppDataRoaming?"R":"-")}{(s.MirrorAppDataLocal?"L":"-")}{(s.MirrorAppDataLocalLow?"w":"-")}]" +
            $"+{s.CustomFolderPaths.Count}custom " +
            $"Log={s.LogLevel}/Trace={s.EnableTraceMode}/Retain={s.LogRetentionDays}d " +
            $"Publish={s.PublishMode} " +
            $"PostSync={(s.PostSyncEnabled ? $"on@{s.PostSyncIntervalMinutes}min" : "off")} " +
            $"IO=Lower:{s.LowerIoPriority} " +
            $"Resume={(s.ResumeEnabled ? $"≥{s.ResumeMinFileSizeBytes / (1024*1024)}MB" : "off")} " +
            $"MirrorLogs={(s.MirrorLogs ? "on" : "off")} " +
            $"RegBackup={(s.MirrorRegistrySnapshots ? (s.RegistryBackupIntervalMinutes > 0 ? $"{s.RegistryBackupIntervalMinutes / 1440}d" : "everyReconcile") : "off")} " +
            $"Stats={(s.StatsWindowEnabled ? $"on@{s.StatsRefreshIntervalMs}ms" : "off")} " +
            $"StartMin={s.StartMinimizedToTray} " +
            $"v={s.SettingsVersion}";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void MigrateIfNeeded(AppSettings s)
    {
        // v2.0 → v2.1: migrate DebugLoggingEnabled bool → AppLogLevel enum
        if (s.SettingsVersion < 3 && s.DebugLoggingEnabled is bool dbg)
        {
            s.LogLevel = dbg ? AppLogLevel.Debug : AppLogLevel.Info;
            s.DebugLoggingEnabled = null;
            s.SettingsVersion = 3;
        }
        // v2.2 → v2.3: no field-level migration needed (no new settings introduced);
        // bump the version number so downstream tooling can see the upgrade.
        if (s.SettingsVersion < 4)
        {
            s.SettingsVersion = 4;
        }
        // v2.3 →: EnableTraceMode defaults to false via the property
        // initializer; just bump the version number.
        if (s.SettingsVersion < 5)
        {
            s.SettingsVersion = 5;
        }
        // v2.3.x → v2.4: monitoring + queue capacity + jitter — all new fields
        // inherit defaults via property initializers. Bump version.
        if (s.SettingsVersion < 6)
        {
            s.SettingsVersion = 6;
        }
        // → v2.4.2: adaptive reconcile + IO priority + new defaults.
        // We deliberately DO NOT force-update existing ReconcileIntervalMinutes,
        // MaxBandwidthBitsPerSecond, or TurboFirstRunBandwidthMbps to the new
        // defaults — existing users may have intentionally tuned these.  New
        // fields (EarlyReconcileQueueThresholdPct, EarlyReconcileMinGapMinutes,
        // LowerIoPriority) get default values via the property initializers.
        if (s.SettingsVersion < 7)
        {
            s.SettingsVersion = 7;
        }
        // → v2.4.5: tightened folder defaults for the 10-PC/30-Mbit
        // corporate scenario.  Crucially, we DO NOT flip existing users'
        // Mirror* flags from true to false during migration — silently
        // disabling someone's enabled backup folders would be a data-loss-
        // *feel* incident even if no data is technically lost.  Only fresh
        // installs (no settings.json yet) see the new defaults; existing
        // users' choices are preserved.
        if (s.SettingsVersion < 8)
        {
            s.SettingsVersion = 8;
        }
        // → v2.4.6: new SkipStartupReconcileIfWithinMinutes field
        // (default 5 via property initializer).  No flag flips for existing
        // users; their behaviour transitions naturally to the new default,
        // which is the safe direction (skips redundant reconciles).
        if (s.SettingsVersion < 9)
        {
            s.SettingsVersion = 9;
        }
        // → v2.4.11: new MirrorLogs field (default false via property
        // initializer).  Optional feature; existing users keep current
        // behaviour until they explicitly enable it from the UI.
        if (s.SettingsVersion < 10)
        {
            s.SettingsVersion = 10;
        }
        // → v2.4.12: new fields RegistryBackupIntervalMinutes,
        // StatsWindowEnabled, StatsRefreshIntervalMs — all inherit defaults
        // via property initializers.  No flag flips for existing users:
        //   • RegistryBackupIntervalMinutes default 43200 (30 d) reduces
        //     registry traffic for everyone, including existing users.  This
        //     is a *safer* change (less server load), not a behaviour-flip
        //     that could surprise an admin.
        //   • StatsWindowEnabled defaults to true — adds a tray menu item but
        //     doesn't do any work until the user clicks it.
        if (s.SettingsVersion < 11)
        {
            s.SettingsVersion = 11;
        }
        // v2.4.x →: the security-monitoring subsystem was removed; new
        // fields PublishMode (DirectWrite), PostSync* (disabled) inherit safe
        // defaults via property initializers.  Any Monitor* keys still present
        // in an old settings.json are simply ignored on deserialize.  No
        // behaviour flips for existing users: PublishMode defaults to the
        // legacy DirectWrite, PostSync is off.
        if (s.SettingsVersion < 12)
        {
            s.SettingsVersion = 12;
        }
        // → v2.5.1: turbo defaults retuned (3 Mbit/1000 files) and new
        // fields TurboOnReconcile, ReconcileOnWake/Unlock/Logon.  We DO NOT
        // overwrite an existing user's tuned TurboFirstRunBandwidthMbps or
        // TurboThresholdFiles — only fresh installs get the new defaults.
        //
        // Deliberate behaviour change for the event triggers: pre-2.5.1, unlock
        // AND logon ALWAYS triggered a reconcile (hardcoded).  The new defaults
        // are ReconcileOnUnlock=false / ReconcileOnLogon=false (and
        // ReconcileOnWake=true, preserving the wake-from-sleep behaviour).  This
        // is the one place we intentionally reduce an existing user's reconcile
        // frequency — it directly serves the "no unnecessary reconciliation"
        // requirement and only removes redundant sweeps (FSW stays live while
        // the screen is merely locked).  Users who relied on unlock/logon sweeps
        // can re-enable them on the Performance tab.
        if (s.SettingsVersion < 13)
        {
            s.SettingsVersion = 13;
        }
        // → v2.5.2: new field ReconcileEnabled (default true = existing
        // behaviour, no change for upgraders). Archive presets are UI-only and
        // not persisted, so nothing to migrate there.
        if (s.SettingsVersion < 14)
        {
            s.SettingsVersion = 14;
        }
        // → v2.5.3: no new settings fields.  The change is structural —
        // settings/state/logs moved from shared %ProgramData% to per-user
        // %LocalAppData%.  The cross-location
        // copy is handled in Load() (legacy-shared adoption), not here.
        if (s.SettingsVersion < 15)
        {
            s.SettingsVersion = 15;
        }
    }

    private AppSettings TryDeserialize(string path)
    {
        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            // Don't swallow.  Settings parse failure means user loses
            // every customized field; we MUST surface this.  Also back up the
            // bad file so the user can recover by hand if needed.
            string backup = path + $".bad-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            try { File.Copy(path, backup, overwrite: false); } catch { }
            Log?.Error($"Settings parse failed at '{path}': {ex.Message}. " +
                       $"Bad file saved as '{Path.GetFileName(backup)}'. " +
                       $"Using defaults.", ex);
            return new AppSettings();
        }
    }
}
