using ProfileMirrorSync.Models;
using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Regression test for audit §3.7: <c>RetryAsync</c> in FileMirror
/// used to log Error and return silently after the final attempt failed,
/// making the caller (and the UI) believe the copy succeeded when in fact
/// no file was written.  Now it throws so the outer DispatchAsync catch
/// records the real failure and the next reconcile retries.
///
/// We trigger the failure path by pointing the destination at a path that
/// is GUARANTEED to fail on Windows: a name containing an illegal char like
/// '|', '<', '>', '?', '*' or a null byte.  These all yield
/// <c>IOException</c> / <c>ArgumentException</c> at the open-stream step
/// inside ThrottledFileCopier, before any bytes are written.
/// </summary>
public class RetryAsyncThrowTests : IDisposable
{
    private readonly string _root;
    private readonly Logger _log;

    public RetryAsyncThrowTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PMSRetry_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task MirrorCreateOrChange_ThrowsAfterAllRetriesFail()
    {
        string src = Path.Combine(_root, "src.txt");
        File.WriteAllText(src, "hello");

        // Destination with an illegal Windows filename character ('|').
        // This will fail every attempt — no transient retry can possibly
        // succeed.  Pre- the method would log Error and return
        // silently; now we expect an exception to escape.
        string dst = Path.Combine(_root, "bad|name.txt");

        var rl     = new ByteRateLimiter(0);
        var copier = new ThrottledFileCopier(rl, _log,
            resume: null, resumeEnabled: false,
            resumeMinBytes: long.MaxValue,
            lowerIoPriority: false);
        // retryCount=2 — fast test
        var mirror = new FileMirror(_log, copier, retryCount: 2);

        await Assert.ThrowsAnyAsync<Exception>(
            async () => { await mirror.MirrorCreateOrChangeAsync(src, dst, CancellationToken.None); });
    }
}
