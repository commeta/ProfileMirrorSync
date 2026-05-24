using System.Diagnostics;
using ProfileMirrorSync.Models;

namespace ProfileMirrorSync.Services;

/// <summary>
/// Runs a user-configured external program (typically an archiver
/// such as 7-Zip) after a full reconcile cycle, at most once per
/// <see cref="AppSettings.PostSyncIntervalMinutes"/>.
///
/// Design:
///   • Pure side-effect service: no shared mutable state, no locks.  The
///     once-per-interval gate lives in PersistentState.LastPostSyncRunUtc and
///     is checked/stamped through PersistentStateStore (same pattern as the
///     log-mirror and registry-snapshot gates).
///   • Hard timeout via a linked CTS; a runaway archiver is killed
///     (entire process tree) rather than allowed to hang the cycle.
///   • BelowNormal priority by default so the archiver doesn't fight the
///     user's foreground work — consistent with PMS's "user shouldn't feel
///     the program" goal.
///   • Never throws to the caller for an external-program failure: a bad exit
///     code or a missing exe is logged Warn and the sync flow continues.
///     Cancellation (OperationCanceledException) DOES propagate so Stop is
///     responsive.
/// </summary>
public sealed class PostSyncRunner
{
    private readonly AppSettings          _settings;
    private readonly Logger               _log;
    private readonly PersistentStateStore _stateStore;
    private readonly string               _machineRoot;

    public PostSyncRunner(AppSettings settings, Logger log,
        PersistentStateStore stateStore, string machineRoot)
    {
        _settings    = settings;
        _log         = log;
        _stateStore  = stateStore;
        _machineRoot = machineRoot;
    }

    /// <summary>
    /// Run the configured program if enabled and the interval gate allows it.
    /// Returns true when the program was launched (regardless of its exit
    /// code), false when skipped (disabled / not yet due / no exe configured).
    /// </summary>
    public async Task<bool> MaybeRunAsync(CancellationToken ct)
    {
        if (!_settings.PostSyncEnabled) return false;
        if (string.IsNullOrWhiteSpace(_settings.PostSyncExePath))
        {
            _log.Warn("Post-sync: включено, но не задан путь к программе — пропуск.");
            return false;
        }

        // Once-per-interval gate.
        int intervalMin = Math.Max(0, _settings.PostSyncIntervalMinutes);
        if (intervalMin > 0)
        {
            var snap = _stateStore.Snapshot();
            if (snap.LastPostSyncRunUtc is DateTime last
                && (DateTime.UtcNow - last) < TimeSpan.FromMinutes(intervalMin))
            {
                if (_log.TraceMode)
                {
                    var ago = DateTime.UtcNow - last;
                    _log.Debug($"Post-sync пропущен: предыдущий запуск {ago.TotalHours:F1} ч назад, " +
                               $"интервал {intervalMin} мин.");
                }
                return false;
            }
        }

        string dest   = _machineRoot;
        string backup = Path.Combine(dest, "backup");
        try { Directory.CreateDirectory(backup); } catch { /* archiver may create it */ }

        DateTime nowLocal = DateTime.Now;
        string args = ExpandPlaceholders(_settings.PostSyncArguments, dest, backup, nowLocal);
        string workDir = string.IsNullOrWhiteSpace(_settings.PostSyncWorkingDir)
            ? (Path.GetDirectoryName(_settings.PostSyncExePath) ?? dest)
            : ExpandPlaceholders(_settings.PostSyncWorkingDir, dest, backup, nowLocal);

        _log.Info($"Post-sync: запуск «{_settings.PostSyncExePath}» {args}");
        var sw = Stopwatch.StartNew();

        try
        {
            int exitCode = await RunProcessAsync(_settings.PostSyncExePath, args, workDir, ct)
                .ConfigureAwait(false);
            sw.Stop();
            if (exitCode == 0)
            {
                _log.Info($"Post-sync: завершено успешно за {sw.Elapsed.TotalSeconds:F0} с.");
            }
            else
            {
                _log.Warn($"Post-sync: программа завершилась с кодом {exitCode} " +
                          $"за {sw.Elapsed.TotalSeconds:F0} с.");
            }
            // Stamp success on any clean exit (the program ran to completion);
            // a non-zero code is the operator's archiver semantics, not our
            // failure to launch.  This prevents re-launching every cycle.
            _stateStore.Update(s => s.LastPostSyncRunUtc = DateTime.UtcNow);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Stop/shutdown — do NOT stamp; let the next opportunity retry.
            _log.Info("Post-sync: отменён (остановка/выход).");
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Launch failure (exe not found, access denied, etc.) — log and
            // continue.  Do NOT stamp so a transient problem retries next cycle.
            _log.Warn($"Post-sync: не удалось выполнить «{_settings.PostSyncExePath}»: {ex.Message}", ex);
            return true; // we attempted; caller doesn't need to do anything else
        }
    }

    /// <summary>
    /// Expand the documented placeholders.  Marked internal so the test project
    /// can verify the expansion contract directly.
    /// </summary>
    internal static string ExpandPlaceholders(string template, string dest, string backup, DateTime nowLocal)
    {
        if (string.IsNullOrEmpty(template)) return "";
        return template
            .Replace("{dest}",    dest,                       StringComparison.OrdinalIgnoreCase)
            .Replace("{backup}",  backup,                     StringComparison.OrdinalIgnoreCase)
            .Replace("{machine}", Environment.MachineName,    StringComparison.OrdinalIgnoreCase)
            .Replace("{user}",    Environment.UserName,       StringComparison.OrdinalIgnoreCase)
            .Replace("{date}",    nowLocal.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}",    nowLocal.ToString("HH-mm-ss"),   StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int> RunProcessAsync(string exe, string args, string workDir, CancellationToken ct)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = exe,
                Arguments              = args,
                WorkingDirectory       = Directory.Exists(workDir) ? workDir : "",
                CreateNoWindow         = true,
                UseShellExecute        = false,
                // Redirect + drain stdout/stderr.  A verbose
                // archiver (e.g. 7-Zip with progress, or a chatty script) can fill
                // the ~4–64 KB OS pipe buffer; once full the child blocks on write
                // and never exits, hanging WaitForExitAsync until the timeout.
                // Draining asynchronously keeps the pipe empty so the child runs.
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            }
        };

        proc.OutputDataReceived += (_, _) => { /* drain & discard */ };
        proc.ErrorDataReceived  += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data) && _log.TraceMode)
                _log.Debug($"post-sync stderr: {e.Data}");
        };

        if (!proc.Start())
            throw new InvalidOperationException("Process.Start вернул false.");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // Best-effort low priority — the user shouldn't feel a heavy archive.
        if (_settings.PostSyncLowPriority)
        {
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        }

        int timeoutMin = Math.Max(0, _settings.PostSyncTimeoutMinutes);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMin > 0) linked.CancelAfter(TimeSpan.FromMinutes(timeoutMin));

        try
        {
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            // Timeout (linked fired without the caller's ct) or genuine cancel.
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;          // genuine Stop/shutdown
            _log.Warn($"Post-sync: программа не завершилась за {timeoutMin} мин — принудительно остановлена.");
            return -1;
        }
    }
}
