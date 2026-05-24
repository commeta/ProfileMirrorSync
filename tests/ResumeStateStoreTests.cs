using ProfileMirrorSync.Models;
using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// ResumeStateStore round-trip + cleanup tests.
///
/// Note: ResumeStateStore is hardcoded to write under
/// %LocalAppData%\ProfileMirrorSync\resume\ ( per-user).  We can't
/// redirect that without a constructor parameter, so these tests do touch the
/// real per-user directory.  Each test uses unique source paths (per-test GUID) so they
/// don't collide with each other or with a real running PMS.  We clean up
/// our own sidecars at the end.
/// </summary>
public class ResumeStateStoreTests : IDisposable
{
    private readonly Logger _log;
    private readonly ResumeStateStore _store;
    private readonly List<string> _createdSrcPaths = new();

    public ResumeStateStoreTests()
    {
        string tempLogDir = Path.Combine(Path.GetTempPath(), "PMSTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempLogDir);
        _log   = new Logger(tempLogDir, AppLogLevel.Warning);
        _store = new ResumeStateStore(_log);
    }

    public void Dispose()
    {
        foreach (string src in _createdSrcPaths)
        {
            try { _store.Clear(src); } catch { }
        }
        _log.Dispose();
    }

    private string MakeUniqueSrcPath()
    {
        string p = $@"C:\TEST\{Guid.NewGuid()}\file.bin";
        _createdSrcPaths.Add(p);
        return p;
    }

    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        string src = MakeUniqueSrcPath();
        var state = new ResumeState
        {
            SrcPath         = src,
            DstPath         = @"\\SRV\Backup\file.bin",
            SrcLength       = 100_000_000,
            SrcLastWriteUtc = new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc),
            BytesCopied     = 50_000_000,
        };
        _store.Save(state, emitTrace: false);

        var loaded = _store.TryLoad(src);
        Assert.NotNull(loaded);
        Assert.Equal(state.SrcPath, loaded!.SrcPath);
        Assert.Equal(state.DstPath, loaded.DstPath);
        Assert.Equal(state.SrcLength, loaded.SrcLength);
        Assert.Equal(state.SrcLastWriteUtc, loaded.SrcLastWriteUtc);
        Assert.Equal(state.BytesCopied, loaded.BytesCopied);
    }

    [Fact]
    public void TryLoad_NonExistent_ReturnsNull()
    {
        string src = MakeUniqueSrcPath(); // not saved
        var loaded = _store.TryLoad(src);
        Assert.Null(loaded);
    }

    [Fact]
    public void Clear_RemovesSidecar()
    {
        string src = MakeUniqueSrcPath();
        _store.Save(new ResumeState
        {
            SrcPath = src, DstPath = "dst", SrcLength = 1, BytesCopied = 0,
        }, emitTrace: false);
        Assert.NotNull(_store.TryLoad(src));

        _store.Clear(src);
        Assert.Null(_store.TryLoad(src));
    }

    [Fact]
    public void Clear_NonExistent_Idempotent()
    {
        string src = MakeUniqueSrcPath();
        // Calling Clear on a non-existent sidecar must not throw.
        _store.Clear(src);
        _store.Clear(src); // second time
    }

    [Fact]
    public void SidecarPath_IsDeterministic()
    {
        string src = @"C:\some\path\file.bin";
        string p1  = _store.SidecarPath(src);
        string p2  = _store.SidecarPath(src);
        Assert.Equal(p1, p2);
        Assert.EndsWith(".json", p1);
    }

    [Fact]
    public void DifferentSourcePaths_DifferentSidecars()
    {
        string p1 = _store.SidecarPath(@"C:\file1.bin");
        string p2 = _store.SidecarPath(@"C:\file2.bin");
        Assert.NotEqual(p1, p2);
    }
}
