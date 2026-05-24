using System.Reflection;
using ProfileMirrorSync.Services;
using ProfileMirrorSync.UI;

// ── Global crash handler ──────────────────────────────────────────────────────
// Catch any unhandled exception so we can write it to the log before dying.
// Logs live in the per-user data dir (AppPaths.LogsDirectory).
AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
{
    string crashLog = Path.Combine(AppPaths.LogsDirectory, "crash.log");
    try
    {
        File.AppendAllText(crashLog,
            $"\r\n=== CRASH {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n{ev.ExceptionObject}\r\n");
    }
    catch { }
};

Application.ThreadException += (_, ev) =>
{
    string crashLog = Path.Combine(AppPaths.LogsDirectory, "crash.log");
    try
    {
        File.AppendAllText(crashLog,
            $"\r\n=== UI THREAD EXCEPTION {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n{ev.Exception}\r\n");
    }
    catch { }

    MessageBox.Show($"Необработанное исключение UI:\r\n{ev.Exception.Message}",
        "ProfileMirrorSync — Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
};
Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

// ── DPI + visual styles ───────────────────────────────────────────────────────
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

// ── Single-instance guard ─────────────────────────────────────────────────────
// `Local\` (per-session) instead of `Global\`.  PMS is a
// per-user tray app; on Remote Desktop / Terminal Server / VDI hosts, each
// interactive session must be able to launch its own PMS instance for its own
// user profile.  `Global\` makes the mutex visible to ALL sessions on the
// machine, so only ONE session would win and others would see "already running"
// even though they have separate profiles.  `Local\` scopes the mutex to
// the current session.
const string MutexName = "Local\\ProfileMirrorSync_SingleInstance";
using var mutex = new Mutex(true, MutexName, out bool createdNew);
if (!createdNew)
{
    MessageBox.Show("ProfileMirrorSync уже запущен.", "ProfileMirrorSync",
        MessageBoxButtons.OK, MessageBoxIcon.Information);
    return;
}

// ── Bootstrap logger ──────────────────────────────────────────────────────────
// Logs live with the per-user settings under %LocalAppData%\ProfileMirrorSync
// (AppPaths.LogsDirectory), so logging works wherever the exe is installed.
string logsDir = AppPaths.LogsDirectory;
Directory.CreateDirectory(logsDir);
using var startLog = new Logger(logsDir, ProfileMirrorSync.Models.AppLogLevel.Debug);

string exePath  = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ProfileMirrorSync.exe");
string exeDir   = Path.GetDirectoryName(exePath) ?? ".";
string version  = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
startLog.Info($"=== ProfileMirrorSync v{version} start ===");
startLog.Info($"exe:      {exePath}");
startLog.Info($"exeDir:   {exeDir}");
startLog.Info($"logs:     {logsDir}");
startLog.Info($"data:     {AppPaths.DataDirectory}");
startLog.Info($"user:     {Environment.UserName}  machine: {Environment.MachineName}");
startLog.Info($"os:       {Environment.OSVersion}");
startLog.Info($"dotnet:   {Environment.Version}");

// Multi-user: the program runs from a SHARED exe location (any path,
// e.g. %ProgramFiles%), but each Windows user gets their OWN settings, sync
// state and logs under %LocalAppData%\ProfileMirrorSync.

// ── Install WinForms SynchronizationContext BEFORE constructing TrayApp ───────
// Application.Run() installs it internally, but TrayApp() is evaluated as an
// argument BEFORE Run() starts. Installing explicitly here prevents the crash.
if (SynchronizationContext.Current is null)
{
    startLog.Info("SynchronizationContext.Current is null — installing WindowsFormsSynchronizationContext");
    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
}
startLog.Info($"SyncContext: {SynchronizationContext.Current?.GetType().Name ?? "none"}");

// ── Run ───────────────────────────────────────────────────────────────────────
// Pass startLog into TrayApp so there is only ONE Logger instance writing to the
// log file at any time. Previously TrayApp created a second Logger independently,
// resulting in two concurrent StreamWriters on the same file.
startLog.Info("Создаю TrayApp и запускаю Application.Run...");
try
{
    Application.Run(new TrayApp(startLog));
}
catch (Exception ex)
{
    startLog.Error("Критическая ошибка Application.Run", ex);
    MessageBox.Show($"Критическая ошибка при запуске:\r\n{ex.Message}\r\n\r\nПодробности в:\r\n{logsDir}",
        "ProfileMirrorSync — Критическая ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
}

startLog.Info("Application.Run завершён — выход.");
