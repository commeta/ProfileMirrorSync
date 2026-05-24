using ProfileMirrorSync.Models;
using ProfileMirrorSync.Services;

namespace ProfileMirrorSync.UI;

/// <summary>
/// Real-time statistics window.
///
/// Polls <see cref="SyncController.GetStatsSnapshot"/> on a UI timer.  The
/// snapshot is a record of primitive values produced without any I/O, so
/// the only cost on the application side is the timer tick + Label.Text
/// updates.  When the window is closed the timer is disposed and there is
/// zero ongoing cost — the controller doesn't even know the window existed.
///
/// changes:
///   • Controller passed as a GETTER (<see cref="Func{T}"/>) instead of a
///     direct reference.  Fixes user-reported "stats window freezes after
///     Stop, never wakes after Start" — DoStopAsync disposes the controller
///     and DoStartAsync creates a brand-new one, but the form previously
///     kept polling the disposed instance.  The getter pulls the LIVE
///     controller from TrayApp on every tick.
///   • Close button click handler added explicitly.  On a modeless form
///     (Show()), DialogResult=Cancel + CancelButton do NOT trigger Close();
///     those properties only auto-close in modal dialogs (ShowDialog).
///   • Scroll container fixed: layout now mirrors SettingsForm — outer
///     Panel{Dock=Fill, AutoScroll=true} wraps an inner TableLayoutPanel
///     {Dock=Top, AutoSize=true}.  TableLayoutPanel.AutoScroll is unreliable
///     when Dock=Fill on a form with another Dock=Bottom sibling; the wrap-
///     in-Panel pattern was already proven on SettingsForm tabs.
/// </summary>
public sealed class StatsForm : Form
{
    private readonly Func<SyncController?> _controllerGetter;
    private readonly AppSettings           _settings;
    private readonly System.Windows.Forms.Timer _timer;

    // Display labels — one per metric.  Updated each tick.
    private readonly Label _lblStatus;
    private readonly Label _lblWatchedRoots;
    private readonly Label _lblQueueDepth;
    private readonly Label _lblHighWaterMark;
    private readonly Label _lblFilesCopied;
    private readonly Label _lblFilesDeleted;
    private readonly Label _lblBytesCopied;
    private readonly Label _lblErrors;
    private readonly Label _lblBandwidth;
    private readonly Label _lblTurbo;
    private readonly Label _lblWorkingSet;
    private readonly Label _lblPrivateMemory;
    private readonly Label _lblNextReconcile;
    private readonly Label _lblLastReconcileStart;
    private readonly Label _lblLastReconcileEnd;
    private readonly Label _lblLastRegistrySnapshot;
    private readonly Label _lblLastLogMirror;

    public StatsForm(Func<SyncController?> controllerGetter, AppSettings settings)
    {
        _controllerGetter = controllerGetter;
        _settings         = settings;

        Text          = "Статистика ProfileMirrorSync";
        Width         = 520;
        // Bumped from 540 → 600 so all 17 metric rows + 4 section
        // headers + close-button bar fit without scrolling on the default
        // open size at 96 DPI.  Below the new MinimumSize the AutoScroll
        // panel below kicks in correctly.
        Height        = 600;
        MinimumSize   = new Size(440, 460);
        StartPosition = FormStartPosition.CenterScreen;
        Font          = new Font("Segoe UI", 9f);
        ShowInTaskbar = true;

        // Esc closes the window.
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        // ── Layout: scrollable Panel (Dock=Fill) + button bar (Dock=Bottom) ──
        //
        // WinForms docks in REVERSE Z-order: the control
        // added LAST is docked FIRST and gets its edge first.  For the Bottom
        // bar to reserve its 44 px and the Fill panel to occupy only the
        // REMAINDER (so its AutoScroll range is correct and the last rows are
        // reachable), the Fill panel must be added FIRST and the Bottom bar
        // LAST.  The previous order (bar first, scroll last) made the scroll
        // panel fill the WHOLE client area and the bar overlap its bottom
        // 44 px — hiding the last metric rows and truncating the scroll range,
        // which is exactly the "bottom fields half-visible, won't scroll to
        // the end" symptom reported on the previous patch.
        var scroll = new Panel
        {
            Dock        = DockStyle.Fill,
            AutoScroll  = true,
            BorderStyle = BorderStyle.None,
        };
        scroll.HorizontalScroll.Enabled = false;
        scroll.HorizontalScroll.Visible = false;

        var table = new TableLayoutPanel
        {
            Dock         = DockStyle.Top,
            ColumnCount  = 2,
            // Extra bottom padding so the final row clears the AutoScroll edge.
            Padding      = new Padding(14, 14, 14, 24),
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _lblStatus               = AddRow(table, "Состояние:");
        _lblWatchedRoots         = AddRow(table, "Наблюдаемых корней:");
        _lblQueueDepth           = AddRow(table, "Очередь зеркалирования:");
        _lblHighWaterMark        = AddRow(table, "Пик очереди (сессия):");
        AddSectionHeader(table, "Объёмы (с момента запуска)");
        _lblFilesCopied          = AddRow(table, "Файлов скопировано:");
        _lblFilesDeleted         = AddRow(table, "Файлов удалено:");
        _lblBytesCopied          = AddRow(table, "Объём скопирован:");
        _lblErrors               = AddRow(table, "Ошибок обработки:");
        AddSectionHeader(table, "Скорость");
        _lblBandwidth            = AddRow(table, "Текущий лимит:");
        _lblTurbo                = AddRow(table, "Turbo-режим:");
        AddSectionHeader(table, "Память процесса");
        _lblWorkingSet           = AddRow(table, "Working Set:");
        _lblPrivateMemory        = AddRow(table, "Private Memory:");
        AddSectionHeader(table, "Расписание");
        _lblNextReconcile        = AddRow(table, "До следующей реконсиляции:");
        _lblLastReconcileStart   = AddRow(table, "Последняя начата:");
        _lblLastReconcileEnd     = AddRow(table, "Последняя завершена:");
        _lblLastRegistrySnapshot = AddRow(table, "Последний снимок реестра:");
        _lblLastLogMirror        = AddRow(table, "Последнее зеркало журналов:");

        scroll.Controls.Add(table);
        Controls.Add(scroll);   // Fill — added FIRST (docked last → takes remainder)

        var bar = new FlowLayoutPanel
        {
            Dock          = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height        = 44,
            Padding       = new Padding(8, 6, 8, 6),
        };
        var btnClose = new Button { Text = "Закрыть", Width = 90 };
        // Explicit Close() handler — DialogResult/CancelButton only auto-close
        // modal (ShowDialog) forms; StatsForm is modeless (Show).
        btnClose.Click += (_, _) => Close();
        bar.Controls.Add(btnClose);
        Controls.Add(bar);      // Bottom — added LAST (docked first → reserves edge)

        // Initial fill so window doesn't show "—" for a full tick.
        RefreshStats();

        int interval = Math.Clamp(settings.StatsRefreshIntervalMs, 250, 10_000);
        _timer = new System.Windows.Forms.Timer { Interval = interval };
        _timer.Tick += (_, _) => RefreshStats();
        _timer.Start();

        FormClosed += (_, _) =>
        {
            try { _timer.Stop(); _timer.Dispose(); } catch { }
        };
    }

    /// <summary>
    /// Reads a fresh stats snapshot from the live controller (resolved via
    /// the getter each tick — see ctor comments).  Cheap — no I/O, no async,
    /// fully synchronous on the UI thread.
    /// </summary>
    private void RefreshStats()
    {
        var controller = _controllerGetter();
        if (controller is null)
        {
            // Controller gap (Stop in progress, or DoRestartAsync
            // between Stop and Start).  Show "—" / "Остановлена" instead of
            // stale numbers from a previous Disposed instance.
            _lblStatus.Text               = "Остановлена";
            _lblWatchedRoots.Text         = "—";
            _lblQueueDepth.Text           = "—";
            _lblHighWaterMark.Text        = "—";
            _lblFilesCopied.Text          = "—";
            _lblFilesDeleted.Text         = "—";
            _lblBytesCopied.Text          = "—";
            _lblErrors.Text               = "—";
            _lblBandwidth.Text            = "—";
            _lblTurbo.Text                = "—";
            _lblWorkingSet.Text           = "—";
            _lblPrivateMemory.Text        = "—";
            _lblNextReconcile.Text        = "—";
            _lblLastReconcileStart.Text   = "—";
            _lblLastReconcileEnd.Text     = "—";
            _lblLastRegistrySnapshot.Text = "—";
            _lblLastLogMirror.Text        = "—";
            return;
        }

        SyncController.StatsSnapshot snap;
        try { snap = controller.GetStatsSnapshot(); }
        catch { return; }  // controller may have been disposed in the gap

        _lblStatus.Text               = !controller.IsRunning
                                            ? "Остановлена"
                                            : snap.Reconciling
                                                ? "Идёт реконсиляция"
                                                : "Работает";
        _lblWatchedRoots.Text         = snap.WatchedRoots.ToString();
        _lblQueueDepth.Text           = $"{snap.QueueDepth} / {snap.QueueCapacity} " +
                                        $"({(snap.QueueCapacity > 0 ? snap.QueueDepth * 100 / snap.QueueCapacity : 0)}%)";
        _lblHighWaterMark.Text        = snap.HighWaterMark.ToString();
        _lblFilesCopied.Text          = snap.FilesCopiedTotal.ToString("N0");
        _lblFilesDeleted.Text         = snap.FilesDeletedTotal.ToString("N0");
        _lblBytesCopied.Text          = FormatBytes(snap.BytesCopiedTotal);
        _lblErrors.Text               = snap.ErrorsTotal.ToString("N0");
        _lblBandwidth.Text            = snap.CurrentBandwidthBitsPerSecond == 0
                                            ? "без ограничения"
                                            : FormatBitsPerSec(snap.CurrentBandwidthBitsPerSecond);
        _lblTurbo.Text                = snap.TurboModeActive ? "активен" : "—";
        _lblWorkingSet.Text           = FormatBytes(snap.ProcessWorkingSetBytes);
        _lblPrivateMemory.Text        = FormatBytes(snap.ProcessPrivateMemoryBytes);
        _lblNextReconcile.Text        = FormatTimeUntil(snap.NextScheduledReconcileUtc);
        _lblLastReconcileStart.Text   = FormatTimeSince(snap.LastReconcileStartUtc)
                                        + (snap.LastReconcileStartUtc is null
                                            ? "" : $"  (в {snap.LastReconcileStartUtc.Value.ToLocalTime():HH:mm:ss})");
        _lblLastReconcileEnd.Text     = snap.Reconciling
                                            ? "идёт сейчас..."
                                            : FormatTimeSince(snap.LastReconcileEndUtc);
        _lblLastRegistrySnapshot.Text = FormatTimeSince(snap.LastRegistrySnapshotUtc);
        _lblLastLogMirror.Text        = FormatTimeSince(snap.LastLogMirrorUtc);
    }

    // ── Formatters ────────────────────────────────────────────────────────────

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 Б";
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v >= 100 ? $"{v:F0} {units[u]}" : $"{v:F1} {units[u]}";
    }

    private static string FormatBitsPerSec(long bps)
    {
        if (bps <= 0) return "0";
        if (bps >= 1_000_000) return $"{bps / 1_000_000.0:F1} Мбит/с";
        if (bps >= 1_000)     return $"{bps / 1_000.0:F1} Кбит/с";
        return $"{bps} бит/с";
    }

    private static string FormatTimeUntil(DateTime? utc)
    {
        if (utc is null) return "—";
        var delta = utc.Value - DateTime.UtcNow;
        if (delta.TotalSeconds <= 0) return "ожидается с минуты на минуту";
        if (delta.TotalDays    >= 1) return $"{delta.TotalDays:F1} сут";
        if (delta.TotalHours   >= 1) return $"{delta.TotalHours:F1} ч";
        if (delta.TotalMinutes >= 1) return $"{delta.TotalMinutes:F1} мин";
        return $"{delta.TotalSeconds:F0} с";
    }

    private static string FormatTimeSince(DateTime? utc)
    {
        if (utc is null) return "—";
        var delta = DateTime.UtcNow - utc.Value;
        if (delta.TotalSeconds < 0)    return "только что";
        if (delta.TotalSeconds < 60)   return $"{delta.TotalSeconds:F0} с назад";
        if (delta.TotalMinutes < 60)   return $"{delta.TotalMinutes:F0} мин назад";
        if (delta.TotalHours   < 24)   return $"{delta.TotalHours:F1} ч назад";
        return $"{delta.TotalDays:F1} сут назад";
    }

    // ── Layout helpers ────────────────────────────────────────────────────────

    private static Label AddRow(TableLayoutPanel p, string label)
    {
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var lbl = new Label
        {
            Text = label, AutoSize = true,
            Padding = new Padding(0, 4, 8, 4),
            ForeColor = SystemColors.ControlDarkDark,
        };
        var val = new Label
        {
            Text = "—", AutoSize = true,
            Padding = new Padding(0, 4, 0, 4),
            Font = new Font("Consolas", 9.5f),
            Anchor = AnchorStyles.Left,
        };
        p.Controls.Add(lbl);
        p.Controls.Add(val);
        return val;
    }

    private static void AddSectionHeader(TableLayoutPanel p, string text)
    {
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var lbl = new Label
        {
            Text = text, AutoSize = true,
            Padding = new Padding(0, 12, 0, 4),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = SystemColors.HotTrack,
        };
        p.Controls.Add(lbl);
        p.SetColumnSpan(lbl, 2);
    }
}
