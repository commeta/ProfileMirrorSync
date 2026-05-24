using ProfileMirrorSync.Models;
using ProfileMirrorSync.Services;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// SettingsStore persistence tests.
///
/// Uses the internal base-directory constructor to isolate each test in a temp
/// folder, so we exercise Save/Load, the corrupt-file recovery path (bad file
/// backed up + defaults returned), the v11→v12 migration, and atomic-write temp
/// cleanup — all without touching the real %ProgramData%.
/// </summary>
public class SettingsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly Logger _log;
    private readonly SettingsStore _store;

    public SettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PMSSettings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _log = new Logger(Path.Combine(_dir, "Logs"), AppLogLevel.Warning);
        _store = new SettingsStore(_dir) { Log = _log };
    }

    public void Dispose()
    {
        try { _log.Dispose(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsKeyFields()
    {
        var s = new AppSettings
        {
            DestinationRoot = @"\\SRV\Backup",
            MaxBandwidthBitsPerSecond = 3_000_000,
            PublishMode = FilePublishMode.TempThenRename,
            PostSyncEnabled = true,
            PostSyncExePath = @"C:\7z\7z.exe",
        };
        _store.Save(s);

        var loaded = _store.Load();
        Assert.Equal(@"\\SRV\Backup", loaded.DestinationRoot);
        Assert.Equal(3_000_000, loaded.MaxBandwidthBitsPerSecond);
        Assert.Equal(FilePublishMode.TempThenRename, loaded.PublishMode);
        Assert.True(loaded.PostSyncEnabled);
        Assert.Equal(@"C:\7z\7z.exe", loaded.PostSyncExePath);
    }

    [Fact]
    public void Save_LeavesNoTempFileBehind()
    {
        _store.Save(new AppSettings());
        Assert.False(File.Exists(_store.SettingsPath + ".tmp"),
            "atomic save must not leave a .tmp artefact");
        Assert.True(File.Exists(_store.SettingsPath));
    }

    [Fact]
    public void Load_CorruptFile_BacksUpAndReturnsDefaults()
    {
        // Write garbage to the settings path, then load.
        File.WriteAllText(_store.SettingsPath, "{ this is : not valid json ]");

        var loaded = _store.Load();

        // Defaults returned (not a throw).
        Assert.NotNull(loaded);
        Assert.Equal(new AppSettings().MaxBandwidthBitsPerSecond, loaded.MaxBandwidthBitsPerSecond);

        // The bad file was preserved as a .bad-* backup for manual recovery.
        var backups = Directory.GetFiles(_dir, "settings.json.bad-*");
        Assert.NotEmpty(backups);
    }

    [Fact]
    public void Load_MigratesV11ToCurrent()
    {
        // A v11 file (pre-2.5.0) loaded through the store must come out stamped
        // at the current version with the new fields at their safe defaults.
        File.WriteAllText(_store.SettingsPath, """
            {
              "DestinationRoot": "\\\\SRV\\Backup",
              "MirrorLogs": true,
              "SettingsVersion": 11
            }
            """);

        var loaded = _store.Load();
        Assert.Equal(15, loaded.SettingsVersion);
        Assert.True(loaded.MirrorLogs);                               // preserved
        Assert.Equal(FilePublishMode.DirectWrite, loaded.PublishMode); // new default
        Assert.False(loaded.PostSyncEnabled);                         // new default
        Assert.True(loaded.ReconcileOnWake);                          // default
        Assert.False(loaded.ReconcileOnUnlock);                       // default
    }

    [Fact]
    public void Load_IgnoresRemovedMonitorKeys()
    {
        // A pre-2.5.0 file still carries Monitor* keys + a Cooldowns-style blob.
        // Load must not throw and must apply the surviving fields.
        File.WriteAllText(_store.SettingsPath, """
            {
              "DestinationRoot": "\\\\SRV\\B",
              "MonitoringEnabled": true,
              "MonitorIntervalHours": 6,
              "PasswordMaxAgeDays": 90,
              "SettingsVersion": 11
            }
            """);

        var loaded = _store.Load();
        Assert.Equal(@"\\SRV\B", loaded.DestinationRoot);
        Assert.Equal(15, loaded.SettingsVersion);
    }
}
