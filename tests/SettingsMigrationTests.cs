using ProfileMirrorSync.Models;
using ProfileMirrorSync.Services;
using System.Text.Json;
using Xunit;

namespace ProfileMirrorSync.Tests;

/// <summary>
/// Settings migration tests — pin the v3 → v10 upgrade chain so future
/// schema changes don't silently lose user data.
/// </summary>
public class SettingsMigrationTests
{
    [Fact]
    public void DefaultAppSettings_HasCurrentSettingsVersion()
    {
        var s = new AppSettings();
        Assert.Equal(15, s.SettingsVersion);
    }

    [Fact]
    public void DefaultAppSettings_V252_NewFieldsHaveCorrectDefaults()
    {
        // scheduled reconcile is ON by default (the disable checkbox is
        // opt-in).  SettingsVersion is the current model version.
        var s = new AppSettings();
        Assert.True(s.ReconcileEnabled);
        Assert.Equal(15, s.SettingsVersion);
    }

    [Fact]
    public void DefaultAppSettings_V251_NewFieldsHaveCorrectDefaults()
    {
        // turbo retuned and new event-trigger
        // toggles.  Turbo: 3 Mbit @ 1000 files, off during scheduled reconcile.
        // Event triggers: wake ON (FSW dead during sleep), unlock/logon OFF
        // (FSW live while locked → redundant).
        var s = new AppSettings();
        Assert.Equal(3,    s.TurboFirstRunBandwidthMbps);
        Assert.Equal(1000, s.TurboThresholdFiles);
        Assert.False(s.TurboOnReconcile);
        Assert.True(s.ReconcileOnWake);
        Assert.False(s.ReconcileOnUnlock);
        Assert.False(s.ReconcileOnLogon);
    }

    [Fact]
    public void DefaultAppSettings_V250_NewFieldsHaveCorrectDefaults()
    {
        // publish mode defaults to legacy DirectWrite (no behaviour
        // flip); post-sync hook is off by default with the 7-Zip example
        // arguments pre-filled for convenience.
        var s = new AppSettings();
        Assert.Equal(FilePublishMode.DirectWrite, s.PublishMode);
        Assert.False(s.PostSyncEnabled);
        Assert.Equal(1440, s.PostSyncIntervalMinutes);
        Assert.Equal(60, s.PostSyncTimeoutMinutes);
        Assert.True(s.PostSyncLowPriority);
        Assert.Contains("{backup}", s.PostSyncArguments);
        Assert.Contains("{dest}",   s.PostSyncArguments);
    }

    [Fact]
    public void OldSettings_WithRemovedMonitorKeys_LoadIgnoresThem()
    {
        // a pre-2.5 settings.json still carries Monitor* keys.  They
        // must be silently ignored (the properties no longer exist) without
        // throwing, and the surviving fields must load normally.
        string oldJson = """
            {
              "DestinationRoot": "\\\\SRV\\Backup",
              "MonitoringEnabled": true,
              "MonitorIntervalHours": 6,
              "MonitorPassword": true,
              "PasswordMaxAgeDays": 90,
              "MonitorFirewall": true,
              "SettingsVersion": 11
            }
            """;
        var opts = new JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var loaded = JsonSerializer.Deserialize<AppSettings>(oldJson, opts);
        Assert.NotNull(loaded);
        Assert.Equal(@"\\SRV\Backup", loaded!.DestinationRoot);
        Assert.Equal(FilePublishMode.DirectWrite, loaded.PublishMode); // default
        Assert.False(loaded.PostSyncEnabled);                          // default
        Assert.Equal(11, loaded.SettingsVersion);                      // pre-migration
    }

    [Fact]
    public void V250_RoundTripJson_PreservesNewFields()
    {
        var original = new AppSettings
        {
            PublishMode             = FilePublishMode.TempThenRename,
            PostSyncEnabled         = true,
            PostSyncExePath         = @"C:\Program Files\7-Zip\7z.exe",
            PostSyncArguments       = "a -t7z \"{backup}\\x.7z\" \"{dest}\\*\"",
            PostSyncWorkingDir      = @"C:\tmp",
            PostSyncIntervalMinutes = 720,
            PostSyncTimeoutMinutes  = 30,
            PostSyncLowPriority     = false,
            SettingsVersion         = 12,
        };
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters    = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        string json = JsonSerializer.Serialize(original, opts);
        var loaded  = JsonSerializer.Deserialize<AppSettings>(json, opts);

        Assert.NotNull(loaded);
        Assert.Equal(FilePublishMode.TempThenRename, loaded!.PublishMode);
        Assert.Equal(original.PostSyncEnabled,         loaded.PostSyncEnabled);
        Assert.Equal(original.PostSyncExePath,         loaded.PostSyncExePath);
        Assert.Equal(original.PostSyncArguments,       loaded.PostSyncArguments);
        Assert.Equal(original.PostSyncWorkingDir,      loaded.PostSyncWorkingDir);
        Assert.Equal(original.PostSyncIntervalMinutes, loaded.PostSyncIntervalMinutes);
        Assert.Equal(original.PostSyncTimeoutMinutes,  loaded.PostSyncTimeoutMinutes);
        Assert.Equal(original.PostSyncLowPriority,     loaded.PostSyncLowPriority);
        Assert.Equal(12,                                loaded.SettingsVersion);
    }

    [Fact]
    public void DefaultAppSettings_MirrorLogsIsFalse()
    {
        // new field, MUST default to opt-in (false)
        var s = new AppSettings();
        Assert.False(s.MirrorLogs);
    }

    [Fact]
    public void DefaultAppSettings_V2412_NewFieldsHaveCorrectDefaults()
    {
        // verify new fields land with safe defaults:
        //   • RegistryBackupIntervalMinutes: 30 days (43200 min) — much less
        //     server traffic than the old "snapshot every reconcile"
        //   • StatsWindowEnabled: true — adds menu item, no work until opened
        //   • StatsRefreshIntervalMs: 1000 — humane refresh rate
        var s = new AppSettings();
        Assert.Equal(43_200, s.RegistryBackupIntervalMinutes);
        Assert.True(s.StatsWindowEnabled);
        Assert.Equal(1000, s.StatsRefreshIntervalMs);
        Assert.True(s.StartMinimizedToTray); // documented default — was previously hardcoded in UI
    }

    [Fact]
    public void DefaultAppSettings_PreservesSafetyDefaults()
    {
        var s = new AppSettings();
        // Spot-check that didn't accidentally regress important defaults.
        Assert.True(s.MirrorDesktop);
        Assert.True(s.MirrorDocuments);
        Assert.True(s.MirrorDownloads);
        Assert.False(s.MirrorPictures);
        Assert.False(s.MirrorAppDataRoaming);
        Assert.True(s.ResumeEnabled);
        Assert.True(s.LowerIoPriority);
        Assert.Equal(1_000_000, s.MaxBandwidthBitsPerSecond);
        Assert.Equal(3, s.TurboFirstRunBandwidthMbps);   // retuned 10→3 for 10-PC/30-Mbit
        Assert.Equal(1440, s.ReconcileIntervalMinutes);
    }

    [Fact]
    public void Settings_RoundTripJson_PreservesAllFields()
    {
        var original = new AppSettings
        {
            DestinationRoot           = @"Z:\Backup",
            MaxBandwidthBitsPerSecond = 5_000_000,
            LogLevel                  = AppLogLevel.Warning,
            MirrorLogs                = true,         // new field
            RetryCount                = 7,
            SkipStartupReconcileIfWithinMinutes = 10,
            ResumeEnabled             = false,
            ResumeMinFileSizeBytes    = 100L * 1024 * 1024,
            ResumeSidecarMaxAgeDays   = 14,
            SettingsVersion           = 10,
        };

        // Use the same options as SettingsStore — string enums + indented.
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters    = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        string json = JsonSerializer.Serialize(original, opts);
        var loaded  = JsonSerializer.Deserialize<AppSettings>(json, opts);

        Assert.NotNull(loaded);
        Assert.Equal(original.DestinationRoot,           loaded!.DestinationRoot);
        Assert.Equal(original.MaxBandwidthBitsPerSecond, loaded.MaxBandwidthBitsPerSecond);
        Assert.Equal(original.LogLevel,                  loaded.LogLevel);
        Assert.Equal(original.MirrorLogs,                loaded.MirrorLogs);
        Assert.Equal(original.RetryCount,                loaded.RetryCount);
        Assert.Equal(original.SkipStartupReconcileIfWithinMinutes, loaded.SkipStartupReconcileIfWithinMinutes);
        Assert.Equal(original.ResumeEnabled,             loaded.ResumeEnabled);
        Assert.Equal(original.ResumeMinFileSizeBytes,    loaded.ResumeMinFileSizeBytes);
        Assert.Equal(original.ResumeSidecarMaxAgeDays,   loaded.ResumeSidecarMaxAgeDays);
        Assert.Equal(original.SettingsVersion,           loaded.SettingsVersion);
    }

    [Fact]
    public void OldSettings_MissingNewFields_LoadsWithDefaults()
    {
        // Simulate a v9 settings.json that doesn't have MirrorLogs at all.
        // JSON deserialization should populate defaults from property initializers.
        string oldJson = """
            {
              "DestinationRoot": "\\\\SRV\\Backup",
              "MaxBandwidthBitsPerSecond": 1000000,
              "SettingsVersion": 9
            }
            """;
        var opts = new JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var loaded = JsonSerializer.Deserialize<AppSettings>(oldJson, opts);
        Assert.NotNull(loaded);
        Assert.Equal(@"\\SRV\Backup", loaded!.DestinationRoot);
        Assert.False(loaded.MirrorLogs); // default
        Assert.True(loaded.ResumeEnabled); // default
        Assert.Equal(5, loaded.RetryCount); // default
        Assert.Equal(9, loaded.SettingsVersion); // unchanged until migration runs
    }

    [Fact]
    public void V2412_RoundTripJson_PreservesNewFields()
    {
        // Pin the new fields' JSON behaviour.  Regression guard
        // for the original-review §2.2 "downgrade safe" concern: known
        // fields must survive a round-trip identity-equal.
        var original = new AppSettings
        {
            RegistryBackupIntervalMinutes = 7 * 1440,  // 7 days
            StatsWindowEnabled            = false,
            StatsRefreshIntervalMs        = 2500,
            StartMinimizedToTray          = false,
            SettingsVersion               = 11,
        };

        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters    = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        string json = JsonSerializer.Serialize(original, opts);
        var loaded  = JsonSerializer.Deserialize<AppSettings>(json, opts);

        Assert.NotNull(loaded);
        Assert.Equal(original.RegistryBackupIntervalMinutes, loaded!.RegistryBackupIntervalMinutes);
        Assert.Equal(original.StatsWindowEnabled,            loaded.StatsWindowEnabled);
        Assert.Equal(original.StatsRefreshIntervalMs,        loaded.StatsRefreshIntervalMs);
        Assert.Equal(original.StartMinimizedToTray,          loaded.StartMinimizedToTray);
        Assert.Equal(original.SettingsVersion,               loaded.SettingsVersion);
    }

    [Fact]
    public void V10Settings_LoadingOlder_GetsNewFieldsAsDefaults()
    {
        // A v10 settings file  doesn't have the fields at all.
        // Loading it in should populate them from property initializers.
        string v10Json = """
            {
              "DestinationRoot": "\\\\SRV\\Backup",
              "MirrorLogs": true,
              "SettingsVersion": 10
            }
            """;
        var opts = new JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var loaded = JsonSerializer.Deserialize<AppSettings>(v10Json, opts);
        Assert.NotNull(loaded);
        // Pre-existing v10 field preserved
        Assert.True(loaded!.MirrorLogs);
        // New fields fall back to property-initializer defaults
        Assert.Equal(43_200, loaded.RegistryBackupIntervalMinutes);
        Assert.True(loaded.StatsWindowEnabled);
        Assert.Equal(1000, loaded.StatsRefreshIntervalMs);
        // SettingsVersion stays at 10 until MigrateIfNeeded runs in SettingsStore.Load
        Assert.Equal(10, loaded.SettingsVersion);
    }
}
