using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Tests for FileMirror.IsUpToDate — the workhorse "should I skip this file"
/// check used by Pass 1, log-mirror, and resume-decision paths.
///
/// Contract (per code comments):
///   • dst missing                            → false
///   • sizes differ                           → false
///   • |Δmtime| &lt; 2s                       → true   (primary)
///   • dst mtime ≥ src mtime                  → true   (fallback for SMB/exFAT
///                                                     that don't preserve mtime)
///   • otherwise                              → false
/// </summary>
public class IsUpToDateTests : IDisposable
{
    private readonly string _dir;

    public IsUpToDateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PMSIsUpToDate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (string src, string dst) MakePair(byte[] srcBytes, byte[]? dstBytes = null)
    {
        string src = Path.Combine(_dir, "src_" + Guid.NewGuid().ToString("N") + ".bin");
        string dst = Path.Combine(_dir, "dst_" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(src, srcBytes);
        if (dstBytes is not null) File.WriteAllBytes(dst, dstBytes);
        return (src, dst);
    }

    [Fact]
    public void DestinationMissing_ReturnsFalse()
    {
        var (src, dst) = MakePair(new byte[] { 1, 2, 3 });
        // dst not created
        Assert.False(FileMirror.IsUpToDate(src, dst));
    }

    [Fact]
    public void SizesDiffer_ReturnsFalse()
    {
        var (src, dst) = MakePair(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3, 4 });
        Assert.False(FileMirror.IsUpToDate(src, dst));
    }

    [Fact]
    public void SameSizeAndMtimeWithin2Sec_ReturnsTrue()
    {
        var (src, dst) = MakePair(new byte[] { 1, 2, 3 }, new byte[] { 9, 9, 9 });
        var t = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(src, t);
        File.SetLastWriteTimeUtc(dst, t.AddSeconds(1)); // < 2s drift

        Assert.True(FileMirror.IsUpToDate(src, dst));
    }

    [Fact]
    public void SameSizeButMtimeDiffersMoreThan2Sec_ReturnsFalse()
    {
        // Src is newer by 10s and the fallback (dst >= src) doesn't apply.
        var (src, dst) = MakePair(new byte[] { 1, 2, 3 }, new byte[] { 9, 9, 9 });
        var t = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(dst, t);
        File.SetLastWriteTimeUtc(src, t.AddSeconds(10));

        Assert.False(FileMirror.IsUpToDate(src, dst));
    }

    [Fact]
    public void DestinationNewerThanSource_FallbackReturnsTrue()
    {
        // Simulates SMB / exFAT that doesn't preserve mtime on copy:
        // after the copy, dst mtime = "now" which is later than src mtime.
        // The check must NOT trigger a recopy in this normal case.
        var (src, dst) = MakePair(new byte[] { 1, 2, 3 }, new byte[] { 9, 9, 9 });
        var t = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(src, t);
        File.SetLastWriteTimeUtc(dst, t.AddSeconds(30)); // dst stamped later by FS

        Assert.True(FileMirror.IsUpToDate(src, dst));
    }

    [Fact]
    public void SourceMissing_ReturnsFalse()
    {
        // Exception path: source file doesn't exist.  IsUpToDate catches and
        // returns false, leaving the decision to higher-level enumeration logic.
        string src = Path.Combine(_dir, "does_not_exist.bin");
        string dst = Path.Combine(_dir, "dst.bin");
        File.WriteAllBytes(dst, new byte[] { 1 });
        Assert.False(FileMirror.IsUpToDate(src, dst));
    }
}
