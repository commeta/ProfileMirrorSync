using ProfileMirrorSync.Models;
using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Regression tests for the audit F-2 finding: Pass 2 of
/// ReconcileRootAsync was supposed to delete destination files whose source
/// path is excluded by the current ignore policy, but the
/// implementation only checked <c>!File.Exists(srcFile)</c>.  As a result,
/// build artefacts (\bin\, \obj\, \.vs\, \node_modules\, \.git\) that
/// pre-dated the exclude defaults stayed on the server forever.
///
/// These tests cover the file-system contract of FileMirror.ReconcileRootAsync
/// directly — using a real temp directory for both src and dst — so we
/// validate the behaviour, not the implementation.
/// </summary>
public class FileMirrorPass2Tests : IDisposable
{
    private readonly string _root;
    private readonly Logger _log;

    public FileMirrorPass2Tests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PMSPass2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        string logDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logDir);
        _log = new Logger(logDir, AppLogLevel.Warning);
    }

    public void Dispose()
    {
        try { _log.Dispose(); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private FileMirror MakeMirror()
    {
        var rl     = new ByteRateLimiter(0);  // unlimited for tests
        var copier = new ThrottledFileCopier(rl, _log,
            resume: null, resumeEnabled: false,
            resumeMinBytes: long.MaxValue,
            lowerIoPriority: false);
        return new FileMirror(_log, copier, retryCount: 1);
    }

    [Fact]
    public async Task Pass2_DeletesOrphan_WhenSourceFileMissing()
    {
        // Baseline: the original behaviour must still work.
        string src = Path.Combine(_root, "src");
        string dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        // the empty-source guard (§4.1) suppresses Pass 2 when the
        // source walked ZERO files, so this baseline test must give the source
        // at least one live file.  That file is mirrored normally; the separate
        // orphan in dst (no src counterpart) is what Pass 2 must still remove.
        File.WriteAllText(Path.Combine(src, "live.txt"), "live");

        // dst has a file with no corresponding src file → orphan.
        File.WriteAllText(Path.Combine(dst, "orphan.txt"), "stale");

        var mirror = MakeMirror();
        await mirror.ReconcileRootAsync(src, dst,
            shouldIgnore: _ => false,
            opts: new ReconcileOptions(0, 0, 0),
            onFileCopied: null,
            ct: CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(dst, "orphan.txt")));
        // Sanity: the live file did get mirrored.
        Assert.True(File.Exists(Path.Combine(dst, "live.txt")));
    }

    [Fact]
    public async Task Pass2_DeletesIgnoredPath_EvenWhenSourceExists()
    {
        // F-2 fix.  This is the regression case: src file exists,
        // but the current ignore policy says skip it.  Pre- the file
        // stayed on dst forever after being copied by an earlier version.
        string src = Path.Combine(_root, "src");
        string dst = Path.Combine(_root, "dst");
        string srcBin = Path.Combine(src, "bin");
        string dstBin = Path.Combine(dst, "bin");
        Directory.CreateDirectory(srcBin);
        Directory.CreateDirectory(dstBin);

        // Both src and dst have the same build artefact.
        File.WriteAllText(Path.Combine(srcBin, "app.exe"), "binary");
        File.WriteAllText(Path.Combine(dstBin, "app.exe"), "binary");

        var mirror = MakeMirror();
        // ignore anything under \bin\.
        Func<string, bool> ignore = p => p.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);

        await mirror.ReconcileRootAsync(src, dst,
            shouldIgnore: ignore,
            opts: new ReconcileOptions(0, 0, 0),
            onFileCopied: null,
            ct: CancellationToken.None);

        // After the fix: dst\bin\app.exe is deleted.
        Assert.False(File.Exists(Path.Combine(dstBin, "app.exe")),
            "Pass 2 should delete a destination file whose source path is excluded.");
    }

    [Fact]
    public async Task Pass2_KeepsFile_WhenSourceExistsAndNotIgnored()
    {
        // Negative case: a file that's neither orphan nor ignored stays put.
        string src = Path.Combine(_root, "src");
        string dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(src, "keep.txt"), "important");
        File.WriteAllText(Path.Combine(dst, "keep.txt"), "important");

        var mirror = MakeMirror();
        await mirror.ReconcileRootAsync(src, dst,
            shouldIgnore: _ => false,
            opts: new ReconcileOptions(0, 0, 0),
            onFileCopied: null,
            ct: CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(dst, "keep.txt")));
    }

    // ── — Pass 2b: empty orphan directory cleanup ─────────────────────

    [Fact]
    public async Task Pass2b_DeletesEmptyOrphanDirectory()
    {
        // regression: pre-v2.4.14 reconcile deleted orphan FILES on
        // dst but left empty dirs.  Over time these accumulated visually.
        // Pass 2b now sweeps dst dirs bottom-up and removes those that are
        // (a) not present on src AND (b) empty after Pass 2's file deletes.
        string src = Path.Combine(_root, "src");
        string dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        // dst has a subdir that does NOT exist on src.  Empty.  Must go.
        string emptyOrphanDir = Path.Combine(dst, "OldProject");
        Directory.CreateDirectory(emptyOrphanDir);

        // A nested empty orphan should also go (bottom-up sweep).
        string nested = Path.Combine(dst, "OldProject2", "deeper");
        Directory.CreateDirectory(nested);

        var mirror = MakeMirror();
        var summary = await mirror.ReconcileRootAsync(src, dst,
            shouldIgnore: _ => false,
            opts: new ReconcileOptions(0, 0, 0),
            onFileCopied: null,
            ct: CancellationToken.None);

        Assert.False(Directory.Exists(emptyOrphanDir),
            "Empty orphan directory should be removed by Pass 2b.");
        Assert.False(Directory.Exists(nested),
            "Nested empty orphan directory should be removed by Pass 2b (bottom-up).");
        Assert.False(Directory.Exists(Path.Combine(dst, "OldProject2")),
            "Parent of a removed nested empty orphan should also be removed in the same pass " +
            "(deepest-first sort guarantees the child is gone before the parent is evaluated).");

        // dstRoot itself must remain — it's the user's backup folder, not an orphan.
        Assert.True(Directory.Exists(dst));

        // Empty orphans count toward OrphansDeleted (same semantic).
        // Three dirs total: OldProject, OldProject2/deeper, OldProject2.
        Assert.Equal(3, summary.OrphansDeleted);
    }

    [Fact]
    public async Task Pass2b_KeepsEmptyDirectory_WhenSourceExists()
    {
        // Negative case: empty dst dir whose src counterpart exists (also
        // empty) must NOT be removed.
        string src = Path.Combine(_root, "src");
        string dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        string sharedEmpty = "SharedFolder";
        Directory.CreateDirectory(Path.Combine(src, sharedEmpty));
        Directory.CreateDirectory(Path.Combine(dst, sharedEmpty));

        var mirror = MakeMirror();
        await mirror.ReconcileRootAsync(src, dst,
            shouldIgnore: _ => false,
            opts: new ReconcileOptions(0, 0, 0),
            onFileCopied: null,
            ct: CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(dst, sharedEmpty)),
            "Empty dst directory whose src counterpart exists must be preserved.");
    }

    [Fact]
    public async Task Pass2b_DoesNotDeleteDstRoot_EvenWhenEmpty()
    {
        // Edge case: src is empty, dst is empty, no orphans.  Pass 2b must
        // never remove dstRoot itself — that's the user's backup folder.
        string src = Path.Combine(_root, "src");
        string dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        var mirror = MakeMirror();
        await mirror.ReconcileRootAsync(src, dst,
            shouldIgnore: _ => false,
            opts: new ReconcileOptions(0, 0, 0),
            onFileCopied: null,
            ct: CancellationToken.None);

        Assert.True(Directory.Exists(dst), "dstRoot itself must never be removed by Pass 2b.");
    }
}
