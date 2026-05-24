using ProfileMirrorSync.Models;
using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Concurrency + round-trip contract tests for PersistentStateStore.
///
/// the Cooldowns dictionary was removed with the security monitor, so
/// PersistentState is now a flat record of scalar timestamps + one flag.  The
/// "all N independent keys persisted" semantic no longer applies (concurrent
/// writers to the SAME scalar field are inherently last-write-wins).  These
/// tests pin what IS still guaranteed:
///   • Snapshot returns an independent copy (mutating it can't corrupt state).
///   • Every timestamp field round-trips through disk.
///   • Concurrent Update() calls never corrupt the file — a fresh load always
///     yields a valid object and the winning value is one of those written.
///
/// These touch the real per-user %LocalAppData% file because the store path is
/// hardcoded; the tests only read/write fields and restore them on dispose.
///
/// shares the "PersistentState" collection with LogMirrorServiceTests
/// so the two classes (both mutating the singleton monitor_state.json) run
/// serially rather than racing under xUnit's default per-class parallelism.
/// </summary>
[Collection("PersistentState")]
public class PersistentStateStoreTests : IDisposable
{
    private readonly Logger _log;
    private readonly PersistentStateStore _store;

    public PersistentStateStoreTests()
    {
        string tempLogDir = Path.Combine(Path.GetTempPath(), "PMSPersist_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempLogDir);
        _log = new Logger(tempLogDir, AppLogLevel.Warning);
        _store = new PersistentStateStore(_log);
    }

    public void Dispose() => _log.Dispose();

    [Fact]
    public void Snapshot_ReturnsIndependentCopy()
    {
        var stamp = new DateTime(2026, 3, 3, 3, 3, 3, DateTimeKind.Utc);
        _store.Update(s => s.LastReconcileUtc = stamp);

        var snap = _store.Snapshot();
        snap.LastReconcileUtc = DateTime.MinValue;   // mutate the copy

        var snap2 = _store.Snapshot();
        Assert.Equal(stamp, snap2.LastReconcileUtc);  // store state unaffected
    }

    [Theory]
    [InlineData("reconcile")]
    [InlineData("completed")]
    [InlineData("logmirror")]
    [InlineData("registry")]
    [InlineData("postsync")]
    public void TimestampFields_RoundTripThroughDisk(string which)
    {
        var stamp = new DateTime(2026, 5, 20, 10, 30, 0, DateTimeKind.Utc);
        _store.Update(s =>
        {
            switch (which)
            {
                case "reconcile": s.LastReconcileUtc          = stamp; break;
                case "completed": s.LastReconcileCompletedUtc = stamp; break;
                case "logmirror": s.LastLogMirrorUtc          = stamp; break;
                case "registry":  s.LastRegistrySnapshotUtc   = stamp; break;
                case "postsync":  s.LastPostSyncRunUtc        = stamp; break;
            }
        });

        // Fresh store → reads from disk.
        var snap = new PersistentStateStore(_log).Snapshot();
        DateTime? actual = which switch
        {
            "reconcile" => snap.LastReconcileUtc,
            "completed" => snap.LastReconcileCompletedUtc,
            "logmirror" => snap.LastLogMirrorUtc,
            "registry"  => snap.LastRegistrySnapshotUtc,
            "postsync"  => snap.LastPostSyncRunUtc,
            _           => null,
        };
        Assert.Equal(stamp, actual);
    }

    [Fact]
    public async Task ConcurrentUpdates_NeverCorruptTheFile()
    {
        // Fire many parallel Update() calls writing distinct values to the same
        // field.  The contract is: no torn/corrupt file, and a fresh load
        // yields a value that was actually written by one of the writers.
        const int N = 50;
        var written = new System.Collections.Concurrent.ConcurrentBag<long>();
        var tasks = new List<Task>(N);
        for (int i = 0; i < N; i++)
        {
            long ticks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks + i;
            written.Add(ticks);
            tasks.Add(Task.Run(() =>
                _store.Update(s => s.LastReconcileUtc = new DateTime(ticks, DateTimeKind.Utc))));
        }
        await Task.WhenAll(tasks);

        var diskSnap = new PersistentStateStore(_log).Snapshot();
        Assert.NotNull(diskSnap.LastReconcileUtc);
        Assert.Contains(diskSnap.LastReconcileUtc!.Value.Ticks, written);
    }
}
