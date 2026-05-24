using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProfileMirrorSync.Models;

namespace ProfileMirrorSync.Services;

/// <summary>
/// Manages "resume sidecars" for byte-range resume of large file copies.
///
/// Each sidecar is a tiny JSON file under
/// %ProgramData%\ProfileMirrorSync\resume\&lt;hash&gt;.json with the source
/// path, source size+mtime, destination path, and bytes-copied-so-far.
///
/// Hash key = first 16 hex chars of SHA-256(srcPath) — short, unique enough,
/// stable across restarts.  We deliberately key on the source path, not the
/// destination path: if the destination root changes (admin reconfigures
/// settings), the resume becomes invalid (dst mismatch) and we restart
/// the copy, which is the right behaviour.
///
/// Sidecars are tiny (~300 bytes) and written every ~1 MB of copied data,
/// so disk overhead is bounded.  Cleanup (orphan sidecars older than N days)
/// happens in SyncController's reconcile loop, not here.
///
/// Thread-safety: each Save/Load is independent; concurrent calls for the
/// SAME source file would only happen if the same file was being copied
/// twice in parallel, which our dedupe set prevents.  No internal locks
/// needed.
/// </summary>
public sealed class ResumeStateStore
{
    private readonly string _dir;
    private readonly Logger _log;

    public ResumeStateStore(Logger log)
    {
        _log = log;
        // per-user resume sidecars (was shared %ProgramData%).
        _dir = Path.Combine(AppPaths.DataDirectory, "resume");
    }

    /// <summary>Returns the sidecar file path for the given source path.</summary>
    public string SidecarPath(string srcPath)
    {
        return Path.Combine(_dir, KeyFor(srcPath) + ".json");
    }

    /// <summary>Reads a sidecar.  Returns null on any failure or absence.</summary>
    public ResumeState? TryLoad(string srcPath)
    {
        try
        {
            string path = SidecarPath(srcPath);
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ResumeState>(json);
        }
        catch (Exception ex)
        {
            _log.Debug($"resume: TryLoad {Path.GetFileName(srcPath)} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Atomically writes a sidecar.  Logs failure at Debug; never throws.
    ///
    /// the per-write trace line is gated by <paramref name="emitTrace"/>
    /// so callers can down-sample log noise on long copies.  The actual sidecar
    /// write (data path) happens unconditionally — only the log entry is gated.
    /// </summary>
    public void Save(ResumeState state, bool emitTrace = true)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            state.UpdatedUtc = DateTime.UtcNow;
            string path = SidecarPath(state.SrcPath);
            string tmp  = path + ".tmp";
            string json = JsonSerializer.Serialize(state);
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            if (emitTrace && _log.TraceMode)
                _log.Debug($"resume[{Path.GetFileName(state.SrcPath)}]: sidecar saved BytesCopied={state.BytesCopied}");
        }
        catch (Exception ex)
        {
            _log.Debug($"resume: Save {Path.GetFileName(state.SrcPath)} failed: {ex.Message}");
        }
    }

    /// <summary>Removes the sidecar for a given source path.  Idempotent.</summary>
    public void Clear(string srcPath)
    {
        try
        {
            string path = SidecarPath(srcPath);
            if (File.Exists(path))
            {
                File.Delete(path);
                if (_log.TraceMode)
                    _log.Debug($"resume[{Path.GetFileName(srcPath)}]: sidecar cleared");
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"resume: Clear {Path.GetFileName(srcPath)} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes sidecars older than <paramref name="maxAgeDays"/>.  Called
    /// periodically by the reconcile loop.  Doesn't traverse subdirectories
    /// because the sidecar dir is flat.
    /// </summary>
    public int CleanupOrphans(int maxAgeDays)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(_dir)) return 0;
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(Math.Max(1, maxAgeDays));
            foreach (string file in Directory.EnumerateFiles(_dir, "*.json"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch { /* skip locked / vanished files */ }
            }
            if (removed > 0)
                _log.Debug($"resume: удалено {removed} sidecar'ов старше {maxAgeDays} дн.");
        }
        catch (Exception ex)
        {
            _log.Debug($"resume: CleanupOrphans failed: {ex.Message}");
        }
        return removed;
    }

    private static string KeyFor(string srcPath)
    {
        // Stable, short, filename-safe.  We don't need cryptographic strength
        // here — only collision resistance for hundreds of resume sidecars
        // simultaneously.  SHA-256 first 16 hex chars (8 bytes) gives a
        // 2^32 birthday bound, far more than we'll ever have in flight.
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(srcPath));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }
}
