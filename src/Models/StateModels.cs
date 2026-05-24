namespace ProfileMirrorSync.Models;

/// <summary>
/// Persistent state owned by <see cref="ProfileMirrorSync.Services.PersistentStateStore"/>.
/// Stored at %ProgramData%\ProfileMirrorSync\state.json.
///
/// the security-monitor cooldown dictionary was removed together with
/// the monitoring subsystem.  Old state files that still carry a "Cooldowns"
/// key deserialize fine — the extra JSON property is simply ignored.
/// </summary>
public sealed class PersistentState
{
    /// <summary>UTC time of the last successful scheduled reconciliation.
    /// Used to compute the next reconcile time across application restarts.
    /// Null = no reconcile has been recorded yet.</summary>
    public DateTime? LastReconcileUtc { get; set; }

    /// <summary>UTC time when the most-recent reconciliation actually FINISHED
    /// all passes for all roots.  Distinct from <see cref="LastReconcileUtc"/>
    /// which is set when reconciliation BEGINS — that one anchors the schedule
    /// even if the reconcile crashes midway.
    /// Used by the startup logic to skip a redundant startup reconcile when the
    /// previous run finished only seconds/minutes ago (Stop/Start atomicity).</summary>
    public DateTime? LastReconcileCompletedUtc { get; set; }

    /// <summary>Set to true by queue-pressure logic to trigger an early
    /// reconcile.  Cleared by the reconcile loop once consumed.</summary>
    public bool EarlyReconcileRequested { get; set; }

    /// <summary>UTC time when EarlyReconcileRequested was last set — used to
    /// enforce the minimum gap between adaptive triggers.</summary>
    public DateTime? LastEarlyReconcileRequestUtc { get; set; }

    /// <summary>UTC time of the most-recent log file mirror operation.
    /// Used by the once-per-day log copying feature to skip redundant
    /// uploads when reconcile runs multiple times in a single day.</summary>
    public DateTime? LastLogMirrorUtc { get; set; }

    /// <summary>UTC time of the most-recent successful registry-snapshot
    /// capture.  Registry backups run on their own (much longer) schedule
    /// controlled by <c>AppSettings.RegistryBackupIntervalMinutes</c>.</summary>
    public DateTime? LastRegistrySnapshotUtc { get; set; }

    /// <summary>UTC time of the most-recent successful post-sync archive
    /// (external program) launch.  Used by the once-per-interval gate so the
    /// archiver runs at most once per <c>AppSettings.PostSyncIntervalMinutes</c>
    /// even when reconcile runs several times in that window.+.</summary>
    public DateTime? LastPostSyncRunUtc { get; set; }
}

/// <summary>
/// Resume sidecar for a large in-progress copy.  Written next to the
/// destination's hash key under %ProgramData%\PMS\resume\.  Allows a copy
/// interrupted by shutdown to continue from the last persisted offset
/// rather than restart from zero.
///
/// <see cref="SrcHeadHash"/> lets resume survive a growing source (e.g. a
/// download still in progress) without restarting from zero: if the file's
/// first few KB still hash the same and the file only grew (length up, mtime
/// up, prefix unchanged), the previously-copied bytes are trusted and we
/// continue from <see cref="BytesCopied"/>.  In-place rewrites (full
/// overwrite) change the head hash → sidecar discarded → restart.  Empty
/// <see cref="SrcHeadHash"/> means the sidecar was written by an older build;
/// the grow-resume branch requires a non-empty hash, so old sidecars silently
/// fall back to strict-match-only behaviour (no regression).
/// </summary>
public sealed class ResumeState
{
    public string   SrcPath         { get; set; } = "";
    public string   DstPath         { get; set; } = "";
    public long     SrcLength       { get; set; }
    public DateTime SrcLastWriteUtc { get; set; }
    public long     BytesCopied     { get; set; }
    /// <summary>Hex SHA-256 of the first 4 KB of source at copy time.
    /// Empty string for sidecars written by older builds.</summary>
    public string   SrcHeadHash     { get; set; } = "";
    public DateTime UpdatedUtc      { get; set; } = DateTime.UtcNow;
}
