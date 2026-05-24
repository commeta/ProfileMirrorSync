using System.Diagnostics;
using ProfileMirrorSync.Models;
using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Regression tests covering several unrelated fixes, grouped so
/// future engineers can find them; each test name maps to a section in the
/// CHANGELOG.
/// </summary>
public class RegressionFixesTests : IDisposable
{
    private readonly string _root;
    private readonly Logger _log;

    public RegressionFixesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PMS2413_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _log = new Logger(_root, AppLogLevel.Warning);
    }

    public void Dispose()
    {
        try { _log.Dispose(); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── F1: ByteRateLimiter livelock fix ──────────────────────────────────────

    [Fact]
    public void MaxBurstBytes_UnlimitedMode_ReturnsIntMaxValue()
    {
        // bitsPerSecond = 0 → unlimited sentinel → no clamp required.
        var rl = new ByteRateLimiter(0);
        Assert.Equal(int.MaxValue, rl.MaxBurstBytes);
    }

    [Theory]
    [InlineData(100_000,     25_000)]    // 100 Kbps → 25 KB burst
    [InlineData(800_000,    200_000)]    // 0.8 Mbps → 200 KB burst
    [InlineData(10_000_000, 2_500_000)]  // 10  Mbps → 2.5 MB burst
    public void MaxBurstBytes_LimitedMode_EqualsTwoSecondsOfTraffic(int bps, int expectedBurst)
    {
        var rl = new ByteRateLimiter(bps);
        // Burst cap = bytes/sec * 2; bytes/sec = bps / 8.
        Assert.Equal(expectedBurst, rl.MaxBurstBytes);
    }

    [Fact]
    public async Task CopyAtVeryLowBandwidth_DoesNotLivelock()
    {
        // F1 regression: with the fixed 64 KB chunk size and a UI
        // floor of 0.1 Mbit/s (= 12.5 KB/s → 25 KB burst), the very first
        // chunk request (64 KB > 25 KB burst cap) would spin WaitAsync
        // forever.  The adaptive-chunk fix clamps the read to burst cap.
        //
        // Test budget: copy 50 KB at 100 Kbps (25 KB burst).  At 12.5 KB/s
        // that's a 4-second copy — comfortably finishes inside the 30-s
        // bail-out token.  Pre-fix this would never return.
        string src = Path.Combine(_root, "src.bin");
        string dst = Path.Combine(_root, "dst.bin");
        File.WriteAllBytes(src, new byte[50_000]);

        var rl     = new ByteRateLimiter(100_000); // 100 Kbps
        var copier = new ThrottledFileCopier(rl, _log);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await copier.CopyAsync(src, dst, cts.Token);

        Assert.Equal(50_000, new FileInfo(dst).Length);
    }

    // ── F9: Append-friendly resume with head-hash guard ───────────────────────

    [Fact]
    public void ResumeState_SrcHeadHash_RoundTrips()
    {
        // New field on ResumeState must persist through JSON round-trip.
        // (ResumeStateStore tests already cover JSON; this one pins the
        // contract: the field exists and serializes.)
        var store = new ResumeStateStore(_log);
        string srcKey = $@"C:\TEST\{Guid.NewGuid()}\f.bin";
        try
        {
            store.Save(new ResumeState
            {
                SrcPath         = srcKey,
                DstPath         = @"\\srv\share\f.bin",
                SrcLength       = 100_000_000,
                SrcLastWriteUtc = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                BytesCopied     = 40_000_000,
                SrcHeadHash     = "AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899",
            }, emitTrace: false);

            var loaded = store.TryLoad(srcKey);
            Assert.NotNull(loaded);
            Assert.Equal("AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899",
                         loaded!.SrcHeadHash);
        }
        finally { try { store.Clear(srcKey); } catch { } }
    }

    [Fact]
    public async Task GrowingFile_ResumesFromOffset_WhenHeadHashUnchanged()
    {
        // F9 regression: a file that grows between two copy attempts (e.g. a
        // log being appended, a download still landing) used to restart from
        // byte 0 every time because (oldLength != newLength) failed the
        // strict-match guard. grow-resume branch trusts the prefix
        // when the head hash matches.
        //
        // Setup: 2 MB file → copy with ResumeMinBytes=1 MB → kill mid-copy by
        // simulating with a manually-written sidecar at 1 MB → grow source to
        // 3 MB (append + 1 MB) → second copy should pick up from offset 1 MB.
        string src    = Path.Combine(_root, "growing.bin");
        string dst    = Path.Combine(_root, "growing.dst");

        // Initial 2 MB of pattern A.
        var patternA  = new byte[2 * 1024 * 1024];
        for (int i = 0; i < patternA.Length; i++) patternA[i] = (byte)('A' + (i % 7));
        File.WriteAllBytes(src, patternA);

        // First half-copy: copy 1 MB of A to dst, write sidecar with that
        // offset + head hash.  We do this without actually running the
        // copier (which would copy the whole file) — we just construct the
        // pre-condition that the copier would have left behind.
        long halfOffset = 1 * 1024 * 1024;
        File.WriteAllBytes(dst, patternA.AsSpan(0, (int)halfOffset).ToArray());

        // Compute head hash of src the same way the copier does (first 4 KB).
        string headHashA;
        using (var fs = new FileStream(src, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: false))
        {
            byte[] head = new byte[4096];
            int read = fs.Read(head, 0, head.Length);
            headHashA = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(head.AsSpan(0, read)));
        }

        var srcInfo = new FileInfo(src);
        var resumeStore = new ResumeStateStore(_log);
        try
        {
            resumeStore.Save(new ResumeState
            {
                SrcPath         = src,
                DstPath         = dst,
                SrcLength       = srcInfo.Length,
                SrcLastWriteUtc = srcInfo.LastWriteTimeUtc,
                BytesCopied     = halfOffset,
                SrcHeadHash     = headHashA,
            }, emitTrace: false);

            // GROW the source: append 1 MB of pattern B (same head, just more
            // bytes at the tail).  Bumps length, bumps mtime.
            using (var fs = new FileStream(src, FileMode.Append, FileAccess.Write))
            {
                var patternB = new byte[1 * 1024 * 1024];
                for (int i = 0; i < patternB.Length; i++) patternB[i] = (byte)('a' + (i % 7));
                fs.Write(patternB, 0, patternB.Length);
            }

            // Now run a real copy with resume enabled and a low threshold so
            // resume engages.  Expect: dst length = 3 MB (1 MB pre-existing +
            // 2 MB new tail), dst[0..1MB] == pattern A (untouched), 
            // dst[1MB..3MB] == fresh read from src.
            var rl     = new ByteRateLimiter(0);
            var copier = new ThrottledFileCopier(rl, _log,
                resume: resumeStore, resumeEnabled: true,
                resumeMinBytes: 1024,           // 1 KB — engages on any sane file
                lowerIoPriority: false);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await copier.CopyAsync(src, dst, cts.Token);

            Assert.Equal(3 * 1024 * 1024, new FileInfo(dst).Length);
            byte[] dstBytes = File.ReadAllBytes(dst);
            // First MB must be unchanged pattern A (never re-read from src).
            for (int i = 0; i < halfOffset; i++)
                Assert.Equal((byte)('A' + (i % 7)), dstBytes[i]);
            // Bytes 1..2 MB are second half of pattern A (read from src).
            for (int i = (int)halfOffset; i < 2 * 1024 * 1024; i++)
                Assert.Equal((byte)('A' + (i % 7)), dstBytes[i]);
            // Bytes 2..3 MB are the appended pattern B.
            for (int i = 2 * 1024 * 1024; i < 3 * 1024 * 1024; i++)
                Assert.Equal((byte)('a' + ((i - 2 * 1024 * 1024) % 7)), dstBytes[i]);
        }
        finally { try { resumeStore.Clear(src); } catch { } }
    }

    [Fact]
    public async Task InPlaceRewrite_DiscardsSidecar_AndCopiesFromZero()
    {
        // F9 negative case: head hash CHANGES (Photoshop-save-style full
        // overwrite).  Sidecar must be discarded, copy must start from
        // byte 0, no corruption.
        string src = Path.Combine(_root, "rewritten.bin");
        string dst = Path.Combine(_root, "rewritten.dst");

        var oldBytes = new byte[2 * 1024 * 1024];
        for (int i = 0; i < oldBytes.Length; i++) oldBytes[i] = (byte)('X' + (i % 3));
        File.WriteAllBytes(src, oldBytes);

        // Pre-existing dst (the "partial copy") and pre-existing sidecar
        // pointing at it, anchored to the OLD head hash.
        long halfOffset = 1 * 1024 * 1024;
        File.WriteAllBytes(dst, oldBytes.AsSpan(0, (int)halfOffset).ToArray());

        var srcInfo = new FileInfo(src);
        var resumeStore = new ResumeStateStore(_log);
        try
        {
            resumeStore.Save(new ResumeState
            {
                SrcPath         = src,
                DstPath         = dst,
                SrcLength       = srcInfo.Length,
                SrcLastWriteUtc = srcInfo.LastWriteTimeUtc,
                BytesCopied     = halfOffset,
                SrcHeadHash     = "0000000000000000000000000000000000000000000000000000000000000000",
                // ^ deliberately wrong — simulates an in-place rewrite that
                //   produces a different head hash for the same length.
            }, emitTrace: false);

            // Now FULLY rewrite src with pattern Y (same length, totally
            // different bytes).  Head hash will be very different from the
            // "0000..." we recorded above.
            var newBytes = new byte[2 * 1024 * 1024];
            for (int i = 0; i < newBytes.Length; i++) newBytes[i] = (byte)('Y' + (i % 3));
            File.WriteAllBytes(src, newBytes);

            // The sidecar's SrcLastWriteUtc was captured from the OLD file.
            // On filesystems with coarse timestamp resolution (FAT, or NTFS
            // when the rewrite lands in the same tick — the whole test runs
            // in ~30 ms) File.WriteAllBytes can leave the mtime unchanged,
            // which would let the STRICT-match branch (length + mtime equal)
            // pass and resume from the 1 MB offset, keeping the old prefix.
            // That is correct product behaviour for a same-length, same-mtime
            // file; it just isn't the in-place-rewrite scenario we want to
            // exercise.  Force a distinct, LATER mtime so strict-match is
            // impossible and the head-hash discard is the only applicable
            // branch — exactly the Photoshop-save case under test.
            File.SetLastWriteTimeUtc(src, srcInfo.LastWriteTimeUtc.AddSeconds(5));

            var rl     = new ByteRateLimiter(0);
            var copier = new ThrottledFileCopier(rl, _log,
                resume: resumeStore, resumeEnabled: true,
                resumeMinBytes: 1024, lowerIoPriority: false);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await copier.CopyAsync(src, dst, cts.Token);

            // dst must equal the NEW src bytes — head hash mismatch caused
            // sidecar discard, copy started from 0.
            Assert.Equal(2 * 1024 * 1024, new FileInfo(dst).Length);
            byte[] dstBytes = File.ReadAllBytes(dst);
            for (int i = 0; i < dstBytes.Length; i++)
                Assert.Equal((byte)('Y' + (i % 3)), dstBytes[i]);
        }
        finally { try { resumeStore.Clear(src); } catch { } }
    }

    // ── F2: ReconcileSummary plumbs all 3 missing counters ────────────────────

    [Fact]
    public void ReconcileSummary_DefaultEmpty_HasAllZeros()
    {
        var s = ReconcileSummary.Empty;
        Assert.Equal(0,  s.FilesCopied);
        Assert.Equal(0L, s.BytesCopied);
        Assert.Equal(0,  s.FilesSkipped);
        Assert.Equal(0,  s.OrphansDeleted);
        Assert.Equal(0,  s.DirsTouched);
    }

    [Fact]
    public async Task ReconcileRootAsync_ReturnsBytesCopiedAndOrphanCount()
    {
        // F2 regression: pre- ReconcileRootAsync returned `Task`,
        // and FilesDeletedTotal / BytesCopiedTotal in the stats window
        // were never updated.  Now it returns ReconcileSummary with both.
        string src = Path.Combine(_root, "rcsrc");
        string dst = Path.Combine(_root, "rcdst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        // 2 source files → expect copied=2, bytes=11
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");      // 5 bytes
        File.WriteAllText(Path.Combine(src, "b.txt"), "world!");     // 6 bytes
        // 1 orphan on dst → expect orphansDeleted=1
        File.WriteAllText(Path.Combine(dst, "orphan.txt"), "stale");

        var rl     = new ByteRateLimiter(0);
        var copier = new ThrottledFileCopier(rl, _log);
        var mirror = new FileMirror(_log, copier, retryCount: 1);

        var summary = await mirror.ReconcileRootAsync(src, dst,
            shouldIgnore: _ => false,
            opts: new ReconcileOptions(0, 0, 0),
            onFileCopied: null,
            ct: CancellationToken.None);

        Assert.Equal(2,  summary.FilesCopied);
        Assert.Equal(11, summary.BytesCopied);   // "hello" + "world!"
        Assert.Equal(1,  summary.OrphansDeleted);
        Assert.Equal(0,  summary.FilesSkipped);
    }

    [Fact]
    public async Task MirrorDeleteAsync_ReturnsTrueOnDelete_FalseWhenNothing()
    {
        // F2 plumbing — MirrorDeleteAsync return value drives the real-time
        // FilesDeletedTotal counter.
        var rl     = new ByteRateLimiter(0);
        var copier = new ThrottledFileCopier(rl, _log);
        var mirror = new FileMirror(_log, copier, retryCount: 1);

        string dst = Path.Combine(_root, "to-delete.txt");
        File.WriteAllText(dst, "x");
        Assert.True(await mirror.MirrorDeleteAsync("ignored-src", dst, CancellationToken.None));

        // Second call: file is already gone → returns false (counter must
        // NOT increment in caller).
        Assert.False(await mirror.MirrorDeleteAsync("ignored-src", dst, CancellationToken.None));
    }

    [Fact]
    public async Task MirrorCreateOrChangeAsync_ReturnsBytes_OrZero()
    {
        // F2 plumbing — MirrorCreateOrChangeAsync return value drives the
        // real-time BytesCopiedTotal counter.
        var rl     = new ByteRateLimiter(0);
        var copier = new ThrottledFileCopier(rl, _log);
        var mirror = new FileMirror(_log, copier, retryCount: 1);

        string src = Path.Combine(_root, "in.txt");
        string dst = Path.Combine(_root, "out.txt");
        File.WriteAllText(src, "Hello, world!");      // 13 bytes

        long bytes1 = await mirror.MirrorCreateOrChangeAsync(src, dst, CancellationToken.None);
        Assert.Equal(13, bytes1);

        // Second call: dst is already up-to-date → returns 0 (counter NOT
        // incremented in caller).
        long bytes2 = await mirror.MirrorCreateOrChangeAsync(src, dst, CancellationToken.None);
        Assert.Equal(0, bytes2);

        // Missing src → 0.
        long bytes3 = await mirror.MirrorCreateOrChangeAsync(
            Path.Combine(_root, "no-such-file"), dst, CancellationToken.None);
        Assert.Equal(0, bytes3);
    }
}
