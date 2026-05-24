using ProfileMirrorSync.Models;

namespace ProfileMirrorSync.Services;

/// <summary>
/// Once-per-day mirror of local log files to the destination,
/// extracted from SyncController.
///
/// Copies CLOSED log files to <c>{machineRoot}/Logs/</c> at most once per
/// 23 hours (gate stored in PersistentState.LastLogMirrorUtc).  Today's live
/// log is skipped — it is still being written and a half-written snapshot
/// would confuse readers; it is picked up the day after the midnight roll.
///
/// LastLogMirrorUtc is stamped only when the cycle made actual progress
/// (copied at least one file, or had nothing to copy because everything was
/// already current).  A transient share outage that fails every copy does not
/// lock out the next attempt for 23 hours.
///
/// Behaviour identical to the pre-refactor inline method.
/// </summary>
public sealed class LogMirrorService
{
    private readonly Logger               _log;
    private readonly PersistentStateStore _stateStore;
    private readonly ByteRateLimiter      _rateLimiter;
    private readonly bool                 _lowerIoPriority;
    private readonly Func<string>         _machineRoot;

    public LogMirrorService(Logger log, PersistentStateStore stateStore,
        ByteRateLimiter rateLimiter, bool lowerIoPriority, Func<string> machineRoot)
    {
        _log             = log;
        _stateStore      = stateStore;
        _rateLimiter     = rateLimiter;
        _lowerIoPriority = lowerIoPriority;
        _machineRoot     = machineRoot;
    }

    public async Task MirrorIfDueAsync(CancellationToken ct)
    {
        var snap = _stateStore.Snapshot();
        if (snap.LastLogMirrorUtc is DateTime last
            && (DateTime.UtcNow - last) < TimeSpan.FromHours(23))
        {
            return;
        }

        string srcDir = _log.LogDirectory;
        if (!Directory.Exists(srcDir)) return;

        string dstDir = Path.Combine(_machineRoot(), "Logs");
        Directory.CreateDirectory(dstDir);

        string liveLogName = $"pms-{DateTime.Now:yyyy-MM-dd}.log";

        // Flush so the most-recent closed log file is fully on disk.
        _log.Flush();

        int copied = 0, skipped = 0, failed = 0;
        // Single copier reused across all log files (light object, runs once/day).
        var logCopier = new ThrottledFileCopier(_rateLimiter, _log,
            resume: null, resumeEnabled: false,
            resumeMinBytes: long.MaxValue,
            lowerIoPriority: _lowerIoPriority,
            publishMode: FilePublishMode.DirectWrite);

        foreach (string srcFile in Directory.EnumerateFiles(srcDir, "pms-*.log"))
        {
            ct.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(srcFile);

            if (string.Equals(fileName, liveLogName, StringComparison.OrdinalIgnoreCase))
                continue;  // today's live log

            string dstFile = Path.Combine(dstDir, fileName);
            if (FileMirror.IsUpToDate(srcFile, dstFile)) { skipped++; continue; }

            try
            {
                await logCopier.CopyAsync(srcFile, dstFile, ct).ConfigureAwait(false);
                copied++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                _log.Warn($"MirrorLogs: пропущен {fileName}: {ex.Message}");
            }
        }

        bool cycleSuccessful = (copied > 0) || (failed == 0);
        if (cycleSuccessful)
            _stateStore.Update(s => s.LastLogMirrorUtc = DateTime.UtcNow);

        _log.Info($"Зеркалирование логов: скопировано {copied}, актуальных {skipped}" +
                  (failed > 0 ? $", ошибок {failed} (попытка будет повторена)" : "") +
                  $", → {dstDir}");
    }
}
