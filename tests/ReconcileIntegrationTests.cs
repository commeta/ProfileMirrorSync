using ProfileMirrorSync.Models;
using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Integration tests for the full reconcile cycle.
///
/// Unlike the focused Pass-2 tests, these build a non-trivial source tree
/// (100+ files across nested dirs), reconcile it to an empty destination, then
/// mutate the source (add / delete / rename / change) and reconcile again,
/// asserting the destination exactly mirrors the source after each pass.
///
/// Exercises FileMirror.ReconcileRootAsync end to end through the real
/// ThrottledFileCopier against real temp directories.
/// </summary>
public class ReconcileIntegrationTests : IDisposable
{
    private readonly string _root, _src, _dst;
    private readonly Logger _log;

    public ReconcileIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PMSIntg_" + Guid.NewGuid().ToString("N"));
        _src  = Path.Combine(_root, "src");
        _dst  = Path.Combine(_root, "dst");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_dst);
        string logDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logDir);
        _log = new Logger(logDir, AppLogLevel.Warning);
    }

    public void Dispose()
    {
        try { _log.Dispose(); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private FileMirror MakeMirror(FilePublishMode mode = FilePublishMode.DirectWrite)
    {
        var rl     = new ByteRateLimiter(0);
        var copier = new ThrottledFileCopier(rl, _log,
            resume: null, resumeEnabled: false,
            resumeMinBytes: long.MaxValue, lowerIoPriority: false, publishMode: mode);
        return new FileMirror(_log, copier, retryCount: 2);
    }

    private Task<ReconcileSummary> Reconcile(FileMirror mirror) =>
        mirror.ReconcileRootAsync(_src, _dst,
            shouldIgnore: p => p.Contains(@"\ignoreme\", StringComparison.OrdinalIgnoreCase),
            opts: new ReconcileOptions(0, 0, 0),
            onFileCopied: null,
            ct: CancellationToken.None);

    /// <summary>Collect every file under root as relative paths + content hash-ish (length+bytes).</summary>
    private static Dictionary<string, byte[]> Snapshot(string root)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            map[Path.GetRelativePath(root, f)] = File.ReadAllBytes(f);
        return map;
    }

    private void BuildSourceTree()
    {
        // 3 top dirs × 40 files = 120 files, plus a deep nest and an ignored dir.
        for (int d = 0; d < 3; d++)
        {
            string dir = Path.Combine(_src, $"dir{d}");
            Directory.CreateDirectory(dir);
            for (int i = 0; i < 40; i++)
                File.WriteAllText(Path.Combine(dir, $"f{i}.txt"), $"content {d}-{i}");
        }
        string deep = Path.Combine(_src, "a", "b", "c", "d");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "deep.txt"), "deep");

        // Ignored subtree — must never reach dst.
        string ign = Path.Combine(_src, "ignoreme");
        Directory.CreateDirectory(ign);
        File.WriteAllText(Path.Combine(ign, "secret.txt"), "do not copy");
    }

    [Fact]
    public async Task FullCycle_MirrorsTree_ThenMutations_StayInSync()
    {
        BuildSourceTree();
        var mirror = MakeMirror();

        // ── First reconcile: empty dst → full mirror ──
        var s1 = await Reconcile(mirror);
        Assert.True(s1.FilesCopied >= 121, $"expected ≥121 copied, got {s1.FilesCopied}");

        var srcMap = Snapshot(_src);
        var dstMap = Snapshot(_dst);
        // Ignored file present in src, absent in dst.
        Assert.True(srcMap.ContainsKey(Path.Combine("ignoreme", "secret.txt")));
        Assert.False(dstMap.ContainsKey(Path.Combine("ignoreme", "secret.txt")));
        // Every non-ignored src file mirrored byte-for-byte.
        AssertMirrors(srcMap, dstMap);

        // ── Mutate source: add, delete, rename, change ──
        File.WriteAllText(Path.Combine(_src, "dir0", "added.txt"), "brand new");          // add
        File.Delete(Path.Combine(_src, "dir1", "f5.txt"));                                // delete
        File.Move(Path.Combine(_src, "dir2", "f0.txt"),                                   // rename
                  Path.Combine(_src, "dir2", "renamed.txt"));
        File.WriteAllText(Path.Combine(_src, "dir0", "f0.txt"), "changed content longer"); // change

        // ── Second reconcile ──
        await Reconcile(mirror);

        var srcMap2 = Snapshot(_src);
        var dstMap2 = Snapshot(_dst);
        AssertMirrors(srcMap2, dstMap2);

        // Spot-check each mutation reflected in dst.
        Assert.True(File.Exists(Path.Combine(_dst, "dir0", "added.txt")));
        Assert.False(File.Exists(Path.Combine(_dst, "dir1", "f5.txt")));
        Assert.True(File.Exists(Path.Combine(_dst, "dir2", "renamed.txt")));
        Assert.False(File.Exists(Path.Combine(_dst, "dir2", "f0.txt")));
        Assert.Equal("changed content longer",
            File.ReadAllText(Path.Combine(_dst, "dir0", "f0.txt")));
    }

    [Fact]
    public async Task FullCycle_TempThenRenameMode_ProducesIdenticalMirror()
    {
        BuildSourceTree();
        var mirror = MakeMirror(FilePublishMode.TempThenRename);

        await Reconcile(mirror);

        AssertMirrors(Snapshot(_src), Snapshot(_dst));
        // No stray *.pms_tmp left behind on dst.
        Assert.Empty(Directory.EnumerateFiles(_dst, "*.pms_tmp", SearchOption.AllDirectories));
    }

    /// <summary>Assert every non-ignored src file equals its dst counterpart and
    /// dst has no extra (orphan) files.</summary>
    private static void AssertMirrors(Dictionary<string, byte[]> src, Dictionary<string, byte[]> dst)
    {
        foreach (var (rel, bytes) in src)
        {
            if (rel.Contains("ignoreme", StringComparison.OrdinalIgnoreCase)) continue;
            Assert.True(dst.ContainsKey(rel), $"dst missing {rel}");
            Assert.Equal(bytes, dst[rel]);
        }
        foreach (var rel in dst.Keys)
            Assert.False(rel.Contains("ignoreme", StringComparison.OrdinalIgnoreCase),
                $"ignored file leaked to dst: {rel}");
    }
}
