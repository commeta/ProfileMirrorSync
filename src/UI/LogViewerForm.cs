using ProfileMirrorSync.Services;

namespace ProfileMirrorSync.UI;

/// <summary>
/// Real-time colour-coded log viewer (modeless).
/// Debug=grey | Info=green | Warning=yellow | Error=red
/// </summary>
public sealed class LogViewerForm : Form
{
    private readonly ListView _list;
    private readonly CheckBox _autoScroll;
    private readonly Label    _countLabel;
    private readonly Logger   _log;
    private int _total;

    private static readonly Dictionary<LogLevel, Color> LevelColors = new()
    {
        { LogLevel.Debug,   Color.FromArgb(200, 200, 200) },
        { LogLevel.Info,    Color.FromArgb(210, 240, 210) },
        { LogLevel.Warning, Color.FromArgb(255, 238, 180) },
        { LogLevel.Error,   Color.FromArgb(255, 200, 200) },
    };

    private static readonly Dictionary<LogLevel, Color> TextColors = new()
    {
        { LogLevel.Debug,   Color.FromArgb(80,  80,  80)  },
        { LogLevel.Info,    Color.FromArgb(10,  80,  10)  },
        { LogLevel.Warning, Color.FromArgb(120, 70,   0)  },
        { LogLevel.Error,   Color.FromArgb(160,  0,   0)  },
    };

    public LogViewerForm(Logger log)
    {
        _log = log;

        Text          = "Журнал событий — ProfileMirrorSync";
        Width         = 1060;
        Height        = 600;
        MinimumSize   = new Size(700, 400);
        StartPosition = FormStartPosition.CenterScreen;

        // ── Toolbar ────────────────────────────────────────────────────────────
        var toolbar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };

        var clearBtn = new ToolStripButton("🗑  Очистить");
        clearBtn.Click += (_, _) => Clear();

        var openBtn = new ToolStripButton("📂  Открыть папку логов");
        openBtn.Click += (_, _) =>
        {
            string dir = ProfileMirrorSync.Services.AppPaths.LogsDirectory;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true }); } catch { }
        };

        var autoScrollHost = new ToolStripControlHost(new CheckBox
        {
            Text = "Авто-прокрутка", Checked = true, AutoSize = true,
        });
        _autoScroll = (CheckBox)autoScrollHost.Control;

        _countLabel = new Label { AutoSize = true, Padding = new Padding(8, 3, 0, 0), ForeColor = SystemColors.GrayText };
        var countHost = new ToolStripControlHost(_countLabel);

        toolbar.Items.AddRange(new ToolStripItem[]
        {
            clearBtn, new ToolStripSeparator(), openBtn,
            new ToolStripSeparator(), autoScrollHost, new ToolStripSeparator(), countHost
        });

        // ── Legend ─────────────────────────────────────────────────────────────
        var legend = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 22, Padding = new Padding(6, 2, 0, 0), BackColor = Color.WhiteSmoke };
        foreach (var kv in LevelColors)
        {
            legend.Controls.Add(new Panel { BackColor = kv.Value, Width = 14, Height = 14, Margin = new Padding(2, 2, 2, 0) });
            legend.Controls.Add(new Label { Text = kv.Key.ToString(), AutoSize = true, Font = new Font("Segoe UI", 8f), Padding = new Padding(0, 1, 8, 0) });
        }

        // ── ListView ───────────────────────────────────────────────────────────
        _list = new ListView
        {
            Dock          = DockStyle.Fill,
            View          = View.Details,
            FullRowSelect = true,
            GridLines     = false,
            OwnerDraw     = true,
            Font          = new Font("Consolas", 9f),
        };
        _list.Columns.Add("Время",    140, HorizontalAlignment.Left);
        _list.Columns.Add("Уровень",   72, HorizontalAlignment.Left);
        _list.Columns.Add("Сообщение", 840, HorizontalAlignment.Left);

        _list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _list.DrawItem         += (_, e) => e.DrawDefault = true;
        _list.DrawSubItem      += OnDrawSubItem;

        // Resize last column when form resizes
        _list.ClientSizeChanged += (_, _) =>
        {
            if (_list.Columns.Count == 3)
                _list.Columns[2].Width = _list.ClientSize.Width - _list.Columns[0].Width - _list.Columns[1].Width - 4;
        };

        Controls.Add(_list);
        Controls.Add(legend);
        Controls.Add(toolbar);

        // Subscribe and backfill history
        _log.EntryAdded += OnEntryAdded;
        FormClosed      += (_, _) => _log.EntryAdded -= OnEntryAdded;

        _list.BeginUpdate();
        foreach (var e in _log.GetHistory())
            AppendRow(e);
        _list.EndUpdate();

        if (_list.Items.Count > 0 && _autoScroll.Checked)
            _list.EnsureVisible(_list.Items.Count - 1);
    }

    private void OnEntryAdded(LogEntry entry)
    {
        if (IsDisposed) return;
        try { BeginInvoke(() => AppendRow(entry)); } catch { }
    }

    private void AppendRow(LogEntry entry)
    {
        var item = new ListViewItem(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"))
        {
            UseItemStyleForSubItems = false,
            Tag = entry.Level,
        };
        item.SubItems.Add(entry.Level.ToString());
        string msg = entry.Message;
        if (entry.Exception is not null)
            msg += $"  ‣ {entry.Exception.GetType().Name}: {entry.Exception.Message}";
        item.SubItems.Add(msg);

        _list.Items.Add(item);
        _total++;
        _countLabel.Text = $"{_total} записей";

        // Cap memory at 5 000 visible rows
        while (_list.Items.Count > 5000)
            _list.Items.RemoveAt(0);

        if (_autoScroll.Checked)
            _list.EnsureVisible(_list.Items.Count - 1);
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item?.Tag is LogLevel level
            && LevelColors.TryGetValue(level, out Color bg)
            && TextColors.TryGetValue(level, out Color fg))
        {
            using var bgBrush = new SolidBrush(bg);
            e.Graphics.FillRectangle(bgBrush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? "", _list.Font, e.Bounds, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
        else
        {
            e.DrawDefault = true;
        }
    }

    private void Clear()
    {
        _list.Items.Clear();
        _total = 0;
        _countLabel.Text = "0 записей";
    }
}
