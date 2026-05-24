using ProfileMirrorSync.Models;
using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Publish-mode tests for ThrottledFileCopier.
///
/// DirectWrite (legacy) and TempThenRename must both produce a byte-identical
/// destination.  TempThenRename must leave no *.pms_tmp artefact behind and
/// must atomically replace an existing destination.
/// </summary>
public class ThrottledFileCopierPublishTests : IDisposable
{
    private readonly string _dir;
    private readonly Logger _log;

    public ThrottledFileCopierPublishTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PMSPublish_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _log = new Logger(Path.Combine(_dir, "Logs"), AppLogLevel.Warning);
    }

    public void Dispose()
    {
        try { _log.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ThrottledFileCopier Make(FilePublishMode mode) =>
        new(new ByteRateLimiter(0), _log,
            resume: null, resumeEnabled: false,
            resumeMinBytes: long.MaxValue, lowerIoPriority: false, publishMode: mode);

    [Theory]
    [InlineData(FilePublishMode.DirectWrite)]
    [InlineData(FilePublishMode.TempThenRename)]
    public async Task Copy_ProducesIdenticalBytes(FilePublishMode mode)
    {
        string src = Path.Combine(_dir, "src.bin");
        string dst = Path.Combine(_dir, "out", "dst.bin");
        byte[] data = new byte[200_000];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(src, data);

        await Make(mode).CopyAsync(src, dst, CancellationToken.None);

        Assert.True(File.Exists(dst));
        Assert.Equal(data, File.ReadAllBytes(dst));
    }

    [Fact]
    public async Task TempThenRename_LeavesNoTempArtefact()
    {
        string src = Path.Combine(_dir, "src.bin");
        string dst = Path.Combine(_dir, "dst.bin");
        File.WriteAllText(src, "hello world");

        await Make(FilePublishMode.TempThenRename).CopyAsync(src, dst, CancellationToken.None);

        Assert.True(File.Exists(dst));
        Assert.False(File.Exists(dst + ".pms_tmp"));
        Assert.Empty(Directory.GetFiles(_dir, "*.pms_tmp"));
    }

    [Fact]
    public async Task TempThenRename_AtomicallyReplacesExistingDestination()
    {
        string src = Path.Combine(_dir, "src.bin");
        string dst = Path.Combine(_dir, "dst.bin");
        File.WriteAllText(dst, "OLD CONTENT TO BE REPLACED");
        File.WriteAllText(src, "new content");

        await Make(FilePublishMode.TempThenRename).CopyAsync(src, dst, CancellationToken.None);

        Assert.Equal("new content", File.ReadAllText(dst));
    }
}
