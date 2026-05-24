using System.Security.Cryptography;
using ProfileMirrorSync.Models;

namespace ProfileMirrorSync.Services;

/// <summary>
/// Copies a file in chunks honouring the rate limiter.
/// Preserves source timestamps (creation + last-write) on the destination.
///
/// True async I/O.
///
/// Earlier versions wrapped synchronous Read/Write/Flush calls in Task.Run.
/// This burned a thread-pool slot per chunk and gave only the *appearance*
/// of async — net effect was ~3 thread-pool jobs per 64 KB copied, which
/// at 10 Mbit/s burst means ~5 000 jobs/s of pure scheduling overhead.
///
/// The file streams are now opened with useAsync:true so the platform
/// performs FILE_FLAG_OVERLAPPED I/O and ReadAsync/WriteAsync return real
/// completion-port-driven Tasks.  Compatible with SMB and vboxsf — both
/// support overlapped I/O at the protocol level.
///
/// dispose-safety (the spurious NotSupportedException from
/// SetFileInformationByHandle on some shares) is retained.
///
/// also adds in-copy retry-with-backoff (3 attempts, 1/2/4 s) for
/// transient network errors (IOException / SocketException family).  The
/// destination file is opened fresh each attempt — partial writes are
/// truncated by SetLength(0).
/// </summary>
public sealed class ThrottledFileCopier
{
    private readonly ByteRateLimiter   _limiter;
    private readonly Logger            _log;
    private readonly ResumeStateStore? _resume;
    private readonly bool              _resumeEnabled;
    private readonly long              _resumeMinBytes;
    private readonly bool              _lowerIoPriority;
    private readonly FilePublishMode   _publishMode;
    // Chunk size adapts at runtime to the limiter's burst capacity.
    // ChunkSizeMax (64 KB) is what we'd LIKE to use; the actual read size at
    // each iteration is clamped to _limiter.MaxBurstBytes so a tight bandwidth
    // setting (e.g. 0.1 Mbit/s — UI allows it) cannot livelock the bucket.
    //
    // ChunkSizeMin (4 KB) is the absolute floor: smaller would explode per-
    // chunk syscall cost.  At ChunkSizeMin the minimum sustainable rate is
    // ChunkSizeMin/(burst=2s) = 2 KB/s = 16 Kbit/s — well below the practical
    // UI floor.  Below ChunkSizeMin we honour the read size anyway (limiter
    // can stretch via Task.Delay) so the copy still progresses, just slowly.
    private const int ChunkSizeMax = 64 * 1024; // 64 KB — ideal
    private const int ChunkSizeMin = 4  * 1024; // 4 KB — floor, see above

    // Sidecar update cadence is now expressed in BYTES, not chunks, so that
    // adaptive chunk-size changes don't accidentally save the sidecar more
    // often than necessary on low-bandwidth links (small chunks = more iters
    // per MB of data).
    private const long ResumeUpdateEveryBytes = 1L * 1024 * 1024; // ~1 MB
    // down-sample resume sidecar trace logging.  Previous version
    // logged every 1 MB → ~265 trace lines for a 270 MB file.
    // Now we log every 10 MB to keep the log readable while still showing
    // progress.  Sidecar WRITES are unchanged — only the log line is gated.
    private const long ResumeTraceLogEveryBytes = 10L * 1024 * 1024;

    public ThrottledFileCopier(ByteRateLimiter limiter, Logger log)
        : this(limiter, log, resume: null, resumeEnabled: false, resumeMinBytes: long.MaxValue,
               lowerIoPriority: false, publishMode: FilePublishMode.DirectWrite) { }

    public ThrottledFileCopier(
        ByteRateLimiter   limiter,
        Logger            log,
        ResumeStateStore? resume,
        bool              resumeEnabled,
        long              resumeMinBytes,
        bool              lowerIoPriority,
        FilePublishMode   publishMode = FilePublishMode.DirectWrite)
    {
        _limiter         = limiter;
        _log             = log;
        _resume          = resume;
        _resumeEnabled   = resumeEnabled;
        _resumeMinBytes  = resumeMinBytes;
        _lowerIoPriority = lowerIoPriority;
        _publishMode     = publishMode;
    }

    public async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Snapshot timestamps BEFORE opening (source may be modified during copy)
        DateTime srcCreated  = DateTime.UtcNow;
        DateTime srcModified = DateTime.UtcNow;
        try
        {
            var si      = new FileInfo(source);
            srcCreated  = si.CreationTimeUtc;
            srcModified = si.LastWriteTimeUtc;
        }
        catch { /* non-fatal */ }

        // ── Choose publish strategy ───────────────────────────────────────────
        // TempThenRename: copy into a sibling *.pms_tmp, then delete-then-rename
        // it over the destination so the destination is only ever replaced
        // atomically by a fully-written file.  Falls back to DirectWrite if the
        // rename is rejected by the share (vboxsf / some NAS firmwares).
        //
        // Resume is only meaningful for DirectWrite (the sidecar offset refers
        // to the destination's on-disk length).  In TempThenRename mode we copy
        // fresh into the tmp file each time; CopyOnceAsync still streams chunked
        // + rate-limited, just without sidecar bookkeeping for the tmp target.
        bool useTemp = _publishMode == FilePublishMode.TempThenRename;

        if (useTemp)
        {
            string tmpPath = destination + ".pms_tmp";
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* stale tmp */ }

            await CopyWithRetryAsync(source, tmpPath, ct, allowResume: false).ConfigureAwait(false);

            // Restore source timestamps on the tmp file BEFORE the rename so the
            // published destination carries the right times atomically.
            try { File.SetCreationTimeUtc(tmpPath, srcCreated);  } catch { }
            try { File.SetLastWriteTimeUtc(tmpPath, srcModified); } catch { }

            if (TryPublishRename(tmpPath, destination))
                return;

            // Fallback: rename unsupported on this share — promote the tmp data
            // to a DirectWrite copy so we still publish the file.  Clean up the
            // tmp artefact afterwards.
            _log.Warn($"publish[{Path.GetFileName(destination)}]: переименование не поддерживается долей — " +
                      "переключаюсь на прямую запись (fallback).");
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            // fall through to DirectWrite below
        }

        // ── DirectWrite (legacy, and TempThenRename fallback) ─────────────────
        ClearReadOnly(destination);
        await CopyWithRetryAsync(source, destination, ct, allowResume: true).ConfigureAwait(false);

        // Restore source timestamps — non-fatal if the share doesn't support it.
        try { File.SetCreationTimeUtc(destination, srcCreated);  } catch { }
        try { File.SetLastWriteTimeUtc(destination, srcModified); } catch { }
    }

    /// <summary>
    /// Delete-then-rename (no MOVEFILE_REPLACE_EXISTING) tmp → final.  Returns
    /// false when the share rejects the rename so the caller can fall back.
    /// Same pattern as FileMirror.MirrorRenameAsync.
    /// </summary>
    private bool TryPublishRename(string tmpPath, string destination)
    {
        try
        {
            ClearReadOnly(destination);
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(tmpPath, destination);
            return true;
        }
        catch (Exception ex)
        {
            if (_log.TraceMode)
                _log.Debug($"publish-rename[{Path.GetFileName(destination)}] failed: " +
                           $"[{ex.GetType().Name}] {ex.Message}");
            return false;
        }
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            }
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Retry-with-backoff wrapper around CopyOnceAsync for transient network
    /// errors.  <paramref name="allowResume"/> gates the sidecar resume path
    /// (only DirectWrite to the final destination may resume).
    /// </summary>
    private async Task CopyWithRetryAsync(string source, string target,
        CancellationToken ct, bool allowResume)
    {
        const int MaxAttempts = 3;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await CopyOnceAsync(source, target, ct, allowResume).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                int delayMs = 1000 * (1 << (attempt - 1)); // 1s, 2s, 4s
                _log.Debug($"copy[{Path.GetFileName(target)}] попытка {attempt}/{MaxAttempts}: " +
                           $"[{ex.GetType().Name}] {ex.Message} → повтор через {delayMs} мс");
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task CopyOnceAsync(string source, string destination, CancellationToken ct, bool allowResume)
    {
        // Rent a buffer big enough for any iteration's chunk size.  The
        // ACTUAL read size per iteration is clamped further down to the
        // limiter's current burst capacity (see ChunkSizeMax/Min comments).
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(ChunkSizeMax);

        FileStream? dst = null;
        bool        allBytesWritten = false;

        // ── Resume decision ───────────────────────────────────────────────────
        // For files at or above the threshold, see if we can pick up where a
        // previous attempt left off.  See header comments for the safety
        // invariants we verify before trusting a sidecar.
        long startOffset = 0;
        long srcLength   = 0;
        DateTime srcLastWriteUtc = DateTime.MinValue;
        bool resumeApplicable = false;
        try
        {
            var si = new FileInfo(source);
            srcLength = si.Length;
            srcLastWriteUtc = si.LastWriteTimeUtc;
            resumeApplicable = allowResume && _resumeEnabled && _resume is not null && srcLength >= _resumeMinBytes;
        }
        catch { /* non-fatal — proceed without resume */ }

        // Compute source head hash up-front (first 4 KB SHA-256).
        // Used to validate that a "growing" source (length+mtime advanced)
        // is an APPEND (download, log, video being recorded), not an
        // in-place rewrite (Photoshop save, full overwrite).  Empty string
        // if read fails for any reason — falls back to strict match policy.
        string srcHeadHash = resumeApplicable
            ? await ComputeHeadHashAsync(source, ct).ConfigureAwait(false)
            : "";

        if (resumeApplicable && _resume is not null)
        {
            var saved = _resume.TryLoad(source);

            // ── Strict match: src is byte-for-byte identical to the snapshot
            //                  the sidecar was taken against ──
            bool strictMatch = saved is not null
                && saved.SrcLastWriteUtc == srcLastWriteUtc
                && saved.SrcLength == srcLength
                && saved.BytesCopied > 0
                && saved.BytesCopied < srcLength
                && File.Exists(destination);

            // ── Grow match: src LENGTHENED and the prefix we already copied
            //                still hashes the same (append-friendly) ──
            //
            // New branch.  Without this, a continuously-growing
            // file (download, video record, log tail) restarted from byte 0
            // on every Stop/Start or every FSW-triggered recopy, wasting
            // gigabytes per session.  Conditions:
            //   • sidecar carries a head hash (i.e. not pre- sidecar)
            //   • current src has the same head hash (no in-place rewrite)
            //   • src grew or stayed same length (NEVER shrank)
            //   • src mtime advanced or stayed the same
            //   • sidecar offset is within the OLD recorded length (sanity)
            //   • dst file exists
            bool growMatch = !strictMatch
                && saved is not null
                && !string.IsNullOrEmpty(saved.SrcHeadHash)
                && !string.IsNullOrEmpty(srcHeadHash)
                && saved.SrcHeadHash.Equals(srcHeadHash, StringComparison.OrdinalIgnoreCase)
                && srcLength >= saved.SrcLength
                && srcLastWriteUtc >= saved.SrcLastWriteUtc
                && saved.BytesCopied > 0
                && saved.BytesCopied <= saved.SrcLength
                && saved.BytesCopied < srcLength
                && File.Exists(destination);

            if (strictMatch || growMatch)
            {
                long dstLen = 0;
                try { dstLen = new FileInfo(destination).Length; } catch { }

                // Lenient resume policy.
                //
                // The sidecar is written AFTER FlushAsync, so dst[0..BytesCopied]
                // is byte-identical to source[0..BytesCopied] for any sidecar
                // that exists.  Three cases:
                //
                //   dstLen == sidecar.BytesCopied
                //       Ideal: clean shutdown between sidecar.Save() and the
                //       next chunk's write.  Resume exactly.
                //
                //   dstLen > sidecar.BytesCopied
                //       Common: Stop interrupted a mid-flight chunk write.
                //       The extra bytes past BytesCopied are uncertain (mid-
                //       write, possibly unflushed).  Trim dst back to the
                //       sidecar offset (KNOWN flushed) and resume.  This is
                //       the case that wasted ~190 MB across 4 Stop/Start
                //       cycles in the user log — fixed here.
                //
                //   dstLen < sidecar.BytesCopied
                //       Impossible without external interference (truncation,
                //       deletion-and-recreate by someone else).  Restart from
                //       zero — sidecar is no longer authoritative.
                if (dstLen == saved!.BytesCopied)
                {
                    startOffset = saved.BytesCopied;
                    string mode = growMatch ? "appended" : "exact";
                    _log.Info($"copy[{Path.GetFileName(destination)}]: продолжение с offset " +
                              $"{startOffset / (1024 * 1024)} МБ из {srcLength / (1024 * 1024)} МБ [{mode}]");
                }
                else if (dstLen > saved.BytesCopied)
                {
                    startOffset = saved.BytesCopied;
                    long trimmed = dstLen - saved.BytesCopied;
                    string mode = growMatch ? "appended" : "exact";
                    _log.Info($"copy[{Path.GetFileName(destination)}]: dst был на " +
                              $"{trimmed} Б впереди sidecar — обрезаем до offset " +
                              $"{startOffset / (1024 * 1024)} МБ и продолжаем " +
                              $"(из {srcLength / (1024 * 1024)} МБ) [{mode}].");
                    // Actual truncation happens in the resume path below via
                    // dst.SetLength(startOffset).
                }
                else
                {
                    // dst < sidecar — sidecar references a longer dst than
                    // exists.  Something external truncated/replaced dst.
                    if (_log.TraceMode)
                        _log.Debug($"copy[{Path.GetFileName(destination)}]: sidecar offset {saved.BytesCopied} " +
                                   $"> dst length {dstLen} — sidecar потерял авторитет, начинаем заново");
                    _resume.Clear(source);
                }
            }
            else if (saved is not null)
            {
                // Sidecar exists but source changed in a way we can't trust
                // (head hash differs = in-place rewrite, OR src shrank, OR
                // sidecar is pre- with no head hash and src grew).
                // Discard and restart.
                if (_log.TraceMode)
                    _log.Debug($"copy[{Path.GetFileName(destination)}]: источник изменился с прошлой попытки — начинаем заново");
                _resume.Clear(source);
            }
        }

        try
        {
            // useAsync:true → real overlapped I/O via I/O completion ports.
            // No Task.Run wrappers needed; ReadAsync/WriteAsync return real Tasks.
            await using var src = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, ChunkSizeMax, useAsync: true);

            // Destination: OpenOrCreate + manual truncate or seek.
            // Sidesteps whatever metadata hints FileMode.Create may emit on
            // vboxsf/NAS shares that reject SetFileInformationByHandle.
            dst = new FileStream(destination, FileMode.OpenOrCreate, FileAccess.Write,
                FileShare.None, ChunkSizeMax, useAsync: true);

            if (startOffset > 0)
            {
                // Resume path: position both streams at the offset.
                src.Seek(startOffset, SeekOrigin.Begin);
                dst.SetLength(startOffset);   // ensure exact length match
                dst.Seek(startOffset, SeekOrigin.Begin);
            }
            else
            {
                dst.SetLength(0);
            }

            long bytesCopied = startOffset;
            long bytesSinceSidecar = 0;
            long lastTracedBytes = startOffset;   // down-sample sidecar trace
            int sidecarWriteCount = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // adaptive chunk: do NOT request more bytes than the
                // limiter can hand out in a single WaitAsync call.  Without
                // this, a tight BW setting (UI floor is 0.1 Mbit/s → 12.5 KB/s
                // → 25 KB burst cap) combined with our previous fixed 64 KB
                // chunk caused WaitAsync to spin forever (the bucket could
                // never refill above 25 KB, but we kept asking for 64).
                int chunk = Math.Min(ChunkSizeMax, _limiter.MaxBurstBytes);
                if (chunk < ChunkSizeMin) chunk = ChunkSizeMin; // see comment on ChunkSizeMin
                int read = await src.ReadAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false);
                if (read <= 0) break;
                await _limiter.WaitAsync(read, ct).ConfigureAwait(false);

                // Thread-level background priority is applied ONLY
                // around the WriteAsync.  C# spec guarantees the `finally`
                // block runs on the same continuation thread that finished
                // the awaited WriteAsync — so END always lands on the same
                // thread that BEGIN was set on.  No leak across awaits.
                //
                // The mistake was wrapping a higher-level `await
                // DispatchAsync` where the await might resume on a different
                // ThreadPool thread, causing END to fail (log line 83
                // ok=False) AND leaking BACKGROUND on the original thread.
                bool bg = _lowerIoPriority && SetCurrentThreadBackground();
                try
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                finally
                {
                    if (bg) RestoreCurrentThread();
                }
                bytesCopied      += read;
                bytesSinceSidecar += read;

                // Persist resume offset every ~1 MB.  We flush BEFORE saving
                // the sidecar so the on-disk length matches the recorded
                // BytesCopied — that's the invariant we check on resume.
                //
                // trace log entry is emitted only every ~10 MB so a
                // 200 MB file produces ~20 trace lines instead of 200.
                //
                // cadence keyed on BYTES, not chunks — adaptive
                // chunk sizing on low-BW links would otherwise multiply the
                // sidecar write count by 16× (16 KB chunks × 16 = 256 KB per
                // sidecar instead of the intended 1 MB).
                if (resumeApplicable && _resume is not null)
                {
                    if (bytesSinceSidecar >= ResumeUpdateEveryBytes)
                    {
                        bytesSinceSidecar = 0;
                        sidecarWriteCount++;
                        await dst.FlushAsync(ct).ConfigureAwait(false);
                        bool emitTrace = (bytesCopied - lastTracedBytes) >= ResumeTraceLogEveryBytes;
                        if (emitTrace) lastTracedBytes = bytesCopied;
                        _resume.Save(new ResumeState
                        {
                            SrcPath         = source,
                            DstPath         = destination,
                            SrcLength       = srcLength,
                            SrcLastWriteUtc = srcLastWriteUtc,
                            SrcHeadHash     = srcHeadHash,    // append-friendly resume
                            BytesCopied     = bytesCopied,
                        }, emitTrace);
                    }
                }
            }

            // Force the FS to commit all bytes BEFORE we try to close. This is
            // FlushFileBuffers under the hood — universally supported on SMB.
            //
            // Final flush uses CancellationToken.None.
            // At this point every byte has already been awaited through
            // dst.WriteAsync; the data sits in OS buffer cache.  All this
            // FlushAsync does is request commit to disk.  If the caller's
            // ct happens to be cancelled at exactly this moment, we'd throw
            // OperationCanceledException without setting allBytesWritten=true,
            // and the file would be classified as failed even though the
            // data is intact and the next reconcile would just re-detect it
            // as up-to-date.  Letting the final commit complete is correct
            // — cancellation took effect on the next operation anyway.
            await dst.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            allBytesWritten = true;

            // Successful completion — clear the sidecar.
            if (resumeApplicable && _resume is not null)
            {
                _resume.Clear(source);
                // summary line so the log shows total work even when
                // per-write traces are sampled.
                if (_log.TraceMode && sidecarWriteCount > 0)
                {
                    long mb = (bytesCopied - startOffset) / (1024 * 1024);
                    _log.Debug($"resume[{Path.GetFileName(source)}]: total {mb} MB written across " +
                               $"{sidecarWriteCount} sidecar updates");
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);

            // Close the destination handle with a SEPARATE try/catch.
            // If Dispose throws AFTER we successfully flushed all bytes, swallow it:
            // the data is on disk; IsUpToDate will confirm.
            if (dst is not null)
            {
                try { await dst.DisposeAsync().ConfigureAwait(false); }
                catch (Exception disposeEx)
                {
                    if (allBytesWritten)
                    {
                        _log.Debug($"dispose-after-write[{Path.GetFileName(destination)}]: " +
                                   $"[{disposeEx.GetType().Name}] {disposeEx.Message} — данные записаны, продолжаем");
                    }
                    else
                    {
                        _log.Debug($"dispose-during-error[{Path.GetFileName(destination)}]: " +
                                   $"[{disposeEx.GetType().Name}] {disposeEx.Message}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Classify an IOException as transient (network glitch, server busy) vs
    /// permanent (path malformed, access denied).  Transient errors get retried;
    /// permanent ones propagate immediately.
    /// </summary>
    private static bool IsTransient(IOException ex)
    {
        // Windows HResult codes that indicate transient network conditions.
        // Source: https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes
        unchecked
        {
            int hr = ex.HResult & 0xFFFF;
            return hr switch
            {
                51   => true,   // ERROR_REM_NOT_LIST       — network not present
                64   => true,   // ERROR_NETNAME_DELETED    — network name deleted
                67   => true,   // ERROR_BAD_NET_NAME       — could not be found
                71   => true,   // ERROR_REQ_NOT_ACCEP      — no more connections
                121  => true,   // ERROR_SEM_TIMEOUT        — semaphore timeout
                170  => true,   // ERROR_BUSY               — requested resource in use
                232  => true,   // ERROR_NO_DATA            — pipe is being closed
                1231 => true,   // ERROR_NETWORK_UNREACHABLE
                1232 => true,   // ERROR_HOST_UNREACHABLE
                _ => false,
            };
        }
    }

    // ── Thread-level low-priority helpers  ────────────────────────────
    //
    // THREAD_MODE_BACKGROUND_BEGIN / _END — Windows 8+ API that lowers the
    // calling thread's CPU priority to IDLE and its I/O priority to VERY LOW.
    //
    // Used to wrap each `await dst.WriteAsync(...)` so the actual write to SMB
    // runs at background priority without admin elevation.  Because the
    // wrapping `finally` follows the await immediately, C# spec guarantees
    // END runs on the same thread that finished WriteAsync — no leak across
    // ThreadPool boundaries (which was the bug).
    //
    // https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadpriority
    private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    private const int THREAD_MODE_BACKGROUND_END   = 0x00020000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    // Per-chunk priority changes are ~few µs each.  At 64 KB chunks on a
    // 10 Mbit/s link, write itself takes ~50 ms — overhead is 0.01 %.
    // Trace logging is intentionally NOT emitted per chunk (would flood the
    // log with thousands of BEGIN/END lines on a large file).
    private bool SetCurrentThreadBackground()
    {
        try { return SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN); }
        catch { return false; }
    }

    private void RestoreCurrentThread()
    {
        try { SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END); }
        catch { /* best-effort: thread will return to ThreadPool either way */ }
    }

    // ── Head-hash for append-friendly resume  ────────────────────────
    //
    // Returns hex SHA-256 of the first HeadHashBytes of `path`, or empty string
    // on any error.  Reads in a small useAsync stream that shares with writers
    // so a file being downloaded concurrently can still be hashed.  Cost on a
    // local 50 MB file: ~1 ms (single 4 KB read + SHA-256 of 4 KB).
    //
    // Why 4 KB: most container formats (ZIP, PNG, ELF, PE, MP4, PDF, MSI, ISO)
    // carry magic bytes and identifying structure in the first 4 KB.  An
    // in-place rewrite (Photoshop save, full-overwrite copy) reliably changes
    // those bytes.  A pure append (download, log, video record) keeps them
    // exactly identical.  We don't need full-file integrity — that would
    // defeat the point of resume.

    private const int HeadHashBytes = 4 * 1024;

    private static async Task<string> ComputeHeadHashAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                HeadHashBytes, useAsync: true);
            byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(HeadHashBytes);
            try
            {
                int read = await fs.ReadAsync(buf.AsMemory(0, HeadHashBytes), ct).ConfigureAwait(false);
                if (read <= 0) return "";
                byte[] hash = SHA256.HashData(buf.AsSpan(0, read));
                return Convert.ToHexString(hash);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buf);
            }
        }
        catch
        {
            // Hash failure → empty string → grow-resume disabled for this
            // attempt (caller falls back to strict-match only, same as the
            // pre- behaviour).  Never throw — copy must proceed.
            return "";
        }
    }
}
