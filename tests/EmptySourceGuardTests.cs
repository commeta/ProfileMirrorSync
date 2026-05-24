using ProfileMirrorSync.Models;
using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Regression tests for the empty-source sanity guard.
///
/// PMS is a ONE-WAY mirror: Pass 2 deletes every dst file with no live src
/// counterpart.  If the source root exists but yields ZERO files (profile not
/// loaded at logon, OneDrive placeholders dehydrated, AV quarantine, ACL slip),
/// blindly running Pass 2 would wipe the only server-side copy of the user's
/// data.  The guard skips Pass 2 (and Pass 2b) when the source is empty but the
/// destination has files, and logs a Warn.  The next reconcile retries once the
/// source is readable again.
/// </summary>
public class EmptySourceGuardTests : IDisposable
{
    private readonly string _root;
    private readonly Logger _log;

    public EmptySourceGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PMSGuard_" + Guid.NewGuid().ToString("N"));
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

    private FileMirror MakeMirror(bool guard = true)
    {
        var rl     = new ByteRateLimiter(0);
        var copier = new ThrottledFileCopier(rl, _log,
            resume: null, resumeEnabled: false,
            resumeMinBytes: long.MaxValue, lowerIoPriority: false);
        return new FileMirror(_log, copier, retryCount: 1, deletionSafetyGuard: guard);
    }

    [Fact]
    public async Task EmptySource_WithPopulatedDst_PreservesDstFiles()
    {
        // The dangerous scenario: src exists but has zero files, dst has data.
        string src = Path.Combine(_root, "src");
        string dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);   // exists, but EMPTY
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(dst, "important1.txt"), "user data");
        File.WriteAllText(Path.Combine(dst, "important2.txt"), "more data");
        Directory.CreateDirectory(Path.Combine(dst, "sub"));
        File.WriteAllText(Path.Combine(dst, "sub", "nested.txt"), "nested data");

        var mirror  = MakeMirror();
        var summary = await mirror.ReconcileRootAsync(src, dst,
            shouldIgnore: _ => false,
            opts: new ReconcileOptions(0, 0, 0),
            onFileCopied: null,
            ct: CancellationToken.None);

        // Nothing must be deleted.
        Assert.True(File.Exists(Path.Combine(dst, "important1.txt")));
        Assert.True(File.Exists(Path.Combine(dst, "important2.txt")));
        Assert.True(File.Exists(Path.Combine(dst, "sub", "nested.txt")));
        Assert.Equal(0, summary.OrphansDeleted);
    }

    [Fact]
    public async Task EmptySource_EmptyDst_IsNoOp()
    {
        // Both empty → guard is irrelevant, nothing to do, no crash.
        string src = Path.Combine(_root, "src");
        string dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        var mirror  = MakeMirror();
        var summary = await mirror.ReconcileRootAsync(src, dst,
            shouldIgnore: _ => false,
            opts: new ReconcileOptions(0, 0, 0),
            onFileCopied: null,
            ct: CancellationToken.None);

        Assert.Equal(0, summary.OrphansDeleted);
    }

    [Fact]
    public async Task NonEmptySource_StillDeletesGenuineOrphans()
    {
        // Guard must NOT suppress legitimate orphan cleanup: if src has at least
        // one file, Pass 2 runs normally and removes dst files with no src match.
        string src = Path.Combine(_root, "src");
        string dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(src, "keep.txt"), "live");
        File.WriteAllText(Path.Combine(dst, "keep.txt"), "live");
        File.WriteAllText(Path.Combine(dst, "orphan.txt"), "stale");

        var mirror  = MakeMirror();
        var summary = await mirror.ReconcileRootAsync(src, dst,
            shouldIgnore: _ => false,
            opts: new ReconcileOptions(0, 0, 0),
            onFileCopied: null,
            ct: CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(dst, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(dst, "orphan.txt")));
        Assert.True(summary.OrphansDeleted >= 1);
    }

    [Fact]
    public async Task AllIgnoredSource_DoesNotTripGuard()
    {
        // A tree whose every file is ignored still has filesWalked > 0 (the walk
        // counts before shouldIgnore), so the guard does NOT trip and Pass 2 may
        // clean ignored dst files.  This pins that the guard keys on the WALK
        // count, not the COPY count.
        string src = Path.Combine(_root, "src");
        string dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        // src has files, but all are ignored.
        File.WriteAllText(Path.Combine(src, "a.tmp"), "x");
        File.WriteAllText(Path.Combine(dst, "a.tmp"), "x");

        var mirror  = MakeMirror();
        var summary = await mirror.ReconcileRootAsync(src, dst,
            shouldIgnore: p => p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase),
            opts: new ReconcileOptions(0, 0, 0),
            onFileCopied: null,
            ct: CancellationToken.None);

        // Ignored dst file removed (Pass 2 ran — guard did not trip).
        Assert.False(File.Exists(Path.Combine(dst, "a.tmp")));
        Assert.True(summary.OrphansDeleted >= 1);
    }

    [Fact]
    public async Task GuardDisabled_EmptySource_DeletesOrphans_LegacyBehaviour()
    {
        // the guard is OPT-IN (off by default).  With it disabled, the
        // classic one-way-mirror behaviour holds: an empty source wipes dst.
        string src = Path.Combine(_root, "src");
        string dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);   // empty
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(dst, "orphan.txt"), "stale");

        var mirror  = MakeMirror(guard: false);   // guard OFF
        var summary = await mirror.ReconcileRootAsync(src, dst,
            shouldIgnore: _ => false,
            opts: new ReconcileOptions(0, 0, 0),
            onFileCopied: null,
            ct: CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(dst, "orphan.txt")));
        Assert.True(summary.OrphansDeleted >= 1);
    }
}
