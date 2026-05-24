using Microsoft.Win32;

namespace ProfileMirrorSync.Services;

/// <summary>Exports registry hives to .reg text files in the backup destination.</summary>
public sealed class RegistrySnapshotService : IDisposable
{
    private readonly IReadOnlyList<string> _hivePaths;
    private readonly string               _destRoot;
    private readonly Logger               _log;

    public RegistrySnapshotService(IEnumerable<string> hivePaths, string destRoot, Logger log)
    {
        _hivePaths = hivePaths.ToList();
        _destRoot  = destRoot;
        _log       = log;
    }

    public async Task CaptureAllAsync(CancellationToken ct)
    {
        string regDir = Path.Combine(_destRoot, "Registry");
        Directory.CreateDirectory(regDir);

        foreach (string hivePath in _hivePaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string outFile = Path.Combine(regDir,
                    hivePath.Replace('\\', '_').Replace(':', '_') + ".reg");

                using var proc = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName        = "reg.exe",
                        Arguments       = $"export \"{hivePath}\" \"{outFile}\" /y",
                        CreateNoWindow  = true,
                        UseShellExecute = false,
                    }
                };
                proc.Start();
                try
                {
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Previously the `using`
                    // closed the Process object on cancel but the OS reg.exe
                    // kept running.  HKCU\Software export can take 30–90 s;
                    // accumulating zombies through repeated Stop/Start cycles
                    // (common when tweaking settings) was a real leak.  Now
                    // we Kill before propagating the OCE, then let `using`
                    // dispose normally.
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                    throw;
                }

                if (proc.ExitCode == 0)
                    _log.Info($"Снимок реестра сохранён: {outFile}");
                else
                    _log.Warn($"reg.exe завершился с кодом {proc.ExitCode} для [{hivePath}]");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn($"Ошибка снимка реестра [{hivePath}]: {ex.Message}");
            }
        }
    }

    public void Dispose() { }
}
