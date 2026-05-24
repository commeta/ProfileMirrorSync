using ProfileMirrorSync.Models;
using ProfileMirrorSync.Services;
using System.Diagnostics;

namespace ProfileMirrorSync.UI;

/// <summary>
/// Settings dialog — six tabs: General / Folders / Advanced / Performance /
/// Archive / Stats.  Always used as modal (ShowDialog); disposed by caller
/// via 'using'.
///
/// changes:
///   • StartMinimizedToTray is now editable on the General tab (was hardcoded
///     to true in BuildResultFromForm).
///   • RegistryBackupIntervalDays is on the Advanced tab — the registry
///     snapshot is decoupled from the reconcile schedule (default 30 days).
///   • New "Статистика" tab toggles the tray menu item and configures the
///     refresh interval of the live stats window.
///   • SettingsVersion floor bumped to 11 to reflect the new fields.
///
/// changes:
///   • Drive-letter destinations (e.g. "Z:") are rejected with a clear
///     message on OK — they produce drive-relative paths in Path.Combine.
///   • All AppSettings fields now have UI controls.  The _preserved*
///     workaround is removed: RetryCount, SkipStartupReconcileIfWithinMinutes,
///     ResumeEnabled, ResumeMinFileSizeBytes, ResumeSidecarMaxAgeDays are
///     now editable on the Performance tab.
///   • New "Зеркалировать журналы (раз в сутки)" checkbox on the Advanced
///     tab, next to the registry-snapshot option.
///   • SettingsVersion is preserved at MAX(current, 11) instead of being
///     hard-pinned, so a downgrade doesn't silently truncate fields added
///     in a future version.
/// </summary>
public sealed class SettingsForm : Form
{
    // ── Tab 1: General ──
    private readonly TextBox       _destination;
    private readonly Button        _browseBtn;
    private readonly NumericUpDown _bandwidthMbps;
    private readonly ComboBox      _logLevel;
    private readonly CheckBox      _syncOnStartup;
    private readonly CheckBox      _startMinimizedToTray;          //

    // ── Tab 2: Folders ──
    private readonly CheckedListBox _folderList;
    private readonly Label          _folderPathPreview;
    private readonly List<string>   _customFolderPaths;

    // ── Tab 3: Advanced ──
    private readonly TextBox       _excluded;
    private readonly NumericUpDown _debounce;
    private readonly NumericUpDown _reconcile;
    private readonly CheckBox      _disableReconcile;              //
    private readonly CheckBox      _registry;
    private readonly TextBox       _registryPaths;
    private readonly NumericUpDown _registryBackupIntervalDays;    //
    private readonly CheckBox      _mirrorLogs;                    //

    // ── Tab 4: Performance ──
    private readonly NumericUpDown _reconcileFileDelay;
    private readonly NumericUpDown _reconcileBatchSize;
    private readonly NumericUpDown _reconcileBatchPause;
    private readonly NumericUpDown _logRetentionDays;
    private readonly CheckBox      _enableTraceMode;
    private readonly CheckBox      _turboEnabled;
    private readonly NumericUpDown _turboThreshold;
    private readonly NumericUpDown _turboBandwidthMbps;
    private readonly CheckBox      _turboOnReconcile;              //
    private readonly CheckBox      _reconcileOnWake;               //
    private readonly CheckBox      _reconcileOnUnlock;             //
    private readonly CheckBox      _reconcileOnLogon;              //
    private readonly NumericUpDown _reconcileJitterPercent;
    private readonly NumericUpDown _queueCapacity;
    private readonly NumericUpDown _earlyReconcileQueueThresholdPct;
    private readonly NumericUpDown _earlyReconcileMinGapMinutes;
    private readonly CheckBox      _lowerIoPriority;
    // previously preserved via _preserved* snapshots, now editable
    private readonly NumericUpDown _retryCount;
    private readonly NumericUpDown _skipStartupReconcileMinutes;
    private readonly CheckBox      _resumeEnabled;
    private readonly NumericUpDown _resumeMinFileSizeMb;
    private readonly NumericUpDown _resumeSidecarMaxAgeDays;
    // file publish mode (Advanced tab)
    private readonly ComboBox      _publishMode;
    private readonly CheckBox      _deletionSafetyGuard;   // opt-in

    // ── Tab 5: Archive ( — post-sync external program) ──
    private readonly ComboBox      _postSyncPreset;                //
    private readonly CheckBox      _postSyncEnabled;
    private readonly TextBox       _postSyncExePath;
    private readonly TextBox       _postSyncArguments;
    private readonly TextBox       _postSyncWorkingDir;
    private readonly NumericUpDown _postSyncIntervalMinutes;
    private readonly NumericUpDown _postSyncTimeoutMinutes;
    private readonly CheckBox      _postSyncLowPriority;

    // ── Tab 6: Stats  ──
    private readonly CheckBox      _statsWindowEnabled;
    private readonly NumericUpDown _statsRefreshIntervalMs;

    public AppSettings Result { get; private set; }

    // single shared ToolTip for all hover hints (replaces the big
    // gray hint Labels that cluttered every tab).  Each parameter now carries
    // an "ⓘ" glyph; hovering it (or its control) shows the explanation.
    private readonly ToolTip _tips = new()
    {
        AutoPopDelay = 30000,   // keep long hints visible 30 s
        InitialDelay = 300,
        ReshowDelay  = 100,
        ShowAlways   = true,
    };

    // preserve the incoming SettingsVersion so a downgrade (running
    // an older PMS on settings.json from a newer one) doesn't silently
    // re-stamp the file with the older version number.
    private readonly int _incomingSettingsVersion;

    // ── Built-in folder metadata ──────────────────────────────────────────────

    private static readonly string[] FolderLabels =
    {
        "Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music",
        "Saved Games", "Favorites", "Contacts", "Links", "Searches",
        @"AppData\Roaming", @"AppData\Local", @"AppData\LocalLow",
    };

    private static string ResolveBuiltinPath(string label)
    {
        string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return label switch
        {
            "Desktop"           => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Documents"         => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Downloads"         => Path.Combine(up, "Downloads"),
            "Pictures"          => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "Videos"            => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "Music"             => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "Saved Games"       => Path.Combine(up, "Saved Games"),
            "Favorites"         => Environment.GetFolderPath(Environment.SpecialFolder.Favorites),
            "Contacts"          => Path.Combine(up, "Contacts"),
            "Links"             => Path.Combine(up, "Links"),
            "Searches"          => Path.Combine(up, "Searches"),
            @"AppData\Roaming"  => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"AppData\Local"    => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"AppData\LocalLow" => Path.Combine(up, @"AppData\LocalLow"),
            _                   => string.Empty
        };
    }

    private static bool[] ReadCheckedStates(AppSettings s) =>
    [
        s.MirrorDesktop, s.MirrorDocuments, s.MirrorDownloads,
        s.MirrorPictures, s.MirrorVideos, s.MirrorMusic,
        s.MirrorSavedGames, s.MirrorFavorites, s.MirrorContacts,
        s.MirrorLinks, s.MirrorSearches,
        s.MirrorAppDataRoaming, s.MirrorAppDataLocal, s.MirrorAppDataLocalLow,
    ];

    // ── Constructor ───────────────────────────────────────────────────────────

    public SettingsForm(AppSettings current)
    {
        Result      = current;
        _customFolderPaths = new List<string>(current.CustomFolderPaths);
        _incomingSettingsVersion = current.SettingsVersion;

        Text          = "Параметры ProfileMirrorSync";
        Width         = 740;
        Height        = 720;
        MinimumSize   = new Size(620, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font          = new Font("Segoe UI", 9f);

        var tabs = new TabControl { Dock = DockStyle.Fill };

        // ── Tab 1: General ────────────────────────────────────────────────────
        var tabGeneral = new TabPage("Основные");
        var gen = MakeTable3(215);

        _destination = new TextBox { Dock = DockStyle.Fill, Text = current.DestinationRoot ?? "" };
        _browseBtn   = new Button  { Text = "Обзор…", Dock = DockStyle.Fill };
        _browseBtn.Click += OnBrowseDestination;
        Row3(gen, LblTip("Сетевая папка:",
            "Корневая папка приёмника (UNC \\\\сервер\\шара\\… или подключённый диск). " +
            "Внутри создаётся {приёмник}\\{Машина}\\{Пользователь}. Указывайте конкретную " +
            "папку, а не корень диска («Z:» отвергается)."), _destination, _browseBtn);

        decimal mbps = Math.Clamp(Math.Round(current.MaxBandwidthBitsPerSecond / 1_000_000m, 1), 0.1m, 1000m);
        _bandwidthMbps = Spin(0.1m, 1000m, 0.5m, 1, mbps);
        Row3(gen, LblTip("Лимит скорости (Мбит/с):",
            "Базовый шейпер трафика (token-bucket). Размазывает запись по времени, чтобы " +
            "не забить интернет-канал."), _bandwidthMbps, LblGray("0.1 – 1000"));

        _logLevel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Anchor = AnchorStyles.Left };
        _logLevel.Items.AddRange(new object[] { "Debug — все события", "Info — обычный режим", "Warning — только ошибки" });
        _logLevel.SelectedIndex = current.LogLevel switch { AppLogLevel.Info => 1, AppLogLevel.Warning => 2, _ => 0 };
        Row3(gen, LblTip("Уровень журнала:",
            "Сколько подробностей писать в лог. Debug — всё (для диагностики), Info — " +
            "обычный режим, Warning — только ошибки. Логи лежат в папке Logs " +
            "рядом с настройками (%LocalAppData%\\ProfileMirrorSync)."),
            _logLevel, LblGray("Debug по умолчанию"));

        _syncOnStartup = new CheckBox { Checked = current.SyncOnStartup, AutoSize = true };
        Row3(gen, LblTip("Синхронизация при запуске:",
            "Выполнять полную сверку (реконсиляцию) сразу при старте программы. Ловит " +
            "изменения, сделанные пока программа была выключена."), _syncOnStartup, new Label());

        // Previously hardcoded to true in BuildResultFromForm.
        _startMinimizedToTray = new CheckBox { Checked = current.StartMinimizedToTray, AutoSize = true };
        Row3(gen, LblTip("Запускать свёрнутым в трей:",
            "При запуске не показывать окно — только значок в области уведомлений. " +
            "Пользователь не должен ощущать присутствие программы."), _startMinimizedToTray, new Label());

        var openLogsBtn = new Button { Text = "Открыть папку логов…", AutoSize = true, Anchor = AnchorStyles.Left };
        openLogsBtn.Click += (_, _) => OpenExplorer(ProfileMirrorSync.Services.AppPaths.LogsDirectory);
        Row3(gen, new Label(), openLogsBtn, new Label());

        AddScrollable(tabGeneral, gen);
        tabs.TabPages.Add(tabGeneral);

        // ── Tab 2: Folders ────────────────────────────────────────────────────
        var tabFolders = new TabPage("Папки");
        var fPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(12),
        };
        fPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        fPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        fPanel.Controls.Add(new Label
        {
            Text = "Выберите папки профиля для резервного копирования.\r\n" +
                   "Используйте «Добавить папку» для произвольных путей.",
            AutoSize = true, Padding = new Padding(0, 0, 0, 6)
        });

        var listRef    = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, Font = new Font("Consolas", 9.5f) };
        var previewRef = new Label { AutoSize = false, Dock = DockStyle.Fill, Height = 24, Padding = new Padding(0, 4, 0, 0) };

        var selRow  = new FlowLayoutPanel { AutoSize = true };
        var btnAll  = new Button { Text = "✔  Выбрать все", AutoSize = true };
        var btnNone = new Button { Text = "✖  Снять все",   AutoSize = true };
        btnAll .Click += (_, _) => { for (int i = 0; i < listRef.Items.Count; i++) listRef.SetItemChecked(i, true);  };
        btnNone.Click += (_, _) => { for (int i = 0; i < listRef.Items.Count; i++) listRef.SetItemChecked(i, false); };
        selRow.Controls.Add(btnAll);
        selRow.Controls.Add(btnNone);
        fPanel.Controls.Add(selRow);

        bool[] chkInit = ReadCheckedStates(current);
        for (int i = 0; i < FolderLabels.Length; i++)
            listRef.Items.Add(FolderLabels[i], chkInit[i]);

        foreach (string cp in _customFolderPaths)
            listRef.Items.Add($"📁 {cp}", true);

        fPanel.Controls.Add(listRef);

        var customRow  = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
        var btnAddPath = new Button { Text = "➕  Добавить папку…", AutoSize = true };
        var btnRemPath = new Button { Text = "➖  Удалить выбранную", AutoSize = true };
        btnAddPath.Click += (_, _) => OnAddCustomFolder(listRef);
        btnRemPath.Click += (_, _) => OnRemoveCustomFolder(listRef);
        customRow.Controls.Add(btnAddPath);
        customRow.Controls.Add(btnRemPath);
        fPanel.Controls.Add(customRow);

        listRef.SelectedIndexChanged += (_, _) =>
        {
            int idx = listRef.SelectedIndex;
            if (idx < 0) { previewRef.Text = ""; return; }

            string fp;
            if (idx < FolderLabels.Length)
                fp = ResolveBuiltinPath(FolderLabels[idx]);
            else
            {
                int ci = idx - FolderLabels.Length;
                fp = ci < _customFolderPaths.Count ? _customFolderPaths[ci] : "";
            }

            bool ok = !string.IsNullOrEmpty(fp) && Directory.Exists(fp);
            previewRef.Text      = string.IsNullOrEmpty(fp) ? "" : (ok ? $"📁  {fp}" : $"⚠  {fp}  (папка не найдена)");
            previewRef.ForeColor = ok ? SystemColors.ControlText : Color.DarkRed;
        };

        fPanel.Controls.Add(previewRef);
        _folderList        = listRef;
        _folderPathPreview = previewRef;

        tabFolders.Controls.Add(fPanel);
        tabs.TabPages.Add(tabFolders);

        // ── Tab 3: Advanced ───────────────────────────────────────────────────
        var tabAdv = new TabPage("Дополнительно");
        var adv    = MakeTable2(270);

        Row2(adv, LblTip("Задержка стабилизации файла (мс):",
            "Сколько ждать после последнего изменения файла перед копированием " +
            "(debounce). Защищает от копирования файла, который ещё пишется. " +
            "Слишком малое значение → лишние копии при активной записи."),
            _debounce = Spin(100, 5000, 50, 0, current.FileDebounceMilliseconds));
        Row2(adv, LblTip("Интервал плановой реконсиляции (мин, 1440 = 24ч):",
            "Как часто запускать полную фоновую сверку источника и приёмника. " +
            "Реконсиляция ловит то, что мог пропустить FileSystemWatcher. Реальное " +
            "время сверки слегка варьируется (jitter) для рассинхронизации парка ПК."),
            _reconcile = Spin(5, 10080, 60, 0, current.ReconcileIntervalMinutes));

        // Master switch for the scheduled reconcile.
        _disableReconcile = new CheckBox
        {
            Text     = "Отключить плановую (периодическую) реконсиляцию",
            AutoSize = true,
            Checked  = !current.ReconcileEnabled,
        };
        RowSpan2(adv, CheckTip(_disableReconcile,
            "По умолчанию ВЫКЛЮЧЕНО (реконсиляция работает). Если включить этот флажок, " +
            "периодическая сверка по таймеру не выполняется — остаётся только " +
            "зеркалирование в реальном времени (FileSystemWatcher), синхронизация при " +
            "запуске и сверка по событиям (сон/разблокировка). Аварийная досрочная " +
            "сверка при переполнении очереди продолжит работать как защита от потери " +
            "данных. Включайте, только если полностью доверяете FSW + стартовой сверке."));

        SectionHeader(adv, "Исключения (по одному пути на строку, относительные)");
        _excluded = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 120,
            Text = string.Join(Environment.NewLine, current.ExcludedRelativePaths) };
        RowSpan2(adv, _excluded);

        SectionHeader(adv, "Снимки реестра (экспорт через reg.exe)");
        _registry = new CheckBox { Text = "Сохранять снимки HKCU\\Software",
            AutoSize = true, Checked = current.MirrorRegistrySnapshots };
        RowSpan2(adv, CheckTip(_registry,
            "Периодически экспортировать ветки реестра пользователя (.reg через reg.exe) " +
            "на приёмник. Полезно для восстановления настроек приложений. Пути задаются " +
            "ниже, по одному на строку."));

        _registryPaths = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 60,
            Text = string.Join(Environment.NewLine, current.RegistryPaths) };
        RowSpan2(adv, _registryPaths);

        // Decouple registry snapshot interval from reconcile interval.
        // Stored as minutes internally; UI exposed as days for usability.
        // 0 days = fall back to "snapshot on every reconcile" (legacy behaviour).
        int regBackupDays = current.RegistryBackupIntervalMinutes <= 0
            ? 0 : Math.Max(1, current.RegistryBackupIntervalMinutes / 1440);
        Row2(adv, LblTip("Интервал снимков реестра (дней, 0=на каждой реконсиляции):",
            "Снимок реестра делается на своём таймере, независимо от реконсиляции, чтобы " +
            "не грузить сервер тяжёлым .reg-экспортом при каждой сверке. 30 дней — " +
            "разумное значение по умолчанию; 0 — на каждой реконсиляции (старое поведение)."),
            _registryBackupIntervalDays = Spin(0, 365, 1, 0, regBackupDays));

        // Mirror logs section
        SectionHeader(adv, "Зеркалирование журналов");
        _mirrorLogs = new CheckBox
        {
            Text     = "Копировать журналы на приёмник (раз в сутки)",
            AutoSize = true,
            Checked  = current.MirrorLogs,
        };
        RowSpan2(adv, CheckTip(_mirrorLogs,
            "Раз в сутки (по завершении плановой реконсиляции) копировать файлы " +
            "pms-ГГГГ-ММ-ДД.log в {приёмник}\\{Машина}\\{Пользователь}\\Logs\\. " +
            "Сегодняшний (открытый) лог не копируется. Копирование использует тот же " +
            "лимит скорости, что и обычная синхронизация."));

        // File publish mode
        SectionHeader(adv, "Режим записи файлов на приёмник");
        _publishMode = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width         = 360,
            Anchor        = AnchorStyles.Left,
        };
        _publishMode.Items.Add("Прямая запись (макс. совместимость)");      // index 0 = DirectWrite
        _publishMode.Items.Add("Через временный файл + переименование");    // index 1 = TempThenRename
        _publishMode.SelectedIndex = current.PublishMode == FilePublishMode.TempThenRename ? 1 : 0;
        Row2(adv, LblTip("Публикация:",
            "«Прямая запись» пишет прямо в целевой файл — максимально совместима с долями, " +
            "отвергающими переименование (vboxsf, часть NAS); резюмирование больших файлов " +
            "работает только в этом режиме. «Через временный файл» пишет в *.pms_tmp рядом " +
            "и атомарно переименовывает поверх цели, поэтому приёмник заменяется только " +
            "полностью записанным файлом; при отказе доли в переименовании автоматически " +
            "откатывается на прямую запись."), _publishMode);

        // Empty-source deletion safety guard (opt-in)
        SectionHeader(adv, "Защита от потери данных");
        _deletionSafetyGuard = new CheckBox
        {
            Text     = "Не удалять файлы на приёмнике, если источник пуст/недоступен",
            AutoSize = true,
            Checked  = current.DeletionSafetyGuardEnabled,
        };
        RowSpan2(adv, CheckTip(_deletionSafetyGuard,
            "Это ОДНОСТОРОННЕЕ ЗЕРКАЛО: по умолчанию пустой источник означает пустой " +
            "приёмник (файлы-сироты удаляются). Если включить, реконсиляция ПРОПУСТИТ " +
            "удаление, когда источник существует, но не дал ни одного файла (профиль не " +
            "загружен, OneDrive-плейсхолдеры, AV-карантин, слетел ACL), а на приёмнике " +
            "файлы есть — чтобы не стереть единственную серверную копию. В лог пишется " +
            "предупреждение; удаление повторится, когда источник снова станет читаемым."));

        AddScrollable(tabAdv, adv);
        tabs.TabPages.Add(tabAdv);

        // ── Tab 4: Performance ────────────────────────────────────────────────
        var tabPerf = new TabPage("Производительность");
        var perf    = MakeTable2(300);

        SectionHeader(perf, "Нагрузка при реконсиляции");
        Row2(perf, LblTip("Пауза между файлами (мс):",
            "Задержка после копирования каждого файла при реконсиляции. Размазывает IO " +
            "во времени, чтобы не было всплесков на сервере и в сети. 0 = без паузы."),
            _reconcileFileDelay = Spin(0, 2000, 10, 0, current.ReconcileFileDelayMs));
        Row2(perf, LblTip("Размер пакета (файлов):",
            "Сколько файлов скопировать подряд, прежде чем сделать паузу между пакетами " +
            "(ниже). 0 = пакетные паузы выключены."),
            _reconcileBatchSize = Spin(0, 1000, 10, 0, current.ReconcileBatchSize));
        Row2(perf, LblTip("Пауза между пакетами (мс):",
            "Дополнительная пауза после каждого пакета файлов. Вместе с паузой между " +
            "файлами позволяет тонко размазать нагрузку на медленном канале."),
            _reconcileBatchPause = Spin(0, 30000, 100, 0, current.ReconcileBatchPauseMs));

        SectionHeader(perf, "Журналы");
        Row2(perf, LblTip("Хранить логи (дней, 0 = всегда):",
            "Старые файлы pms-*.log автоматически удаляются по истечении этого срока. " +
            "0 = хранить вечно."),
            _logRetentionDays = Spin(0, 3650, 1, 0, current.LogRetentionDays));
        _enableTraceMode = new CheckBox
        {
            Text     = "Трассировка: записывать стек исключений (Warning/Error)",
            AutoSize = true,
            Checked  = current.EnableTraceMode
        };
        RowSpan2(perf, CheckTip(_enableTraceMode,
            "При ошибках писать в лог полный стек вызовов. Помогает в диагностике, но " +
            "делает логи многословнее. Для обычной работы не нужно."));

        SectionHeader(perf, "Turbo-режим (ускорение при event-storm)");
        _turboEnabled = new CheckBox { Text = "Включить turbo при большой очереди в реальном времени", AutoSize = true, Checked = current.TurboFirstRunEnabled };
        RowSpan2(perf, CheckTip(_turboEnabled,
            "Когда в реальном времени накапливается всплеск изменений (например, " +
            "распаковали большой архив), временно поднять лимит скорости до turbo-" +
            "значения, чтобы быстрее разгрести очередь, затем вернуться к базовому лимиту."));
        Row2(perf, LblTip("Порог очереди (файлов):",
            "Сколько файлов должно накопиться в очереди, чтобы включился turbo."),
            _turboThreshold = Spin(10, 100000, 100, 0, current.TurboThresholdFiles));
        Row2(perf, LblTip("Turbo скорость (Мбит/с):",
            "Лимит скорости в turbo-режиме."),
            _turboBandwidthMbps = Spin(1, 1000, 1, 0, current.TurboFirstRunBandwidthMbps));
        _turboOnReconcile = new CheckBox
        {
            Text     = "Разрешать turbo и во время плановой реконсиляции (не только real-time)",
            AutoSize = true,
            Checked  = current.TurboOnReconcile
        };
        RowSpan2(perf, CheckTip(_turboOnReconcile,
            "По умолчанию выключено: turbo срабатывает только на всплеск в реальном " +
            "времени. Если включить, turbo может подняться и во время ночной плановой " +
            "сверки — это ускорит её, но создаст всплеск нагрузки на сервер/канал."));

        SectionHeader(perf, "Триггеры реконсиляции по событиям");
        _reconcileOnWake = new CheckBox
        {
            Text     = "После выхода из сна/гибернации (рекомендуется — FSW не работает во сне)",
            AutoSize = true,
            Checked  = current.ReconcileOnWake
        };
        RowSpan2(perf, CheckTip(_reconcileOnWake,
            "Во сне FileSystemWatcher не работает и может пропустить изменения, поэтому " +
            "сверка после пробуждения оправдана. Рекомендуется оставить включённым."));
        _reconcileOnUnlock = new CheckBox
        {
            Text     = "После разблокировки экрана (обычно лишнее — FSW работает при блокировке)",
            AutoSize = true,
            Checked  = current.ReconcileOnUnlock
        };
        RowSpan2(perf, CheckTip(_reconcileOnUnlock,
            "При простой блокировке экрана FileSystemWatcher продолжает работать, файлы " +
            "не «разъезжаются», поэтому сверка здесь обычно избыточна. По умолчанию выкл."));
        _reconcileOnLogon = new CheckBox
        {
            Text     = "После входа в сессию (logon)",
            AutoSize = true,
            Checked  = current.ReconcileOnLogon
        };
        RowSpan2(perf, CheckTip(_reconcileOnLogon,
            "Запускать сверку при входе пользователя в сессию. Обычно дублирует " +
            "«синхронизацию при запуске». По умолчанию выкл."));

        SectionHeader(perf, "Корпоративный режим");
        Row2(perf, LblTip("Jitter плановой реконсиляции (%):",
            "Случайный разброс времени плановой сверки (± половина процента от интервала), " +
            "чтобы парк ПК не бил по серверу одновременно."),
            _reconcileJitterPercent = Spin(0, 100, 5, 0, current.ReconcileJitterPercent));
        Row2(perf, LblTip("Лимит очереди (операций):",
            "Максимум операций в очереди от FileSystemWatcher до включения backpressure. " +
            "При переполнении события Created/Changed отбрасываются (их подберёт " +
            "реконсиляция), а Delete/Rename удерживаются."),
            _queueCapacity = Spin(100, 100000, 1000, 0, current.QueueCapacity));
        Row2(perf, LblTip("Порог досрочной реконсиляции (% очереди, 0=выкл):",
            "Если очередь заполнится до этого процента, запускается досрочная сверка — " +
            "защита от потери данных при всплеске изменений. 0 = выключить."),
            _earlyReconcileQueueThresholdPct = Spin(0, 100, 5, 0, current.EarlyReconcileQueueThresholdPct));
        Row2(perf, LblTip("Мин. интервал между досрочными реконсиляциями (мин):",
            "Не запускать досрочную сверку чаще, чем раз в N минут, чтобы постоянный " +
            "поток изменений не вызывал непрерывные сверки."),
            _earlyReconcileMinGapMinutes = Spin(1, 1440, 5, 0, current.EarlyReconcileMinGapMinutes));
        _lowerIoPriority = new CheckBox
        {
            Text     = "Низкий приоритет IO/CPU (фоновый режим)",
            AutoSize = true,
            Checked  = current.LowerIoPriority
        };
        RowSpan2(perf, CheckTip(_lowerIoPriority,
            "Понижать приоритет потока копирования (THREAD_MODE_BACKGROUND) на время " +
            "записи — диск и CPU отдаются активным приложениям пользователя. " +
            "Пользователь не должен ощущать присутствие программы."));

        // previously hidden settings, now exposed
        SectionHeader(perf, "Надёжность копирования");
        Row2(perf, LblTip("Попыток при ошибке копирования:",
            "Сколько раз повторить копирование файла при временной ошибке (сеть моргнула, " +
            "файл занят) с нарастающей паузой, прежде чем признать ошибку."),
            _retryCount = Spin(1, 20, 1, 0, current.RetryCount));
        Row2(perf, LblTip("Пропускать стартовую реконсиляцию,\r\nесли предыдущая завершилась <N мин назад (0=всегда):",
            "Защита от лишней сверки при быстром перезапуске (Stop → правка настроек → " +
            "Start). Если предыдущая сверка успешно завершилась менее N минут назад, " +
            "стартовая пропускается. 0 = всегда выполнять."),
            _skipStartupReconcileMinutes = Spin(0, 1440, 1, 0, current.SkipStartupReconcileIfWithinMinutes));

        SectionHeader(perf, "Резюмирование больших файлов");
        _resumeEnabled = new CheckBox
        {
            Text     = "Включить byte-range resume (продолжение прерванной копии)",
            AutoSize = true,
            Checked  = current.ResumeEnabled,
        };
        RowSpan2(perf, CheckTip(_resumeEnabled,
            "Для больших файлов сохранять прогресс копирования рядом (sidecar), чтобы " +
            "после обрыва/выключения продолжить с места, а не качать заново. Работает " +
            "только в режиме «Прямая запись»."));
        Row2(perf, LblTip("Минимальный размер файла для resume (МБ):",
            "Резюмирование применяется только к файлам крупнее этого размера — для мелких " +
            "файлов накладные расходы не оправданы."),
            _resumeMinFileSizeMb = Spin(1, 10240, 10, 0,
                Math.Clamp(current.ResumeMinFileSizeBytes / (1024 * 1024), 1, 10240)));
        Row2(perf, LblTip("Срок хранения sidecar-файлов (дней):",
            "Заброшенные файлы прогресса (.json) старше этого срока удаляются при " +
            "реконсиляции, чтобы не копились."),
            _resumeSidecarMaxAgeDays = Spin(1, 365, 1, 0, current.ResumeSidecarMaxAgeDays));

        AddScrollable(tabPerf, perf);
        tabs.TabPages.Add(tabPerf);

        // ── Tab 5: Archive / post-sync hook ( renamed) ──────────────────
        var tabArch = new TabPage("Архивация и бэкап");
        var arch    = MakeTable2(300);

        SectionHeader(arch, "Внешняя программа после синхронизации (архиватор / скрипт)");
        _postSyncEnabled = new CheckBox
        {
            Text     = "Запускать внешнюю программу (архиватор, скрипт) после синхронизации",
            AutoSize = true,
            Checked  = current.PostSyncEnabled,
        };
        RowSpan2(arch, CheckTip(_postSyncEnabled,
            "Гибкий хук: на ОТДЕЛЬНОМ таймере (интервал ниже), независимо от реконсиляции, " +
            "запускается любая указанная программа. Пустой путь — хук выключен (можно " +
            "положиться на отдельный серверный скрипт архивации)."));

        // Preset picker: auto-fills exe/args/workdir with max-setting
        // defaults so the admin doesn't memorise archiver flags.
        _postSyncPreset = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width         = 420,
            Anchor        = AnchorStyles.Left,
        };
        foreach (var p in PostSyncPresets.All) _postSyncPreset.Items.Add(p.DisplayName);
        var presetHint = new Label
        {
            AutoSize    = true,
            ForeColor   = SystemColors.GrayText,
            MaximumSize = new Size(560, 0),
            Padding     = new Padding(0, 0, 0, 4),
        };
        Row2(arch, LblTip("Пресет:",
            "Готовые рецепты на максимальных настройках: 7-Zip, WinRAR, ZIP, зеркальная " +
            "копия Robocopy, удаление старых архивов и архивация с удалением старых. " +
            "Выбор пресета подставляет программу, аргументы и рабочую папку — при " +
            "необходимости отредактируйте их вручную."), _postSyncPreset);
        RowSpan2(arch, presetHint);

        _postSyncExePath = new TextBox { Dock = DockStyle.Fill, Text = current.PostSyncExePath };
        var browseExe = new Button { Text = "Обзор…", AutoSize = true, Anchor = AnchorStyles.Left };
        browseExe.Click += (_, __) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title  = "Выберите программу",
                Filter = "Программы (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|Все файлы (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) _postSyncExePath.Text = dlg.FileName;
        };
        var exeRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        exeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        exeRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        exeRow.Controls.Add(_postSyncExePath, 0, 0);
        exeRow.Controls.Add(browseExe, 1, 0);
        Row2(arch, LblTip("Программа:",
            "Полный путь к исполняемому файлу (.exe/.bat/.cmd). Пресеты подставляют " +
            "стандартные пути (7-Zip, WinRAR, встроенные Robocopy/forfiles)."), exeRow);

        _postSyncArguments = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 70,
            Text = current.PostSyncArguments,
        };
        RowSpan2(arch, LblTip("Аргументы командной строки:",
            "Плейсхолдеры подставляются при запуске:\r\n" +
            "  {dest}    — папка приёмника ({Приёмник}\\{Машина}\\{Пользователь})\r\n" +
            "  {backup}  — {dest}\\backup (создаётся автоматически)\r\n" +
            "  {machine}, {user}, {date} (ГГГГ-ММ-ДД), {time} (ЧЧ-ММ-СС)\r\n" +
            "Регистр плейсхолдеров не важен."));
        RowSpan2(arch, _postSyncArguments);

        _postSyncWorkingDir = new TextBox { Dock = DockStyle.Fill, Text = current.PostSyncWorkingDir };
        Row2(arch, LblTip("Рабочая папка (пусто = папка программы):",
            "Текущий каталог для запускаемой программы. Поддерживает те же плейсхолдеры. " +
            "Пусто — берётся папка самого исполняемого файла."), _postSyncWorkingDir);

        SectionHeader(arch, "Расписание и приоритет");
        Row2(arch, LblTip("Интервал таймера (мин, 1440 = раз в сутки):",
            "Как часто запускать внешнюю программу. Считается от последнего успешного " +
            "запуска, независимо от частоты реконсиляции."),
            _postSyncIntervalMinutes = Spin(1, 43200, 60, 0, current.PostSyncIntervalMinutes));
        Row2(arch, LblTip("Таймаут программы (мин, 0 = ждать до конца):",
            "Если программа не завершилась за это время, она принудительно останавливается " +
            "(вместе с дочерними процессами). 0 = ждать без ограничения."),
            _postSyncTimeoutMinutes = Spin(0, 1440, 5, 0, current.PostSyncTimeoutMinutes));
        _postSyncLowPriority = new CheckBox
        {
            Text     = "Запускать с пониженным приоритетом (BelowNormal)",
            AutoSize = true,
            Checked  = current.PostSyncLowPriority,
        };
        RowSpan2(arch, CheckTip(_postSyncLowPriority,
            "Запускать архиватор с пониженным приоритетом CPU, чтобы тяжёлое сжатие не " +
            "мешало работе пользователя."));

        // Wire preset selection AFTER all target controls exist.
        bool applyingInitialPreset = false;
        _postSyncPreset.SelectedIndexChanged += (_, _) =>
        {
            int i = _postSyncPreset.SelectedIndex;
            if (i < 0 || i >= PostSyncPresets.All.Count) return;
            var preset = PostSyncPresets.All[i];
            presetHint.Text = preset.Hint;
            if (preset.Key == PostSyncPresets.CustomKey) return;   // don't wipe fields
            // On the initial match we only set the hint — the saved exe/args/
            // workdir already equal the preset, and we must NOT flip the user's
            // saved PostSyncEnabled state.  Only an explicit user pick fills the
            // fields and enables the hook.
            if (applyingInitialPreset) return;
            _postSyncExePath.Text    = preset.ExePath;
            _postSyncArguments.Text  = preset.Arguments;
            _postSyncWorkingDir.Text = preset.WorkingDir;
            _postSyncEnabled.Checked = true;
        };
        // Initial selection: match current args back to a preset (or Custom).
        string initialKey = PostSyncPresets.MatchKey(current.PostSyncExePath, current.PostSyncArguments);
        int initialIdx = 0;
        for (int i = 0; i < PostSyncPresets.All.Count; i++)
            if (PostSyncPresets.All[i].Key == initialKey) { initialIdx = i; break; }
        applyingInitialPreset = true;
        _postSyncPreset.SelectedIndex = initialIdx;
        applyingInitialPreset = false;

        AddScrollable(tabArch, arch);
        tabs.TabPages.Add(tabArch);

        // ── Tab 6: Stats  ────────────────────────────────────────────
        var tabStats = new TabPage("Статистика");
        var stat     = MakeTable2(320);

        SectionHeader(stat, "Окно статистики");
        _statsWindowEnabled = new CheckBox
        {
            Text     = "Показывать пункт «Статистика…» в меню трея",
            AutoSize = true,
            Checked  = current.StatsWindowEnabled,
        };
        RowSpan2(stat, CheckTip(_statsWindowEnabled,
            "Показывать в окне: время до следующей реконсиляции, длину очереди, число " +
            "наблюдаемых корней, объём памяти, скопировано/удалено за сессию, текущий " +
            "лимит скорости и статус turbo, даты последней реконсиляции/снимка реестра/" +
            "зеркала логов. Сбор метрик бесплатный (нет IO/сети); пока окно закрыто, " +
            "накладные расходы нулевые."));

        Row2(stat, LblTip("Интервал обновления окна (мс, 250 – 10000):",
            "Как часто окно статистики перечитывает метрики, пока оно открыто. На " +
            "фоновую работу программы не влияет."),
            _statsRefreshIntervalMs = Spin(250, 10_000, 250, 0,
                Math.Clamp(current.StatsRefreshIntervalMs, 250, 10_000)));

        AddScrollable(tabStats, stat);
        tabs.TabPages.Add(tabStats);

        // ── Buttons ───────────────────────────────────────────────────────────
        var bar    = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8, 6, 8, 6) };
        var btnOk  = new Button { Text = "OK",     DialogResult = DialogResult.OK,     Width = 90 };
        var btnCnl = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Width = 90 };
        bar.Controls.Add(btnOk);
        bar.Controls.Add(btnCnl);

        AcceptButton = btnOk;
        CancelButton = btnCnl;
        Controls.Add(tabs);
        Controls.Add(bar);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }

    // ── Browse on STA thread so the form stays responsive ────────────────────

    private void OnBrowseDestination(object? sender, EventArgs e)
    {
        _browseBtn.Enabled = false;
        string currentText = _destination.Text.Trim();
        var form = this;

        var thread = new Thread(() =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description            = "Выберите папку назначения",
                UseDescriptionForTitle = true,
                ShowNewFolderButton    = true,
            };
            if (!string.IsNullOrWhiteSpace(currentText) &&
                !currentText.StartsWith(@"\\", StringComparison.Ordinal) &&
                Directory.Exists(currentText))
            {
                try { dlg.InitialDirectory = currentText; } catch { }
            }

            if (dlg.ShowDialog() == DialogResult.OK)
            {
                string picked = dlg.SelectedPath;
                try { form.Invoke(() => _destination.Text = picked); } catch { }
            }
            try { form.Invoke(() => _browseBtn.Enabled = true); } catch { }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    // ── Custom folder management ──────────────────────────────────────────────

    private void OnAddCustomFolder(CheckedListBox list)
    {
        var form = this;

        var thread = new Thread(() =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description            = "Выберите произвольную папку для синхронизации",
                UseDescriptionForTitle = true,
                ShowNewFolderButton    = false,
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            string picked = dlg.SelectedPath;
            try
            {
                form.Invoke(() =>
                {
                    if (string.IsNullOrEmpty(picked)) return;
                    if (_customFolderPaths.Contains(picked, StringComparer.OrdinalIgnoreCase)) return;
                    _customFolderPaths.Add(picked);
                    list.Items.Add($"📁 {picked}", true);
                    list.SelectedIndex = list.Items.Count - 1;
                });
            }
            catch { /* form may be closed */ }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private void OnRemoveCustomFolder(CheckedListBox list)
    {
        int idx = list.SelectedIndex;
        if (idx < FolderLabels.Length) return;

        int ci = idx - FolderLabels.Length;
        if (ci < 0 || ci >= _customFolderPaths.Count) return;

        _customFolderPaths.RemoveAt(ci);
        list.Items.RemoveAt(idx);
    }

    // ── Collect result + drive-letter validation ──────────

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
        {
            // reject bare drive-letter destinations
            // ("Z:", "Z" without slash).  Path.Combine treats these as
            // drive-relative, producing paths like "Z:MyComputer\Commeta" that
            // resolve against the current working directory of Z:.
            string dest = _destination.Text.Trim();
            string trimmed = dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 2 && trimmed[1] == ':' && trimmed == dest)
            {
                MessageBox.Show(this,
                    $"Папка назначения «{dest}» — это корень диска без разделителя.\r\n" +
                    $"Укажите конкретную папку, например «{dest}\\Backup».",
                    "Параметры", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                DialogResult = DialogResult.None;
                return;
            }

            Result = BuildResultFromForm();
        }
        base.OnFormClosing(e);
    }

    /// <summary>
    /// Build an AppSettings from the CURRENT form state.  Public so the
    /// "Применить сейчас" button can call it BEFORE the dialog closes.
    /// </summary>
    public AppSettings BuildResultFromForm()
    {
        bool[] chk = new bool[FolderLabels.Length];
        for (int i = 0; i < FolderLabels.Length; i++)
            chk[i] = _folderList.GetItemChecked(i);

        var activePaths = new List<string>();
        for (int i = FolderLabels.Length; i < _folderList.Items.Count; i++)
        {
            if (_folderList.GetItemChecked(i))
            {
                int ci = i - FolderLabels.Length;
                if (ci < _customFolderPaths.Count)
                    activePaths.Add(_customFolderPaths[ci]);
            }
        }

        int bps = (int)Math.Max(100_000m, _bandwidthMbps.Value * 1_000_000m);
        AppLogLevel lvl = _logLevel.SelectedIndex switch { 1 => AppLogLevel.Info, 2 => AppLogLevel.Warning, _ => AppLogLevel.Debug };

        return new AppSettings
        {
            DestinationRoot           = _destination.Text.Trim(),
            MaxBandwidthBitsPerSecond = bps,
            LogLevel                  = lvl,
            SyncOnStartup             = _syncOnStartup.Checked,
            StartMinimizedToTray      = _startMinimizedToTray.Checked,     //
            MirrorDesktop             = chk[0],  MirrorDocuments      = chk[1],
            MirrorDownloads           = chk[2],  MirrorPictures       = chk[3],
            MirrorVideos              = chk[4],  MirrorMusic          = chk[5],
            MirrorSavedGames          = chk[6],  MirrorFavorites      = chk[7],
            MirrorContacts            = chk[8],  MirrorLinks          = chk[9],
            MirrorSearches            = chk[10], MirrorAppDataRoaming = chk[11],
            MirrorAppDataLocal        = chk[12], MirrorAppDataLocalLow= chk[13],
            CustomFolderPaths         = activePaths,
            MirrorRegistrySnapshots   = _registry.Checked,
            RegistryPaths             = Lines(_registryPaths.Text),
            // UI exposes days; store as minutes.
            RegistryBackupIntervalMinutes = (int)_registryBackupIntervalDays.Value * 1440,
            MirrorLogs                = _mirrorLogs.Checked,    //
            ExcludedRelativePaths     = Lines(_excluded.Text),
            FileDebounceMilliseconds  = (int)_debounce.Value,
            ReconcileIntervalMinutes  = (int)_reconcile.Value,
            ReconcileEnabled          = !_disableReconcile.Checked,                   //
            RetryCount                = (int)_retryCount.Value,                       //
            ReconcileFileDelayMs      = (int)_reconcileFileDelay.Value,
            ReconcileBatchSize        = (int)_reconcileBatchSize.Value,
            ReconcileBatchPauseMs     = (int)_reconcileBatchPause.Value,
            LogRetentionDays          = (int)_logRetentionDays.Value,
            TurboFirstRunEnabled      = _turboEnabled.Checked,
            TurboThresholdFiles       = (int)_turboThreshold.Value,
            TurboFirstRunBandwidthMbps= (int)_turboBandwidthMbps.Value,
            TurboOnReconcile          = _turboOnReconcile.Checked,           //
            ReconcileOnWake           = _reconcileOnWake.Checked,            //
            ReconcileOnUnlock         = _reconcileOnUnlock.Checked,          //
            ReconcileOnLogon          = _reconcileOnLogon.Checked,           //
            EnableTraceMode           = _enableTraceMode.Checked,
            ReconcileJitterPercent    = (int)_reconcileJitterPercent.Value,
            QueueCapacity             = (int)_queueCapacity.Value,
            EarlyReconcileQueueThresholdPct = (int)_earlyReconcileQueueThresholdPct.Value,
            EarlyReconcileMinGapMinutes     = (int)_earlyReconcileMinGapMinutes.Value,
            LowerIoPriority           = _lowerIoPriority.Checked,
            // file publish mode
            PublishMode               = _publishMode.SelectedIndex == 1
                                            ? FilePublishMode.TempThenRename
                                            : FilePublishMode.DirectWrite,
            DeletionSafetyGuardEnabled = _deletionSafetyGuard.Checked,
            // post-sync external program (archiver / script hook)
            PostSyncEnabled           = _postSyncEnabled.Checked,
            PostSyncExePath           = _postSyncExePath.Text.Trim(),
            PostSyncArguments         = _postSyncArguments.Text,
            PostSyncWorkingDir        = _postSyncWorkingDir.Text.Trim(),
            PostSyncIntervalMinutes   = (int)_postSyncIntervalMinutes.Value,
            PostSyncTimeoutMinutes    = (int)_postSyncTimeoutMinutes.Value,
            PostSyncLowPriority       = _postSyncLowPriority.Checked,
            SkipStartupReconcileIfWithinMinutes = (int)_skipStartupReconcileMinutes.Value,
            ResumeEnabled             = _resumeEnabled.Checked,
            ResumeMinFileSizeBytes    = (long)_resumeMinFileSizeMb.Value * 1024L * 1024L,
            ResumeSidecarMaxAgeDays   = (int)_resumeSidecarMaxAgeDays.Value,
            // Stats window (real-time diagnostics)
            StatsWindowEnabled        = _statsWindowEnabled.Checked,
            StatsRefreshIntervalMs    = (int)_statsRefreshIntervalMs.Value,
            // preserve the higher of the incoming and
            // current model version so an older PMS against a newer settings.json
            // doesn't silently re-stamp the file with a lower version.
            SettingsVersion           = Math.Max(_incomingSettingsVersion, 14),
        };
    }

    // ── Layout helpers ────────────────────────────────────────────────────────

    private static Label Lbl(string t)     => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 7, 8, 7) };
    private static Label LblGray(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };

    // WinForms ToolTip does NOT word-wrap; without explicit newlines a
    // long hint renders as a single line that can span the whole screen.  Wrap
    // to a sane column (~64 chars) on word boundaries, preserving any newlines
    // the caller already inserted.
    private static string WrapTip(string text, int width = 64)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var outLines = new List<string>();
        foreach (string para in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (para.Length == 0) { outLines.Add(""); continue; }
            var line = new System.Text.StringBuilder();
            foreach (string word in para.Split(' '))
            {
                if (line.Length == 0)
                    line.Append(word);
                else if (line.Length + 1 + word.Length <= width)
                    line.Append(' ').Append(word);
                else
                {
                    outLines.Add(line.ToString());
                    line.Clear();
                    line.Append(word);
                }
            }
            if (line.Length > 0) outLines.Add(line.ToString());
        }
        return string.Join("\r\n", outLines);
    }

    // A small clickable/hoverable "ⓘ" info glyph bound to a tooltip.
    private Label InfoIcon(string tip)
    {
        string wrapped = WrapTip(tip);
        var icon = new Label
        {
            Text      = "ⓘ",
            AutoSize  = true,
            ForeColor = SystemColors.Highlight,
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor    = Cursors.Help,
            Anchor    = AnchorStyles.Left,
            Padding   = new Padding(2, 7, 0, 7),
        };
        _tips.SetToolTip(icon, wrapped);
        // Clicking also pops the tip (touch / discoverability).
        icon.Click += (_, _) => _tips.Show(wrapped, icon, 0, icon.Height, 30000);
        return icon;
    }

    // A label followed by an info glyph, composed so the row stays single-cell.
    private Control LblTip(string text, string tip)
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            Margin        = new Padding(0),
            Anchor        = AnchorStyles.Left,
            WrapContents  = false,
        };
        flow.Controls.Add(Lbl(text));
        flow.Controls.Add(InfoIcon(tip));
        return flow;
    }

    // A checkbox followed by an info glyph (for RowSpan2 boolean rows).
    private Control CheckTip(CheckBox box, string tip)
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            Margin        = new Padding(0),
            Anchor        = AnchorStyles.Left,
            WrapContents  = false,
        };
        box.Margin = new Padding(0, 3, 0, 3);
        flow.Controls.Add(box);
        flow.Controls.Add(InfoIcon(tip));
        _tips.SetToolTip(box, WrapTip(tip));
        return flow;
    }

    private static NumericUpDown Spin(decimal min, decimal max, decimal step, int dec, decimal val) =>
        new() { Minimum = min, Maximum = max, Increment = step, DecimalPlaces = dec,
                Value = Math.Clamp(val, min, max),
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 4, 3, 4),
                Width = 150 };

    private static TableLayoutPanel MakeTable3(int col0) =>
        new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, Padding = new Padding(12), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink }
            .Also(p => { p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, col0)); p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); });

    private static TableLayoutPanel MakeTable2(int col0) =>
        new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(12), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink }
            .Also(p => { p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, col0)); p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); });

    // Wrap a content TableLayoutPanel in an AutoScroll Panel and
    // attach it to the given tab.  Fixes user-reported "rows hidden at the
    // bottom of small windows" by giving each tab a reliable vertical
    // scrollbar that activates when content exceeds the visible area.
    //
    // The table itself is now Dock=Top + AutoSize so its preferred height
    // reflects the actual sum of rows (not the tab's client area), and the
    // outer Panel's AutoScroll uses that preferred height to decide when
    // to show the scrollbar.  HScroll disabled — vertical only, as
    // intended.
    private static void AddScrollable(TabPage tab, TableLayoutPanel content)
    {
        var scroll = new Panel
        {
            Dock        = DockStyle.Fill,
            AutoScroll  = true,
            BorderStyle = BorderStyle.None,
        };
        scroll.HorizontalScroll.Enabled = false;
        scroll.HorizontalScroll.Visible = false;
        scroll.Controls.Add(content);
        tab.Controls.Add(scroll);
    }

    private static void Row3(TableLayoutPanel p, Control c0, Control c1, Control c2)
    { p.RowStyles.Add(new RowStyle(SizeType.AutoSize)); p.Controls.Add(c0); p.Controls.Add(c1); p.Controls.Add(c2); }

    private static void Row2(TableLayoutPanel p, Control c0, Control c1)
    { p.RowStyles.Add(new RowStyle(SizeType.AutoSize)); p.Controls.Add(c0); p.Controls.Add(c1); }

    private static void RowSpan2(TableLayoutPanel p, Control c)
    {
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        p.SetColumnSpan(c, 2);
        p.Controls.Add(c);
    }

    private static void SectionHeader(TableLayoutPanel p, string title)
    {
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var lbl = new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Padding = new Padding(0, 12, 0, 4) };
        p.Controls.Add(lbl); p.SetColumnSpan(lbl, 2);
    }

    private static List<string> Lines(string text) =>
        text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static void OpenExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); } catch { }
    }
}

internal static class ControlExtensions
{
    public static T Also<T>(this T self, Action<T> setup) { setup(self); return self; }
}
