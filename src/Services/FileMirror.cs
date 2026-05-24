namespace ProfileMirrorSync.Services;

/// <summary>High-level mirror operations: create/change, delete, rename, throttled reconcile.</summary>
public sealed class FileMirror
{
    private readonly Logger              _log;
    private readonly ThrottledFileCopier _copier;
    private readonly int                 _retryCount;
    private readonly bool                _deletionSafetyGuard;  // opt-in
    private const int RetryDelayBase = 200; // ms

    public FileMirror(Logger log, ThrottledFileCopier copier, int retryCount,
        bool deletionSafetyGuard = false)
    {
        _log                 = log;
        _copier              = copier;
        _retryCount          = Math.Max(1, retryCount);
        _deletionSafetyGuard = deletionSafetyGuard;
    }

    /// <summary>
    /// Mirror a file/dir create-or-change from src to dst.
    /// Returns the number of BYTES written when an actual copy happened; 0
    /// otherwise (directory create, src missing, dst already up-to-date).
    /// return value added so the real-time FSW path can credit
    /// the stats counters in <see cref="SyncController.StatsSnapshot"/>.
    /// </summary>
    public async Task<long> MirrorCreateOrChangeAsync(string src, string dst, CancellationToken ct)
    {
        if (Directory.Exists(src))
        {
            Directory.CreateDirectory(dst);
            // Best-effort timestamp propagation at creation time.
            // Note: file writes inside this directory will subsequently reset its
            // LastWriteTime — the authoritative fix is Pass 3 of ReconcileRootAsync.
            TryPropagateDirectoryTimestamps(src, dst);
            return 0;
        }
        if (!File.Exists(src)) return 0;
        if (IsUpToDate(src, dst)) return 0;

        // Snapshot length before the copy so we can credit stats even if the
        // file is modified during/after.  Read length is best-effort; on
        // failure we still return 0 (counter unaffected, copy still credited
        // via filesCopied if the higher-level path tracks it).
        long bytes = 0;
        try { bytes = new FileInfo(src).Length; } catch { }

        await RetryAsync($"copy {src}", async () =>
        {
            await _copier.CopyAsync(src, dst, ct).ConfigureAwait(false);
            _log.Debug($"Скопировано: {src}");
        }, ct).ConfigureAwait(false);

        return bytes;
    }

    /// <summary>
    /// Delete a mirrored file or directory.  Single-shot (no retry by design):
    /// transient delete failures (AV scan locking the file, etc.) become
    /// orphans on the destination side and are cleaned up by Pass 2 of the
    /// next reconcile.  Repeated delete attempts could mask other failures
    /// or amplify them, so we deliberately log Warn and move on.
    /// Behavior unchanged; contract now explicit.
    /// Returns true when something was actually deleted (used by
    /// SyncController for the live FilesDeleted stat).  Failures and
    /// "nothing-to-delete" both return false.
    /// </summary>
    public Task<bool> MirrorDeleteAsync(string _src, string dst, CancellationToken _ct)
    {
        try
        {
            if (File.Exists(dst))
            {
                File.Delete(dst);
                _log.Debug($"Удалён файл: {dst}");
                return Task.FromResult(true);
            }
            if (Directory.Exists(dst))
            {
                Directory.Delete(dst, recursive: true);
                _log.Debug($"Удалена папка: {dst}");
                return Task.FromResult(true);
            }
        }
        catch (Exception ex) { _log.Warn($"Ошибка удаления {dst}: {ex.Message}"); }
        return Task.FromResult(false);
    }

    public Task MirrorRenameAsync(string oldDst, string newSrc, string newDst, CancellationToken _ct)
    {
        try
        {
            if (File.Exists(oldDst))
            {
                string? d = Path.GetDirectoryName(newDst);
                if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                // Avoid File.Move(overwrite:true) — that maps to MoveFileEx(MOVEFILE_REPLACE_EXISTING)
                // which vboxsf and some NAS firmwares reject with NotSupportedException.
                // Delete target first, then plain rename.
                if (File.Exists(newDst))
                {
                    // Track delete success; bail with a
                    // clear message if it failed.  Previously a failed Delete
                    // (file locked by AV scanner, etc.) was logged as Warn
                    // and we proceeded to File.Move, which then threw "file
                    // already exists" — confusing duplicate log line with
                    // unclear root cause.
                    try
                    {
                        File.Delete(newDst);
                    }
                    catch (Exception delEx)
                    {
                        _log.Warn($"Не удалось удалить {newDst} перед переименованием: {delEx.Message}. " +
                                  $"Rename отложен; будет повторён реконсиляцией.", delEx);
                        return Task.CompletedTask;
                    }
                }
                File.Move(oldDst, newDst);
                _log.Debug($"Переименован: {oldDst} → {newDst}");
            }
            else if (Directory.Exists(oldDst))
            {
                Directory.Move(oldDst, newDst);
                _log.Debug($"Папка переименована: {oldDst} → {newDst}");
            }
        }
        catch (Exception ex) { _log.Warn($"Ошибка переименования {oldDst}: {ex.Message}", ex); }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Full reconciliation with configurable throttling.
    /// Files are processed in batches separated by pauses to avoid I/O spikes.
    /// Callback onFileCopied is invoked after each file copy (for turbo-mode tracking).
    ///
    /// Three passes:
    ///   1. Copy new / changed files (rate-limited, batched).
    ///   2. Delete orphan destination files (those with no corresponding source).
    ///   3. Propagate directory timestamps from source to destination, bottom-up.
    ///      Must run last because file writes reset the parent directory's
    ///      LastWriteTime; bottom-up because setting a parent's timestamp before
    ///      modifying its children would be undone by those modifications.
    ///
    /// Uses safe directory enumeration that skips reparse points (junctions, symlinks)
    /// and directories for which we lack access — preventing UnauthorizedAccessException
    /// and "Specified method is not supported" on shell-virtual folders.
    ///
    /// Returns a <see cref="ReconcileSummary"/> so the caller can
    /// credit live-stats counters once per cycle (FilesCopied, BytesCopied,
    /// OrphansDeleted, FilesSkipped, DirsTouched).
    /// </summary>
    public async Task<ReconcileSummary> ReconcileRootAsync(
        string srcRoot, string dstRoot,
        Func<string, bool> shouldIgnore,
        ReconcileOptions opts,
        Action? onFileCopied,
        CancellationToken ct)
    {
        if (!Directory.Exists(srcRoot)) return ReconcileSummary.Empty;
        _log.Info($"Реконсиляция: {srcRoot} → {dstRoot}  (delay={opts.FileDelayMs}ms batch={opts.BatchSize}×{opts.BatchPauseMs}ms)");

        int  filesCopied  = 0;
        long bytesCopied  = 0;   // credited to stats by caller
        int  filesSkipped = 0;
        int  filesWalked  = 0;
        var pass1Stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // track throttle time separately so the timing trace shows
        // I/O time vs intentional delay time. Helps distinguish
        // "program is slow" from "program is correctly throttled".
        long throttleMs = 0;

        // ── Pass 1: copy new / changed files ──────────────────────────────────
        // per-file Task.Delay applies ONLY when we actually copied a
        // file.  Iterating over an up-to-date tree of 100k files previously
        // burned ~33 minutes of pure delays per reconcile.  With this fix the
        // walk runs at filesystem-stat speed and we throttle only the real
        // I/O work.  Same logic as Pass 2 fix in.
        foreach (string srcFile in SafeEnumerateFiles(srcRoot))
        {
            ct.ThrowIfCancellationRequested();
            filesWalked++;
            if (shouldIgnore(srcFile)) continue;

            string rel     = Path.GetRelativePath(srcRoot, srcFile);
            string dstFile = Path.Combine(dstRoot, rel);

            if (IsUpToDate(srcFile, dstFile)) continue;

            // snapshot length before copy for byte-counting.
            // Same justification as MirrorCreateOrChangeAsync: counter
            // is informational and tolerant of mid-copy modifications.
            long thisFileBytes = 0;
            try { thisFileBytes = new FileInfo(srcFile).Length; } catch { }

            bool copied = false;
            try
            {
                await _copier.CopyAsync(srcFile, dstFile, ct).ConfigureAwait(false);
                onFileCopied?.Invoke();
                filesCopied++;
                bytesCopied += thisFileBytes;
                copied = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                filesSkipped++;
                _log.Warn($"Реконсиляция: пропущен {srcFile}: [{ex.GetType().Name}] {ex.Message}", ex);
            }

            // Only yield when we did real I/O (a copy attempt — whether
            // succeeded or failed).  Skipped up-to-date files don't need
            // throttling because they cost no bandwidth and no server load.
            if (copied && opts.CurrentFileDelayMs > 0)
            {
                throttleMs += opts.CurrentFileDelayMs;
                await Task.Delay(opts.CurrentFileDelayMs, ct).ConfigureAwait(false);
            }

            // Batch pause: longer rest after N files actually copied
            if (opts.BatchSize > 0 && opts.CurrentBatchPauseMs > 0 && filesCopied > 0 && filesCopied % opts.BatchSize == 0)
            {
                _log.Debug($"Реконсиляция: пауза после {filesCopied} файлов ({opts.CurrentBatchPauseMs} мс)");
                throttleMs += opts.CurrentBatchPauseMs;
                await Task.Delay(opts.CurrentBatchPauseMs, ct).ConfigureAwait(false);
            }
        }
        pass1Stopwatch.Stop();
        // Pass 1 timing trace split — distinguish actual I/O work
        // from intentional throttle delays. When 0 files are
        // copied, throttleMs should be 0 and the walk should be milliseconds.
        long totalMs    = pass1Stopwatch.ElapsedMilliseconds;
        long ioMs       = Math.Max(0, totalMs - throttleMs);
        _log.Debug($"Pass 1: пройдено {filesWalked} файлов, скопировано {filesCopied}, " +
                   $"пропущено {filesSkipped}, время {totalMs} мс " +
                   $"(I/O ~{ioMs} мс + троттлинг ~{throttleMs} мс)");

        // ── Pass 2: remove orphan destination files ────────────────────────────
        // Apply Task.Delay only when an actual deletion happened.
        //
        // EMPTY-SOURCE SANITY GUARD.  This is a
        // ONE-WAY MIRROR: Pass 2 deletes every dst file with no live src
        // counterpart.  If the source root exists but Pass 1 walked ZERO files
        // (profile not fully loaded at ONLOGON, OneDrive placeholders
        // dehydrated, AV quarantine, ACL slip, junction dropped), deleting all
        // of dst would wipe the only server-side copy of the user's data.  When
        // the source looks empty but the destination has files, we SKIP Pass 2
        // (and Pass 2b) entirely and log a Warn — the next reconcile retries
        // once the source is readable again.  filesWalked counts every source
        // file SafeEnumerateFiles yielded (before shouldIgnore), so a tree that
        // is genuinely all-ignored still has filesWalked > 0 and does not trip
        // the guard.
        int orphansDeleted = 0;
        int dstScanned = 0;
        bool emptySourceGuardTripped = false;
        if (Directory.Exists(dstRoot))
        {
            bool dstHasFiles = _deletionSafetyGuard && SafeEnumerateFiles(dstRoot).Any();
            if (filesWalked == 0 && dstHasFiles)
            {
                emptySourceGuardTripped = true;
                _log.Warn($"Реконсиляция: источник '{srcRoot}' не дал ни одного файла, " +
                          $"но в назначении '{dstRoot}' файлы есть — удаление-сирот ПРОПУЩЕНО " +
                          "во избежание потери данных (источник недоступен/не загружен). " +
                          "Будет повторено при следующей реконсиляции.");
            }
            else
            {
                foreach (string dstFile in SafeEnumerateFiles(dstRoot))
                {
                    ct.ThrowIfCancellationRequested();
                    dstScanned++;
                    string rel     = Path.GetRelativePath(dstRoot, dstFile);
                    string srcFile = Path.Combine(srcRoot, rel);

                    // Delete if (a) the corresponding source file no longer
                    // exists, OR (b) it is an actively-ignored path on the
                    // source side (cleans up leftover .pms_tmp and build
                    // artefacts that pre-date the current exclude policy).
                    if (!File.Exists(srcFile) || shouldIgnore(srcFile))
                    {
                        try
                        {
                            File.Delete(dstFile);
                            orphansDeleted++;
                            if (opts.CurrentFileDelayMs > 0)
                                await Task.Delay(Math.Max(1, opts.CurrentFileDelayMs / 4), ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) { _log.Warn($"Реконсиляция: ошибка удаления {dstFile}: [{ex.GetType().Name}] {ex.Message}", ex); }
                    }
                }
            }
        }
        _log.Debug($"Pass 2: проверено {dstScanned} файлов, удалено {orphansDeleted}");

        // ── Pass 2b: remove empty orphan directories ───────────────────────────
        // Pass 2 deletes orphan FILES; empty dirs whose source
        // counterpart no longer exists used to accumulate on dst forever
        // (only FSW Deleted events for dirs could clean them up, and FSW
        // misses events fired while PMS was off).  Process bottom-up so
        // child dirs are checked first — when a child empty dir is removed,
        // its parent may become empty and get caught on the next pass.
        // Counts toward OrphansDeleted (same semantic: "removed from dst
        // because not on src").
        // Pass 2b is also suppressed by the empty-source guard: if the
        // source produced no files we cannot trust "src dir missing" as a real
        // orphan signal.
        if (!emptySourceGuardTripped && Directory.Exists(dstRoot))
        {
            var dstDirs = new List<string>();
            CollectDirectoriesSafe(dstRoot, dstDirs);
            dstDirs.Sort((a, b) => CountSeparators(b).CompareTo(CountSeparators(a)));
            int emptyDirsRemoved = 0;
            foreach (string dstDir in dstDirs)
            {
                ct.ThrowIfCancellationRequested();
                // Never delete the dst root itself — that's the user's
                // backup folder, not an orphan.
                if (string.Equals(dstDir, dstRoot, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    string rel    = Path.GetRelativePath(dstRoot, dstDir);
                    string srcDir = Path.Combine(srcRoot, rel);
                    if (Directory.Exists(srcDir)) continue;            // src still has it → keep
                    if (Directory.EnumerateFileSystemEntries(dstDir).Any()) continue; // not empty → keep
                    Directory.Delete(dstDir, recursive: false);
                    emptyDirsRemoved++;
                    orphansDeleted++;
                    // throttle empty-dir removal too, so
                    // EVERY IO-producing reconcile loop has a regulated delay
                    // (matches Pass 2's deletion cadence: FileDelayMs/4 per op).
                    if (opts.CurrentFileDelayMs > 0)
                        await Task.Delay(Math.Max(1, opts.CurrentFileDelayMs / 4), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* skip locked / vanished — next reconcile will retry */ }
            }
            if (emptyDirsRemoved > 0)
                _log.Debug($"Pass 2b: удалено пустых сиротских папок {emptyDirsRemoved}");
        }

        // ── Pass 3: propagate directory timestamps (bottom-up) ─────────────────
        int dirsTouched = 0;
        int dirsScanned = 0;
        // track throttle time in Pass 3 as well so the timing trace
        // shows whether the time spent is server-IO or intentional delay.
        long pass3ThrottleMs = 0;
        var pass3Stopwatch = System.Diagnostics.Stopwatch.StartNew();
        if (Directory.Exists(dstRoot))
        {
            // Collect source dirs in any order, then sort by depth (separator
            // count) descending so we touch the deepest dirs first.  Setting
            // a parent's timestamp before its child writes would be undone
            // by those writes.
            //
            // sort by SEPARATOR COUNT, not string
            // length.  Length is not equivalent to depth: a single dir with
            // a long name ("C:\verylongnamehere\sub", length 24, depth 3)
            // would sort before a deeply-nested short-named tree
            // ("C:\a\b\c\d\e", length 12, depth 6), causing the deeper
            // dir's timestamp to be set AFTER its parent — undoing Pass 3.
            var srcDirs = new List<string>();
            CollectDirectoriesSafe(srcRoot, srcDirs);
            srcDirs.Sort((a, b) => CountSeparators(b).CompareTo(CountSeparators(a)));

            foreach (string srcDir in srcDirs)
            {
                ct.ThrowIfCancellationRequested();
                dirsScanned++;
                string rel    = Path.GetRelativePath(srcRoot, srcDir);
                string dstDir = rel == "." ? dstRoot : Path.Combine(dstRoot, rel);
                if (!Directory.Exists(dstDir)) continue;
                if (TryPropagateDirectoryTimestamps(srcDir, dstDir))
                {
                    dirsTouched++;
                    // Throttle metadata IO storm on SMB.  Each
                    // SetLastWriteTimeUtc on a remote dir is one round-trip;
                    // on a 30 ms RTT link, 200 dirs back-to-back = 6 s of pure
                    // metadata IO storming the server.  A quarter of the
                    // file-copy delay is a sensible inter-dir gap — it has
                    // zero bandwidth cost and lets the server breathe.
                    if (opts.CurrentFileDelayMs > 0)
                    {
                        int dirDelayMs = Math.Max(1, opts.CurrentFileDelayMs / 4);
                        pass3ThrottleMs += dirDelayMs;
                        await Task.Delay(dirDelayMs, ct).ConfigureAwait(false);
                    }
                    // Honour the same batch-pause cadence as Pass 1 so the
                    // server sees a uniform "small batches + pause" pattern.
                    if (opts.BatchSize > 0 && opts.CurrentBatchPauseMs > 0
                        && dirsTouched > 0 && dirsTouched % opts.BatchSize == 0)
                    {
                        pass3ThrottleMs += opts.CurrentBatchPauseMs;
                        await Task.Delay(opts.CurrentBatchPauseMs, ct).ConfigureAwait(false);
                    }
                }
            }
        }
        pass3Stopwatch.Stop();
        // split Pass 3 timing trace (mirrors Pass 1's format) so
        // operators can tell "slow server" from "correctly throttled".
        long pass3TotalMs = pass3Stopwatch.ElapsedMilliseconds;
        long pass3IoMs    = Math.Max(0, pass3TotalMs - pass3ThrottleMs);
        _log.Debug($"Pass 3: пройдено {dirsScanned} директорий, обновлено {dirsTouched}, " +
                   $"время {pass3TotalMs} мс " +
                   $"(I/O ~{pass3IoMs} мс + троттлинг ~{pass3ThrottleMs} мс)");

        _log.Info($"Реконсиляция завершена: {srcRoot}  " +
                  $"({filesCopied} скопировано, {filesSkipped} ошибок, " +
                  $"{orphansDeleted} удалено, {dirsTouched} папок: даты)");

        return new ReconcileSummary(
            FilesCopied:     filesCopied,
            BytesCopied:     bytesCopied,
            FilesSkipped:    filesSkipped,
            OrphansDeleted:  orphansDeleted,
            DirsTouched:     dirsTouched);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// True if dst is "current enough" relative to src.
    ///
    /// Primary check: identical size AND timestamps within 2 s.
    ///
    /// Fallback (filesystem doesn't preserve timestamps): identical size AND
    /// dst.LastWriteTimeUtc >= src.LastWriteTimeUtc.  This treats a destination
    /// stamped "now" (because the FS rewrote it on copy) as up-to-date for the
    /// current source revision; if the source is later modified its
    /// LastWriteTime moves forward and the check fails, triggering a recopy.
    /// </summary>
    public static bool IsUpToDate(string src, string dst)
    {
        if (!File.Exists(dst)) return false;
        try
        {
            var si = new FileInfo(src);
            var di = new FileInfo(dst);
            if (si.Length != di.Length) return false;

            double deltaSec = (si.LastWriteTimeUtc - di.LastWriteTimeUtc).TotalSeconds;
            if (Math.Abs(deltaSec) < 2) return true;                  // primary
            if (di.LastWriteTimeUtc >= si.LastWriteTimeUtc) return true; // fallback for non-preserving FS
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Best-effort propagation of source directory CreationTime + LastWriteTime
    /// to the destination directory.  Returns true on success.
    ///
    /// Set order matters on Windows: LastWriteTime FIRST, then CreationTime.
    /// Setting LastWriteTime touches metadata which can in turn refresh
    /// CreationTime on some filesystems — so we set CreationTime last.
    /// </summary>
    private static bool TryPropagateDirectoryTimestamps(string srcDir, string dstDir)
    {
        try
        {
            var si = new DirectoryInfo(srcDir);
            DateTime created  = si.CreationTimeUtc;
            DateTime modified = si.LastWriteTimeUtc;

            // skip the write (and the per-dir throttle it
            // incurs) when the destination LastWriteTime already matches the
            // source within 2 s.  Previously this ran SetLastWriteTimeUtc on
            // EVERY directory on EVERY reconcile — e.g. 202 dirs + ~5 s of
            // SMB metadata round-trips per pass even when 0 files changed.
            // One cheap GetLastWriteTimeUtc stat per dir replaces one write +
            // its throttle pause; on a stable tree Pass 3 becomes near-free.
            try
            {
                DateTime dstModified = Directory.GetLastWriteTimeUtc(dstDir);
                if (Math.Abs((dstModified - modified).TotalSeconds) < 2)
                    return false;   // already current → not touched
            }
            catch { /* fall through and attempt the write */ }

            try { Directory.SetLastWriteTimeUtc(dstDir, modified); } catch { return false; }
            try { Directory.SetCreationTimeUtc(dstDir, created);   } catch { /* non-fatal */ }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Enumerates all files under <paramref name="root"/> recursively,
    /// skipping directories that are reparse points (junctions / symlinks) or
    /// that are not accessible due to permissions.
    ///
    /// Background: Windows shell virtual folders (e.g. "Мои видеозаписи" in
    /// localised profiles) are NTFS junction points.  Calling
    /// Directory.EnumerateFiles with SearchOption.AllDirectories crosses them,
    /// which either throws UnauthorizedAccessException or NotSupportedException
    /// depending on the target.  Manually walking the tree lets us skip them.
    /// </summary>
    private static IEnumerable<string> SafeEnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly); }
            catch { continue; }
            foreach (string f in files) yield return f;

            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly); }
            catch { continue; }

            foreach (string sub in subdirs)
            {
                try
                {
                    var di = new DirectoryInfo(sub);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                stack.Push(sub);
            }
        }
    }

    /// <summary>
    /// Adds <paramref name="root"/> and all its non-reparse-point descendant
    /// directories to <paramref name="output"/>.  Mirror of SafeEnumerateFiles
    /// but for directories — used by Pass 3 of ReconcileRootAsync.
    /// </summary>
    private static void CollectDirectoriesSafe(string root, List<string> output)
    {
        output.Add(root);
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly); }
            catch { continue; }

            foreach (string sub in subdirs)
            {
                try
                {
                    var di = new DirectoryInfo(sub);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                output.Add(sub);
                stack.Push(sub);
            }
        }
    }

    /// <summary>
    /// Counts directory separators in a path.  Used by Pass 3 of
    /// ReconcileRootAsync to sort source directories by depth (so the
    /// deepest dirs are touched first and their parents' timestamps are
    /// not subsequently overwritten by writes to children).
    /// </summary>
    internal static int CountSeparators(string path)
    {
        int n = 0;
        foreach (char c in path) if (c == '\\' || c == '/') n++;
        return n;
    }

    /// <summary>
    /// High-level retry-with-backoff for a mirror action (default 5 attempts,
    /// delays 200/400/800/1600 ms; the final attempt rethrows).
    ///
    /// NOTE the nested retry: when the action is a copy,
    /// <see cref="ThrottledFileCopier.CopyAsync"/> has its OWN inner retry layer
    /// (3 attempts, 1 s/2 s delays) for transient network IOExceptions.  Worst
    /// case the two layers combine to ~18 s of backoff and up to 15 attempts on
    /// a single file before the exception finally propagates.  This is
    /// intentional (inner = transient SMB glitches, outer = broader failures),
    /// but be aware of the combined timing when tuning RetryCount.
    /// </summary>
    private async Task RetryAsync(string label, Func<Task> action, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= _retryCount; attempt++)
        {
            try { await action().ConfigureAwait(false); return; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < _retryCount)
            {
                int delay = RetryDelayBase * (1 << (attempt - 1));
                _log.Warn($"Попытка {attempt}/{_retryCount} [{label}]: {ex.Message}. Повтор через {delay} мс.");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            // Final attempt failed.  Previously this
            // catch logged Error and returned silently, making the caller
            // (MirrorCreateOrChangeAsync via FSW path) believe the copy
            // succeeded — the file was counted in stats and FileProcessed
            // fired, but the destination had no file.  Now we rethrow so the
            // outer DispatchAsync catch records the real failure and the
            // next reconcile picks up the missing file.
            catch (Exception ex)
            {
                _log.Error($"Не удалось [{label}] после {_retryCount} попыток", ex);
                throw;
            }
        }
    }
}

/// <summary>Parameters controlling how aggressively reconciliation uses I/O.</summary>
public sealed record ReconcileOptions(int FileDelayMs, int BatchSize, int BatchPauseMs)
{
    public static ReconcileOptions Default => new(20, 50, 500);
    public static ReconcileOptions Turbo   => new(0,  0,  0);

    /// <summary>
    /// When turbo is active, the inter-file / batch throttle delays are
    /// suppressed so the raised bandwidth limit can actually be used to drain a
    /// backlog quickly.  Previously turbo lifted only the byte-rate cap while
    /// these fixed Task.Delay pauses stayed in place and dominated throughput
    /// for many small files (visible in logs as "пауза после N файлов" firing
    /// throughout a turbo burst).  The byte-rate limiter still applies, so the
    /// turbo bandwidth cap is respected — we only drop the artificial pauses.
    /// Optional: null ⇒ never in turbo (delays always apply, e.g. in tests).
    /// </summary>
    public Func<bool>? IsTurboActive { get; init; }

    private bool TurboNow => IsTurboActive?.Invoke() == true;

    /// <summary>Per-file delay in effect right now (0 while turbo is active).</summary>
    public int CurrentFileDelayMs  => TurboNow ? 0 : FileDelayMs;
    /// <summary>Batch pause in effect right now (0 while turbo is active).</summary>
    public int CurrentBatchPauseMs => TurboNow ? 0 : BatchPauseMs;
}

/// <summary>
/// Per-root reconcile result.  Returned by
/// <see cref="FileMirror.ReconcileRootAsync"/> so SyncController can credit
/// stats counters (FilesCopied, BytesCopied, FilesDeleted) once per cycle
/// rather than wiring per-file callbacks for each counter.
/// </summary>
public sealed record ReconcileSummary(
    int  FilesCopied,
    long BytesCopied,
    int  FilesSkipped,
    int  OrphansDeleted,
    int  DirsTouched)
{
    public static ReconcileSummary Empty => new(0, 0, 0, 0, 0);
}
