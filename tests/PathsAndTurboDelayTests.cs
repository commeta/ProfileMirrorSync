using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// ReconcileOptions now suppresses the artificial inter-file / batch
/// pauses while turbo is active (the byte-rate cap still applies).  These tests
/// pin that behaviour so a regression can't silently restore the pauses.
/// </summary>
public class ReconcileOptionsTurboTests
{
    [Fact]
    public void WhenTurboInactive_DelaysApplyAsConfigured()
    {
        var opts = new ReconcileOptions(FileDelayMs: 20, BatchSize: 50, BatchPauseMs: 500)
        {
            IsTurboActive = () => false,
        };
        Assert.Equal(20,  opts.CurrentFileDelayMs);
        Assert.Equal(500, opts.CurrentBatchPauseMs);
    }

    [Fact]
    public void WhenTurboActive_DelaysAreSuppressed()
    {
        var opts = new ReconcileOptions(FileDelayMs: 20, BatchSize: 50, BatchPauseMs: 500)
        {
            IsTurboActive = () => true,
        };
        Assert.Equal(0, opts.CurrentFileDelayMs);
        Assert.Equal(0, opts.CurrentBatchPauseMs);
        // BatchSize is not a delay — it's unaffected (still used for cadence math).
        Assert.Equal(50, opts.BatchSize);
    }

    [Fact]
    public void WhenNoTurboPredicate_DelaysAlwaysApply()
    {
        // Null predicate (e.g. tests / non-controller callers) ⇒ never turbo.
        var opts = new ReconcileOptions(20, 50, 500);
        Assert.Equal(20,  opts.CurrentFileDelayMs);
        Assert.Equal(500, opts.CurrentBatchPauseMs);
    }

    [Fact]
    public void TurboPredicate_IsReadDynamically()
    {
        bool turbo = false;
        var opts = new ReconcileOptions(20, 50, 500) { IsTurboActive = () => turbo };
        Assert.Equal(20, opts.CurrentFileDelayMs);   // before
        turbo = true;
        Assert.Equal(0,  opts.CurrentFileDelayMs);   // flips live, mid-reconcile
        turbo = false;
        Assert.Equal(20, opts.CurrentFileDelayMs);   // and back
    }
}

/// <summary>
/// AppPaths resolves the per-user data dir and a writable logs dir.
/// </summary>
public class AppPathsTests
{
    [Fact]
    public void DataDirectory_IsUnderLocalAppData()
    {
        string localAppData = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(localAppData, AppPaths.DataDirectory);
        Assert.EndsWith("ProfileMirrorSync", AppPaths.DataDirectory);
    }

    [Fact]
    public void LogsDirectory_IsUnderDataDirectory()
    {
        // Logs live with the per-user settings (DataDirectory\Logs), so logging
        // works regardless of where the exe is installed.
        Assert.StartsWith(AppPaths.DataDirectory, AppPaths.LogsDirectory);
        Assert.EndsWith("Logs", AppPaths.LogsDirectory);
        System.IO.Directory.CreateDirectory(AppPaths.LogsDirectory);
        Assert.True(System.IO.Directory.Exists(AppPaths.LogsDirectory));
    }

    [Fact]
    public void LegacySharedDirectory_IsUnderProgramData()
    {
        string programData = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.CommonApplicationData);
        Assert.StartsWith(programData, AppPaths.LegacySharedDirectory);
    }
}
