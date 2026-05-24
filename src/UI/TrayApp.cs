using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ProfileMirrorSync.Models;
using ProfileMirrorSync.Services;

namespace ProfileMirrorSync.UI;

/// <summary>
/// System-tray host.
/// Thread-safety: all _controller access is marshalled to the UI thread via PostToUi().
/// Handles power events (sleep/wake) and session events (logoff/shutdown).
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly SettingsStore _store;
    private AppSettings            _settings;
    private readonly Logger        _log;

    private SyncController? _controller;
    private readonly Form    _uiProxy;   // hidden marshaling form

    private readonly NotifyIcon        _tray;
    private readonly ContextMenuStrip  _menu;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _statusItem;

    private LogViewerForm? _logViewer;
    private StatsForm?     _statsForm;     //
    private string          _lastSyncedFile = "";

    // shared persistent state (last-reconcile + early-reconcile flag).
    // the security-monitor subsystem was removed entirely.
    private readonly PersistentStateStore _stateStore;

    public TrayApp(Logger log)
    {
        // Hidden proxy form — its HWND is used for marshaling via BeginInvoke/Invoke
        _uiProxy = new Form
        {
            Text          = "ProfileMirrorSync_UIProxy",
            Visible       = false,
            ShowInTaskbar = false,
            WindowState   = FormWindowState.Minimized,
            Size          = new Size(1, 1),
        };
        _uiProxy.CreateControl();

        _store    = new SettingsStore();
        _settings = _store.Load();
        _log      = log;
        _log.SetLevel(_settings.LogLevel);
        _log.SetTraceMode(_settings.EnableTraceMode);
        _store.Log = _log;  // allow SettingsStore to log parse/save errors

        // shared persistent state used by SyncController (reconcile timestamps)
        _stateStore = new PersistentStateStore(_log);

        // Read version from Assembly attribute (was hardcoded "v2.4.12";
        // operations team couldn't tell PMS versions apart from logs after the
        // bump because the string was never refreshed).  Audit D-1.
        string asmVersion = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString() ?? "?";
        _log.Info($"=== ProfileMirrorSync v{asmVersion} TrayApp ctor ===");
        try { LogSystemInfo(); } catch (Exception ex) { _log.Warn($"LogSystemInfo: {ex.Message}"); }

        // ── Context menu ───────────────────────────────────────────────────────
        _statusItem      = new ToolStripMenuItem("● Остановлено") { Enabled = false };
        _statusItem.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _startItem       = new ToolStripMenuItem("▶  Запустить",  null, OnStart);
        _stopItem        = new ToolStripMenuItem("■  Остановить", null, OnStop) { Enabled = false };

        _menu = new ContextMenuStrip();
        // "Статистика…" опционально, по StatsWindowEnabled.
        var menuItems = new List<ToolStripItem>
        {
            _statusItem, new ToolStripSeparator(),
            _startItem, _stopItem, new ToolStripSeparator(),
            new ToolStripMenuItem("📋  Журнал",                    null, OnOpenLog),
        };
        if (_settings.StatsWindowEnabled)
            menuItems.Add(new ToolStripMenuItem("📊  Статистика…",  null, OnOpenStats));
        menuItems.AddRange(new ToolStripItem[]
        {
            new ToolStripMenuItem("⚙   Параметры…",               null, OnOpenSettings),
            new ToolStripMenuItem("📂  Открыть папку назначения",  null, OnOpenDestination),
            new ToolStripSeparator(),
            new ToolStripMenuItem("✕  Выход",                     null, OnExit),
        });
        _menu.Items.AddRange(menuItems.ToArray());

        // ── Tray icon ─────────────────────────────────────────────────────────
        _tray = new NotifyIcon
        {
            Icon             = BuildIcon(running: false),
            Text             = "ProfileMirrorSync — остановлено",
            ContextMenuStrip = _menu,
            Visible          = true,
        };
        _tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OnOpenLog(null, EventArgs.Empty);
        };

        // ── System power + session events ─────────────────────────────────────
        SystemEvents.PowerModeChanged  += OnPowerModeChanged;
        SystemEvents.SessionSwitch     += OnSessionSwitch;
        SystemEvents.SessionEnding     += OnSessionEnding;

        // ── Old log cleanup ───────────────────────────────────────────────────
        _ = Task.Run(() => CleanOldLogs());

        _log.Info("TrayApp ctor завершён.");

        if (_settings.SyncOnStartup)
        {
            _log.Info("SyncOnStartup=true → запускаю...");
            BeginStart();
        }
    }

    // ── Start / Stop ──────────────────────────────────────────────────────────

    private void BeginStart() => _ = Task.Run(DoStartAsync);

    // Lifecycle semaphore that serialises ALL paths that call
    // DoStartAsync / DoStopAsync.  Before this, six different sites used
    // fire-and-forget `_ = Task.Run(DoStartAsync)` which could overlap (e.g.
    // OnStart from menu, OnOpenSettings-restart, BeginStart from ctor, wake-
    // from-sleep, SessionEnding).  Two overlapping DoStartAsync calls would
    // race on _controller assignment and leak the loser.  The semaphore
    // serialises every entry into the lifecycle: callers either acquire and
    // do their work, or wait their turn.  All five lifecycle sites below
    // route through Do*Locked wrappers.
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private async Task DoStartAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try { await DoStartAsyncLocked().ConfigureAwait(false); }
        finally { _lifecycleLock.Release(); }
    }

    private async Task DoStartAsyncLocked()
    {
        _log.Info("DoStartAsync начало");
        // Idempotent: if a controller is already running (left over
        // from a parallel Start that beat us through the lock), do nothing.
        if (_controller is { IsRunning: true })
        {
            _log.Debug("DoStartAsync: контроллер уже запущен — пропускаем.");
            return;
        }
        SyncController ctrl;
        try
        {
            ctrl = new SyncController(_settings, _log, _stateStore);
            ctrl.FileProcessed += path =>
            {
                _lastSyncedFile = Path.GetFileName(path);
                PostToUi(UpdateStatus);
            };
            await ctrl.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка запуска SyncController", ex);
            PostToUi(() =>
            {
                try { _tray.ShowBalloonTip(4000, "ProfileMirrorSync", $"Ошибка запуска: {ex.Message}", ToolTipIcon.Error); } catch { }
                UpdateStatus();
            });
            return;
        }

        PostToUi(() => { _controller = ctrl; UpdateStatus(); });
        _log.Info("Синхронизация активна.");
    }

    private async Task DoStopAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try { await DoStopAsyncLocked().ConfigureAwait(false); }
        finally { _lifecycleLock.Release(); }
    }

    private async Task DoStopAsyncLocked()
    {
        _log.Info("DoStopAsync начало");

        // TCS guarantees the UI-thread action completes before we dispose the controller.
        // (Task.Delay(80) race replaced)
        var tcs = new TaskCompletionSource<SyncController?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PostToUi(() => { tcs.TrySetResult(_controller); _controller = null; });

        var ctrl = await tcs.Task.ConfigureAwait(false);
        if (ctrl is not null)
        {
            await ctrl.StopAsync().ConfigureAwait(false);
            ctrl.Dispose();
        }

        _lastSyncedFile = "";
        PostToUi(UpdateStatus);
    }

    /// <summary>
    /// Atomic stop-then-start, used by OnOpenSettings when settings
    /// change while the controller is running.  Holds the lifecycle lock for
    /// the entire pair so an intervening OnStart/OnStop from the menu can't
    /// slip between Stop and Start.
    /// </summary>
    private async Task DoRestartAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DoStopAsyncLocked().ConfigureAwait(false);
            await DoStartAsyncLocked().ConfigureAwait(false);
        }
        finally { _lifecycleLock.Release(); }
    }

    // ── Menu handlers ─────────────────────────────────────────────────────────

    private void OnStart(object? sender, EventArgs e)
    {
        if (_controller is { IsRunning: true }) return;
        // if a controller exists in a transient state (Starting/
        // Stopping/Stopped-but-not-yet-disposed), route through DoRestartAsync
        // so it's properly stopped under the lifecycle lock before we Start.
        // Bare Dispose() left ports/workers/watchers in indeterminate states
        // and could leak the new controller's resources if Start raced.
        if (_controller is not null)
        {
            _ = Task.Run(DoRestartAsync);
            return;
        }
        UpdateStatus();
        BeginStart();
    }

    private void OnStop(object? sender, EventArgs e) => _ = Task.Run(DoStopAsync);

    private void OnOpenLog(object? sender, EventArgs e)
    {
        if (_logViewer is { IsDisposed: false }) { _logViewer.BringToFront(); _logViewer.Activate(); return; }
        _logViewer = new LogViewerForm(_log);
        _logViewer.FormClosed += (_, _) => _logViewer = null;
        _logViewer.Show();
    }

    // Stats window: same singleton-form pattern as the log viewer.
    // The form polls SyncController.GetStatsSnapshot() on a UI timer; when
    // closed the timer is disposed and no further cost is incurred.
    //
    // Pass a GETTER (Func<SyncController?>) instead of a direct
    // controller reference.  Previously the form held the controller that
    // existed at the moment "Статистика…" was clicked; after a Stop/Start
    // cycle (or settings change → DoRestartAsync) the old controller was
    // Disposed and a NEW one was created — but the form kept polling the
    // OLD one, which stayed in StateStopped with stale counters.  User
    // visible: stats window "freezes" after Stop and never wakes up after
    // Start.  Getter pattern lets the form pick up the live controller on
    // every tick at zero extra cost.
    private void OnOpenStats(object? sender, EventArgs e)
    {
        if (_controller is null)
        {
            MessageBox.Show("Контроллер ещё не инициализирован — попробуйте позже.",
                "Статистика", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_statsForm is { IsDisposed: false }) { _statsForm.BringToFront(); _statsForm.Activate(); return; }
        _statsForm = new StatsForm(() => _controller, _settings);
        _statsForm.FormClosed += (_, _) => _statsForm = null;
        _statsForm.Show();
    }

    private void OnOpenSettings(object? sender, EventArgs e)
    {
        bool wasRunning = _controller is { IsRunning: true };
        using var form  = new SettingsForm(_settings);
        if (form.ShowDialog() != DialogResult.OK) return;

        _settings = form.Result;
        _store.Save(_settings);
        _log.SetLevel(_settings.LogLevel);
        _log.SetTraceMode(_settings.EnableTraceMode);
        _log.Info("Настройки сохранены.");

        if (wasRunning)
            _ = Task.Run(DoRestartAsync);
    }

    private void OnOpenDestination(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_settings.DestinationRoot))
        {
            MessageBox.Show("Папка назначения не задана.", "ProfileMirrorSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        OpenExplorer(_settings.DestinationRoot);
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _ = Task.Run(async () =>
        {
            _log.Info("Выход запрошен пользователем.");
            await DoStopAsync().ConfigureAwait(false);
            PostToUi(() => { _tray.Visible = false; Application.Exit(); });
        });
    }

    // ── Power & session events ────────────────────────────────────────────────

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            if (!_settings.ReconcileOnWake)
            {
                _log.Info("PowerMode: Resume — реконсиляция после пробуждения отключена в параметрах.");
                return;
            }
            _log.Info($"PowerMode: Resume — планирую реконсиляцию после пробуждения.");
            PostToUi(() => _controller?.TriggerResumeReconcile());
        }
        else if (e.Mode == PowerModes.Suspend)
        {
            _log.Info("PowerMode: Suspend — система уходит в сон.");
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // Unlock/logon reconcile is now opt-in (default off).  FSW keeps
        // running while the screen is merely locked, so files do not diverge and
        // a reconcile here is usually redundant frequent reconciliation.
        bool trigger =
            (e.Reason == SessionSwitchReason.SessionUnlock && _settings.ReconcileOnUnlock) ||
            (e.Reason == SessionSwitchReason.SessionLogon  && _settings.ReconcileOnLogon);
        if (!trigger) return;

        _log.Info($"Session: {e.Reason} — планирую реконсиляцию (включено в параметрах).");
        PostToUi(() => _controller?.TriggerResumeReconcile());
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        _log.Info($"SessionEnding: {e.Reason} — корректное завершение...");
        // No-UI-marshal stop path for shutdown/logoff.
        //
        // The classic implementation called Task.Run(DoStopAsync).Wait(),
        // which deadlocked through's lock + PostToUi chain:
        //   UI thread → .Wait()  ──blocks UI──┐
        //   bg Task → DoStopAsyncLocked       │
        //       └── PostToUi(...)             │
        //       └── await tcs.Task ←──────────┘  (UI never pumps to set TCS)
        // 8 s timeout would fire but Stop would not actually complete.
        //
        // Fix: read _controller DIRECTLY on the UI thread (no PostToUi needed
        // — we ARE the UI thread), then await StopAsync on a background task.
        // StopAsync itself does no UI marshalling, so blocking the UI thread
        // on it via .Wait() is safe.  This bypasses the lifecycle semaphore
        // because shutdown is exceptional: any concurrent Start/Stop is
        // pointless when the process is dying anyway.
        var ctrl = _controller;
        _controller = null;

        if (ctrl is null)
        {
            _log.Info("SessionEnding: контроллер не запущен.");
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool completed;
        try
        {
            completed = Task.Run(async () =>
            {
                try { await ctrl.StopAsync().ConfigureAwait(false); }
                finally { try { ctrl.Dispose(); } catch { } }
            }).Wait(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            _log.Warn($"SessionEnding: исключение в StopAsync: {ex.Message}", ex);
            completed = false;
        }
        if (completed)
            _log.Info($"SessionEnding: завершено за {sw.ElapsedMilliseconds} мс.");
        else
            _log.Warn($"SessionEnding: StopAsync не завершился за 8 с (прошло {sw.ElapsedMilliseconds} мс), выход принудительный.");
    }

    // ── Log cleanup ───────────────────────────────────────────────────────────

    private void CleanOldLogs()
    {
        if (_settings.LogRetentionDays <= 0) return;
        try
        {
            var cutoff = DateTime.Today.AddDays(-_settings.LogRetentionDays);
            foreach (string f in Directory.EnumerateFiles(_store.LogsDirectory, "pms-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(f) < cutoff)
                    {
                        File.Delete(f);
                        _log.Info($"Удалён старый лог: {Path.GetFileName(f)}");
                    }
                }
                catch { }
            }
        }
        catch (Exception ex) { _log.Warn($"CleanOldLogs: {ex.Message}"); }
    }

    // ── Status ────────────────────────────────────────────────────────────────

    private void UpdateStatus()
    {
        bool running = _controller is { IsRunning: true };
        _startItem.Enabled = !running;
        _stopItem.Enabled  =  running;

        if (running)
        {
            string dest = _settings.DestinationRoot ?? "(не задано)";
            double mbps = _settings.MaxBandwidthBitsPerSecond / 1_000_000.0;
            _statusItem.Text = $"● Работает  →  {ShortPath(dest)}";
            var oldIcon = _tray.Icon;
            _tray.Icon = BuildIcon(running: true);
            oldIcon?.Dispose();
            string tip = string.IsNullOrEmpty(_lastSyncedFile)
                ? $"ProfileMirrorSync  [{mbps:F1} Мбит/с]"
                : $"↑ {_lastSyncedFile}  [{mbps:F1} Мбит/с]";
            SetTrayTip(tip);
        }
        else
        {
            _statusItem.Text = "● Остановлено";
            var oldIcon = _tray.Icon;
            _tray.Icon = BuildIcon(running: false);
            oldIcon?.Dispose();
            SetTrayTip("ProfileMirrorSync — остановлено");
        }
    }

    private void SetTrayTip(string tip) { try { _tray.Text = tip.Length > 63 ? tip[..60] + "…" : tip; } catch { } }
    private static string ShortPath(string p) => p.Length > 38 ? "…" + p[^35..] : p;

    // ── GDI icon builder ──────────────────────────────────────────────────────

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Icon BuildIcon(bool running)
    {
        using var bmp = new Bitmap(16, 16);
        using var g   = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        Color fill = running ? Color.FromArgb(0, 175, 75) : Color.FromArgb(130, 130, 130);
        using (var br = new SolidBrush(fill)) g.FillEllipse(br, 1, 1, 13, 13);
        using var wb = new SolidBrush(Color.White);
        if (running)
        {
            var pts = new[] { new Point(8,2),new Point(13,8),new Point(10,8),new Point(10,13),new Point(6,13),new Point(6,8),new Point(3,8) };
            g.FillPolygon(wb, pts);
        }
        else { g.FillRectangle(wb, 4, 4, 3, 8); g.FillRectangle(wb, 9, 4, 3, 8); }
        IntPtr hicon = bmp.GetHicon();
        Icon icon = (Icon)Icon.FromHandle(hicon).Clone();
        DestroyIcon(hicon);
        return icon;
    }

    // ── Thread marshaling ─────────────────────────────────────────────────────

    private void PostToUi(Action action)
    {
        // When _uiProxy is already disposed (we're
        // in the middle of shutdown), silently drop the action.  Previously
        // we called action() directly on the current (possibly non-UI)
        // thread, which would throw InvalidOperationException ("Cross-thread
        // operation") for any action that touched WinForms controls — and
        // the catch below would just log it as a Warn.  At shutdown we don't
        // care about UI updates anyway; let them die quietly.
        if (_uiProxy.IsDisposed) return;
        try { if (_uiProxy.InvokeRequired) _uiProxy.BeginInvoke(action); else action(); }
        catch (ObjectDisposedException) { /* race with Dispose — ignore */ }
        catch (InvalidOperationException) { /* form already closed — ignore */ }
        catch (Exception ex) { _log.Warn($"PostToUi: {ex.Message}"); }
    }

    private void OpenExplorer(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); }
        catch (Exception ex) { _log.Warn($"OpenExplorer '{path}': {ex.Message}"); }
    }

    private void LogSystemInfo()
    {
        _log.Info($"OS: {Environment.OSVersion}  .NET: {Environment.Version}");
        _log.Info($"Machine: {Environment.MachineName}  User: {Environment.UserName}");
        _log.Info($"Dest: {_settings.DestinationRoot}  BW: {_settings.MaxBandwidthBitsPerSecond/1_000_000.0:F1} Мбит/с");
        _log.Info($"Reconcile: interval={_settings.ReconcileIntervalMinutes}мин delay={_settings.ReconcileFileDelayMs}мс batch={_settings.ReconcileBatchSize}×{_settings.ReconcileBatchPauseMs}мс");
        _log.Info($"Turbo: enabled={_settings.TurboFirstRunEnabled} threshold={_settings.TurboThresholdFiles} speed={_settings.TurboFirstRunBandwidthMbps}Мбит/с");
        // comprehensive one-line dump for diff against SettingsStore
        // before/after-save trace.  Same format as SettingsStore.SummariseSettings.
        _log.Info($"Settings (runtime): {SettingsStore.Summarise(_settings)}");
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch    -= OnSessionSwitch;
            SystemEvents.SessionEnding    -= OnSessionEnding;

            // Close child windows so their UI timers stop and any
            // pending Invokes don't fire on a half-disposed tray.
            try { _statsForm?.Close(); _statsForm?.Dispose(); } catch { }
            try { _logViewer?.Close(); _logViewer?.Dispose(); } catch { }

            _tray.Visible = false;
            _tray.Icon?.Dispose();
            _tray.Dispose();
            _menu.Dispose();
            _controller?.Dispose();
            _uiProxy.Dispose();
            _lifecycleLock.Dispose(); //
            // _log owned by Program.cs
        }
        base.Dispose(disposing);
    }
}
