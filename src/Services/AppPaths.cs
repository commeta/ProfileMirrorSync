namespace ProfileMirrorSync.Services;

/// <summary>
/// Single source of truth for where the program keeps its files.
///
/// Multi-user model: per-user data — settings.json, monitor_state.json,
/// resume\, and Logs\ — all live under %LocalAppData%\ProfileMirrorSync
/// (i.e. C:\Users\&lt;user&gt;\AppData\Local\…). Each Windows user therefore gets
/// their OWN settings, sync state and logs, even though every user runs the
/// SAME shared exe (which can sit in a read-only location such as
/// %ProgramFiles%). This location is always writable by the owning user
/// without administrator rights.
///
/// Older builds kept everything in the SHARED %ProgramData%\ProfileMirrorSync —
/// see SettingsStore for the one-time migration that copies an existing shared
/// settings.json into the per-user location on first run.
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "ProfileMirrorSync";

    /// <summary>Per-user data root: %LocalAppData%\ProfileMirrorSync.</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    /// <summary>
    /// Legacy shared root (%ProgramData%\ProfileMirrorSync) — read only, used by
    /// the one-time settings migration and to keep self-exclude paths correct.
    /// </summary>
    public static string LegacySharedDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        AppFolderName);

    /// <summary>
    /// Logs directory: %LocalAppData%\ProfileMirrorSync\Logs — the same per-user
    /// root as the settings, so logging works regardless of where the exe is
    /// installed (including read-only %ProgramFiles%).
    /// </summary>
    public static string LogsDirectory { get; } = Path.Combine(DataDirectory, "Logs");
}
