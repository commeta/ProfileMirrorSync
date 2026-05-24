using ProfileMirrorSync.Models;
using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Groups test classes that mutate the shared %ProgramData% monitor_state.json
/// so xUnit runs them serially (no cross-class races on that singleton file).
/// No fixture needed — the attribute alone disables parallelism between members.
/// </summary>
[CollectionDefinition("PersistentState")]
public class PersistentStateCollection { }

/// <summary>
/// Filesystem tests for LogMirrorService: it must copy
/// CLOSED log files (not today's live log) to {machineRoot}/Logs/, and skip
/// already-current files.  We point the Logger at an isolated temp dir, drop a
/// couple of dated log files there, and mirror to another temp dir.
///
/// Note: LogMirrorService gates on PersistentState.LastLogMirrorUtc via the
/// real PersistentStateStore (%ProgramData%).  These tests reset that stamp to
/// null before running so the gate is open — same shared-path pattern as the
/// existing PersistentStateStoreTests.
/// </summary>
[Collection("PersistentState")]
public class LogMirrorServiceTests : IDisposable
{
    private readonly string _logDir;
    private readonly string _destRoot;
    private readonly Logger _log;
    private readonly PersistentStateStore _state;

    public LogMirrorServiceTests()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "PMSLogMirror_" + Guid.NewGuid().ToString("N"));
        _logDir   = Path.Combine(baseDir, "Logs");
        _destRoot = Path.Combine(baseDir, "dest");
        Directory.CreateDirectory(_logDir);
        Directory.CreateDirectory(_destRoot);
        _log   = new Logger(_logDir, AppLogLevel.Warning);
        _state = new PersistentStateStore(_log);
        // Open the gate: pretend we've never mirrored.
        _state.Update(s => s.LastLogMirrorUtc = null);
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_logDir)!, recursive: true); } catch { }
    }

    private LogMirrorService Make() =>
        new(_log, _state, new ByteRateLimiter(0), lowerIoPriority: false, () => _destRoot);

    [Fact]
    public async Task CopiesClosedLog_SkipsTodaysLiveLog()
    {
        // A closed (yesterday-dated) log file and today's live log.  The live
        // log already exists and is held open by the Logger — we must NOT touch
        // it here (the handle is exclusive for append); MirrorIfDueAsync must
        // skip it on its own.
        string yesterday = $"pms-{DateTime.Now.AddDays(-1):yyyy-MM-dd}.log";
        string today     = $"pms-{DateTime.Now:yyyy-MM-dd}.log";
        File.WriteAllText(Path.Combine(_logDir, yesterday), "old closed log\n");

        await Make().MirrorIfDueAsync(CancellationToken.None);

        string dstLogs = Path.Combine(_destRoot, "Logs");
        Assert.True(File.Exists(Path.Combine(dstLogs, yesterday)), "closed log should be mirrored");
        Assert.False(File.Exists(Path.Combine(dstLogs, today)),   "today's live log must NOT be mirrored");
    }

    [Fact]
    public async Task StampsLastLogMirrorUtc_OnSuccess()
    {
        string yesterday = $"pms-{DateTime.Now.AddDays(-1):yyyy-MM-dd}.log";
        File.WriteAllText(Path.Combine(_logDir, yesterday), "old\n");

        await Make().MirrorIfDueAsync(CancellationToken.None);

        Assert.NotNull(_state.Snapshot().LastLogMirrorUtc);
    }

    [Fact]
    public async Task SkipsWhenRecentlyMirrored()
    {
        string yesterday = $"pms-{DateTime.Now.AddDays(-1):yyyy-MM-dd}.log";
        File.WriteAllText(Path.Combine(_logDir, yesterday), "old\n");
        // Close the gate: mirrored 1 hour ago (< 23h).
        _state.Update(s => s.LastLogMirrorUtc = DateTime.UtcNow.AddHours(-1));

        await Make().MirrorIfDueAsync(CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_destRoot, "Logs", yesterday)),
            "should skip mirroring within the 23h gate");
    }
}
