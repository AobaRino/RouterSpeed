using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RouterSpeed;

/// <summary>
/// A small desktop panel in the TrafficMonitor style: two "label: value" rows on a dark
/// semi-transparent, click-through plate with selectable topmost or desktop placement.
/// It can be hidden with a shortcut; all interaction lives in the tray. All rates supplied by the
/// poller are bytes per second.
/// </summary>
public sealed class SpeedBarForm : Form
{
    // Borderless, margin-free two-row panel sized to its widest value ("1023 KB/s") at 12px.
    private const int LogicalWidth = 174;
    private const int LogicalHeight = 32;
    private const int RowHeight = 16;
    private static readonly Color Surface = Color.Black;
    private static readonly Color Foreground = Color.FromArgb(246, 250, 252);
    // Fullscreen mini mode: two 1px columns in the monitor's bottom-right corner, direct on
    // the left and proxy on the right. Download is stacked from the bottom, upload above it.
    private const int MiniColumns = 2;
    private static readonly Color MiniTransparent = Color.FromArgb(255, 0, 255);
    private static readonly Color DownloadColor = Color.FromArgb(64, 220, 128);
    private static readonly Color UploadColor = Color.FromArgb(255, 160, 48);
    // Log scale up to ~1 Gbit/s, so idle chatter and a full download both stay readable.
    private const double MiniFullScale = 128d * 1024 * 1024;
    private const int DefaultOpacityPercent = 80;
    private static readonly int[] OpacityChoices = [100, 90, 80, 70, 60, 50];
    // Tray icon colours: cyan for direct, violet for proxy, grey while disconnected.
    private static readonly Color Muted = Color.FromArgb(130, 142, 161);
    private static readonly Color DirectColor = Color.FromArgb(88, 214, 215);
    private static readonly Color ProxyColor = Color.FromArgb(180, 152, 255);
    private readonly Func<CancellationToken, Task<SpeedSnapshot>> _poll;
    private readonly Action? _configure;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly NotifyIcon _tray = new();
    private readonly GlobalHotkey _hotkey;
    private readonly StartupRegistration _startup = new();
    private readonly Font _font = new("Microsoft YaHei UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly string _preferencesPath;
    private readonly PanelOrderMonitor _panelOrder;
    private readonly ToolStripMenuItem _topmostItem;
    private readonly ToolStripMenuItem _opacityItem = new("透明度");
    private readonly ToolStripMenuItem _visibilityItem;
    private readonly ToolStripLabel _statusItem = new();
    private readonly ToolStripMenuItem _detailsItem = new("实时数据与说明");
    private readonly ToolStripMenuItem _hotkeyItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripLabel _hotkeyErrorItem = new() { ForeColor = Color.DarkOrange, Visible = false };
    private ShortcutDefinition? _shortcut;
    private SpeedSnapshot _snapshot = new(0, 0, 0, 0, "正在连接", "正在从路由器读取本机的直连和代理网速。", false);
    private Icon? _trayIcon;
    private int? _iconState;
    private UiPreferences? _preferences;
    private Task? _pollTask;
    private bool _resourcesDisposed;
    private bool _userHidden;
    private bool _started;
    private int _opacityPercent = DefaultOpacityPercent;
    private bool _miniMode;
    private Point _normalLocation;
    private Rectangle _fullscreenArea;

    public SpeedBarForm(Func<CancellationToken, Task<SpeedSnapshot>> poll, Action? configure = null)
        : this(poll, configure, PreferencesPath) { }

    // A separate preferences path lets UI checks run without touching user state.
    internal SpeedBarForm(Func<CancellationToken, Task<SpeedSnapshot>> poll, Action? configure, string preferencesPath, Func<nint>? foreground = null)
    {
        _poll = poll ?? throw new ArgumentNullException(nameof(poll));
        _configure = configure;
        _preferencesPath = preferencesPath;
        Text = "RouterSpeed · 本机网速";
        AccessibleName = "本机直连和代理网速";
        AccessibleRole = AccessibleRole.Indicator;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(LogicalWidth, LogicalHeight);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Surface;
        ForeColor = Foreground;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        _preferences = ReadPreferences();
        TopMost = _preferences?.AlwaysOnTop ?? false;
        _opacityPercent = Math.Clamp(_preferences?.OpacityPercent ?? DefaultOpacityPercent, 30, 100);
        _shortcut = _preferences is { ShortcutConfigured: true } ? _preferences.Shortcut : ShortcutDefinition.Default;

        _visibilityItem = new ToolStripMenuItem("隐藏网速条", null, (_, _) => ToggleVisibility());
        _hotkeyItem = new ToolStripMenuItem("快捷键设置…", null, (_, _) => OpenHotkeySettings());
        _startupItem = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleStartup())
        {
            AccessibleDescription = "登录当前 Windows 账户时自动显示网速条。"
        };
        _topmostItem = new ToolStripMenuItem("保持置顶") { Checked = TopMost };
        _topmostItem.Click += (_, _) =>
        {
            TopMost = !TopMost;
            _topmostItem.Checked = TopMost;
            _panelOrder?.ApplyOrder();
            SavePreferences();
        };
        _opacityItem.DropDown.ShowItemToolTips = false;
        foreach (int percent in OpacityChoices)
        {
            int value = percent;
            var choice = new ToolStripMenuItem($"{value} %") { Checked = value == _opacityPercent, Tag = value };
            choice.Click += (_, _) =>
            {
                _opacityPercent = value;
                ApplyPanelOpacity();
                UpdateOpacityMenu();
                SavePreferences();
            };
            _opacityItem.DropDownItems.Add(choice);
        }
        _menu.ShowItemToolTips = false;
        _detailsItem.DropDown.ShowItemToolTips = false;
        _detailsItem.DropDownOpening += (_, _) => UpdateDetailValues();
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(_detailsItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_visibilityItem);
        _menu.Items.Add(_hotkeyItem);
        _menu.Items.Add(_hotkeyErrorItem);
        _menu.Items.Add("位置设置…", null, (_, _) => OpenPositionSettings());
        _menu.Items.Add(_topmostItem);
        _menu.Items.Add(_opacityItem);
        _menu.Items.Add(_startupItem);
        _menu.Items.Add("重置位置", null, (_, _) => { ResetPosition(); ShowBar(); SavePreferences(); });
        _menu.Items.Add(new ToolStripSeparator());
        if (_configure is not null)
            _menu.Items.Add("连接设置…", null, (_, _) => OpenConfiguration());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => Close());
        _menu.Opening += (_, _) => UpdateMenu();
        _menu.Closed += (_, _) =>
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)(() => _panelOrder?.ApplyOrder()));
        };
        _tray.ContextMenuStrip = _menu;
        _tray.DoubleClick += (_, _) => ToggleVisibility();
        // An empty NotifyIcon.Text also suppresses the native tray hover balloon.
        _tray.Text = string.Empty;
        _hotkey = new GlobalHotkey(this, () =>
        {
            // A shortcut being edited must not hide its own settings dialog.
            if (OwnedForms.Any(form => form.Visible)) return;
            _menu.Close();
            ToggleVisibility();
        });
        _hotkey.TrySet(_shortcut, out _);
        _panelOrder = new PanelOrderMonitor(this, () => !_started || _menu.Visible || OwnedForms.Any(form => form.Visible), foreground, UpdateFullscreenLayout);
        UpdatePresentation();
        _tray.Visible = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // LAYERED + TRANSPARENT passes input through across processes, including
            // at 100% opacity. NOACTIVATE prevents taking keyboard focus from a game.
            cp.ExStyle |= 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyPanelOpacity();
    }

    private void ApplyPanelOpacity()
    {
        if (!IsHandleCreated) return;
        // Mini mode keys out the background colour (LWA_COLORKEY | LWA_ALPHA) so only the
        // coloured bar pixels are drawn over the fullscreen app, at full strength.
        if (_miniMode)
            SetLayeredWindowAttributes(Handle, (uint)ColorTranslator.ToWin32(MiniTransparent), 255, 3);
        else
            SetLayeredWindowAttributes(Handle, 0, (byte)Math.Round(255 * _opacityPercent / 100d), 2);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_started) return;
        _started = true;
        RestorePosition();
        _panelOrder.ApplyOrder();
        _pollTask ??= PollContinuouslyAsync(_lifetime.Token);
    }

    private async Task PollContinuouslyAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SpeedSnapshot current;
                try
                {
                    current = await _poll(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // Never expose network exceptions, URLs, credentials, or response bodies in the UI.
                    current = new(0, 0, 0, 0, "连接中断", "暂时无法读取路由器数据。请检查网络和连接设置；程序会自动重试。", false);
                }

                if (cancellationToken.IsCancellationRequested || IsDisposed)
                    return;
                _snapshot = current;
                UpdatePresentation();
                Invalidate();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown, including cancellation during the refresh delay.
        }
    }

    private void UpdatePresentation()
    {
        string direct = $"直连 ↓ {FormatRate(_snapshot.DirectDown, _snapshot.Connected)}  ↑ {FormatRate(_snapshot.DirectUp, _snapshot.Connected)}";
        string proxy = $"代理 ↓ {FormatRate(_snapshot.ProxyDown, _snapshot.Connected)}  ↑ {FormatRate(_snapshot.ProxyUp, _snapshot.Connected)}";
        AccessibleDescription = $"{_snapshot.Status}。{direct}。{proxy}。{_snapshot.Detail}";
        _statusItem.Text = _snapshot.Status;
        // Keep an explicitly opened menu current without rebuilding it during navigation.
        if (_detailsItem.DropDown.Visible) UpdateDetailValues();
        int iconState = !_snapshot.Connected ? 0 : HasUnclassifiedTraffic ? 2 : 1;
        if (_iconState != iconState)
        {
            Icon replacement = CreateTrayIcon(_snapshot.Connected, HasUnclassifiedTraffic);
            _tray.Icon = replacement;
            _trayIcon?.Dispose();
            _trayIcon = replacement;
            _iconState = iconState;
        }
    }

    private bool HasUnclassifiedTraffic => _snapshot.Connected && _snapshot.Status.Contains("未分类", StringComparison.Ordinal);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        if (_miniMode)
        {
            PaintMiniColumn(g, 0, _snapshot.DirectDown, _snapshot.DirectUp);
            PaintMiniColumn(g, 1, _snapshot.ProxyDown, _snapshot.ProxyUp);
            return;
        }
        float scale = DeviceDpi / 96f;
        g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        // A disconnected panel greys out completely instead of showing a warning marker.
        Color text = _snapshot.Connected ? Foreground : Muted;
        PaintRow(g, "直连:", 0, _snapshot.DirectDown, _snapshot.DirectUp, text);
        PaintRow(g, "代理:", RowHeight, _snapshot.ProxyDown, _snapshot.ProxyUp, text);
    }

    private void PaintRow(Graphics g, string label, float y, double down, double up, Color text)
    {
        using var brush = new SolidBrush(text);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center
        };
        float middle = y + RowHeight / 2f;
        // The partial-classification marker has its own 5px slot, so the label and colon
        // stay put when it appears or disappears.
        if (HasUnclassifiedTraffic) g.DrawString("*", _font, brush, new RectangleF(0, y, 5, RowHeight), format);
        g.DrawString(label, _font, brush, new RectangleF(5, y, 30, RowHeight), format);
        DrawArrow(g, brush, 35, middle, down: true);
        g.DrawString(FormatRate(down, _snapshot.Connected), _font, brush, new RectangleF(43, y, 62, RowHeight), format);
        DrawArrow(g, brush, 107, middle, down: false);
        g.DrawString(FormatRate(up, _snapshot.Connected), _font, brush, new RectangleF(115, y, 62, RowHeight), format);
    }

    // Drawn in physical pixels: one crisp 1px column per class, download from the bottom
    // and upload stacked above it, each getting up to half of the column.
    private void PaintMiniColumn(Graphics g, int x, double down, double up)
    {
        if (!_snapshot.Connected) return;
        int length = ClientSize.Height;
        int downPixels = MiniBarPixels(down, length / 2), upPixels = MiniBarPixels(up, length / 2);
        using var downBrush = new SolidBrush(DownloadColor);
        using var upBrush = new SolidBrush(UploadColor);
        g.FillRectangle(downBrush, x, length - downPixels, 1, downPixels);
        g.FillRectangle(upBrush, x, length - downPixels - upPixels, 1, upPixels);
    }

    internal static int MiniBarPixels(double bytesPerSecond, int maximum)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0 || maximum <= 0) return 0;
        double fraction = Math.Log2(1 + bytesPerSecond / 1024) / Math.Log2(1 + MiniFullScale / 1024);
        return (int)Math.Round(Math.Clamp(fraction, 0, 1) * maximum);
    }

    /// <summary>A squat filled triangle (6×4 logical px) reads as up/down at small sizes better than a text arrow.</summary>
    private static void DrawArrow(Graphics g, Brush brush, float left, float middle, bool down)
    {
        const float w = 6, h = 4;
        float top = middle - h / 2, bottom = middle + h / 2;
        PointF[] points = down
            ? [new(left, top), new(left + w, top), new(left + w / 2, bottom)]
            : [new(left, bottom), new(left + w, bottom), new(left + w / 2, top)];
        g.FillPolygon(brush, points);
    }

    internal static string FormatRate(double bytesPerSecond, bool connected = true)
    {
        if (!connected || !double.IsFinite(bytesPerSecond) || bytesPerSecond < 0)
            return "—";
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];
        int unit = 0;
        while (bytesPerSecond >= 1024 && unit < units.Length - 1)
        {
            bytesPerSecond /= 1024;
            unit++;
        }
        string number = bytesPerSecond.ToString(unit == 0 || bytesPerSecond >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture);
        return $"{number} {units[unit]}";
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyPanelSize();
        if (_miniMode) PositionMiniPanel();
        Invalidate();
    }

    // The mini columns are literal 1px wide and a quarter of the fullscreen monitor tall.
    private void ApplyPanelSize() => ClientSize = _miniMode
        ? new Size(MiniColumns, Math.Max(2, _fullscreenArea.Height / 4))
        : new Size((int)Math.Round(LogicalWidth * DeviceDpi / 96f), (int)Math.Round(LogicalHeight * DeviceDpi / 96f));

    internal void UpdateFullscreenLayout(nint foreground)
    {
        if (TopMost && foreground == 0) return;
        Rectangle screen = Rectangle.Empty;
        bool mini = TopMost && FullscreenWindow.TryGetScreen(foreground, out screen);
        if (mini)
        {
            if (!_miniMode || screen != _fullscreenArea)
            {
                if (!_miniMode) _normalLocation = Location;
                _fullscreenArea = screen;
                SetMiniMode(true);
            }
            PositionMiniPanel();
        }
        else if (_miniMode)
        {
            SetMiniMode(false);
            Location = _normalLocation;
            ClampToWorkArea();
        }
    }

    private void SetMiniMode(bool mini)
    {
        _miniMode = mini;
        BackColor = mini ? MiniTransparent : Surface;
        ApplyPanelSize();
        ApplyPanelOpacity();
        Invalidate();
    }

    private void PositionMiniPanel()
    {
        Point point = new(_fullscreenArea.Right - Width, _fullscreenArea.Bottom - Height);
        if (Location != point) Location = point;
    }

    private void ToggleVisibility()
    {
        if (!_userHidden) { _userHidden = true; Hide(); }
        else ShowBar();
    }

    private void ShowBar()
    {
        _userHidden = false;
        if (!_miniMode) ClampToWorkArea();
        Show();
        _panelOrder.ApplyOrder();
    }

    private void UpdateMenu()
    {
        _topmostItem.Checked = TopMost;
        _visibilityItem.Text = _userHidden ? "显示网速条" : "隐藏网速条";
        _visibilityItem.ShortcutKeyDisplayString = _hotkey.IsRegistered ? _hotkey.Current?.DisplayText ?? string.Empty : string.Empty;
        _hotkeyErrorItem.Text = _hotkey.RegistrationError ?? string.Empty;
        _hotkeyErrorItem.Visible = _hotkey.RegistrationError is not null;
        UpdateOpacityMenu();
        UpdateStartupMenu();
        UpdateDetailValues();
    }

    private void UpdateOpacityMenu()
    {
        _opacityItem.Text = $"透明度：{_opacityPercent} %";
        foreach (ToolStripItem item in _opacityItem.DropDownItems)
            if (item is ToolStripMenuItem choice && choice.Tag is int value) choice.Checked = value == _opacityPercent;
    }

    private void UpdateStartupMenu()
    {
        bool readable = _startup.TryGetEnabled(out bool enabled, out _);
        _startupItem.CheckState = readable ? enabled ? CheckState.Checked : CheckState.Unchecked : CheckState.Indeterminate;
        _startupItem.Text = readable ? "开机自启" : "开机自启（读取失败）";
    }

    private void ToggleStartup()
    {
        if (!_startup.TryGetEnabled(out bool enabled, out string? error) ||
            !_startup.TrySetEnabled(!enabled, out error))
        {
            MessageBox.Show(this, error ?? "暂时无法修改开机自启，请稍后重试。", "RouterSpeed · 开机自启",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateStartupMenu();
    }

    private void UpdateDetailValues()
    {
        string[] lines = [
            $"直连  ▼ {FormatRate(_snapshot.DirectDown, _snapshot.Connected)}   ▲ {FormatRate(_snapshot.DirectUp, _snapshot.Connected)}",
            $"代理  ▼ {FormatRate(_snapshot.ProxyDown, _snapshot.Connected)}   ▲ {FormatRate(_snapshot.ProxyUp, _snapshot.Connected)}",
            .. _snapshot.Detail.Split('\n', StringSplitOptions.RemoveEmptyEntries).SelectMany(WrapMenuLine),
            HasUnclassifiedTraffic ? "* 仅显示已确认的直连和代理流量。" : "只统计这台 Windows 电脑的 IPv4 公网流量。",
            "▼ 下载 · ▲ 上传 · 1 KB = 1024 B",
            "浮窗点击穿透；右键托盘图标打开菜单。",
            TopMost ? "保持置顶已开启。" : "浮窗置底，其他应用窗口可以覆盖它。",
            _miniMode ? "全屏迷你模式：右下角两条 1px 竖条，左直连、右代理；绿色下载、橙色上传。" : "置顶时遇到全屏窗口自动缩成右下角两条竖线，退出后恢复。",
            "移动浮窗请使用托盘菜单的“位置设置”。"
        ];
        var items = _detailsItem.DropDownItems;
        while (items.Count > lines.Length) { var last = items[^1]; items.Remove(last); last.Dispose(); }
        while (items.Count < lines.Length) items.Add(new ToolStripLabel());
        for (int i = 0; i < lines.Length; i++) items[i].Text = lines[i];
    }

    private static IEnumerable<string> WrapMenuLine(string line)
    {
        const int width = 45;
        for (int i = 0; i < line.Length; i += width)
            yield return line.Substring(i, Math.Min(width, line.Length - i));
    }

    private void OpenHotkeySettings()
    {
        using var dialog = new HotkeySettingsForm(_shortcut, candidate =>
        {
            ShortcutDefinition? previousRegistration = _hotkey.Current;
            if (!_hotkey.TrySet(candidate, out string? error)) return error;
            ShortcutDefinition? previous = _shortcut;
            _shortcut = candidate;
            if (!SavePreferences())
            {
                _shortcut = previous;
                bool restored = _hotkey.TrySet(previousRegistration, out string? restoreError);
                return "快捷键设置无法保存，请检查当前用户的数据目录是否可写后重试。" +
                    (restored ? "" : $"\n{restoreError}");
            }
            UpdateMenu();
            return null;
        });
        dialog.ShowDialog(this);
    }

    private void OpenConfiguration()
    {
        try { _configure?.Invoke(); }
        catch
        {
            MessageBox.Show(this, "暂时无法打开连接设置，请退出后重试。", "RouterSpeed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenPositionSettings()
    {
        using var dialog = new PanelPositionForm(_miniMode ? _normalLocation : Location);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (_miniMode) _normalLocation = dialog.PanelLocation;
        else { Location = dialog.PanelLocation; ClampToWorkArea(); }
        _panelOrder.ApplyOrder();
        SavePreferences();
    }

    private void RestorePosition()
    {
        if (_preferences is null) ResetPosition();
        else { Location = new Point(_preferences.X, _preferences.Y); ClampToWorkArea(); }
    }

    // Bottom-right corner of the primary work area, just above the taskbar, like TrafficMonitor.
    private void ResetPosition()
    {
        Rectangle area = (Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position)).WorkingArea;
        int margin = (int)Math.Round(12 * DeviceDpi / 96f);
        var point = new Point(area.Right - (int)Math.Round(LogicalWidth * DeviceDpi / 96f) - margin,
            area.Bottom - (int)Math.Round(LogicalHeight * DeviceDpi / 96f) - margin);
        if (_miniMode) _normalLocation = point;
        else Location = point;
    }

    private void ClampToWorkArea()
    {
        Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    private static string PreferencesPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RouterSpeed", "ui.json");

    private UiPreferences? ReadPreferences()
    {
        try { return File.Exists(_preferencesPath) ? JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(_preferencesPath)) : null; }
        catch { return null; }
    }

    private bool SavePreferences()
    {
        try
        {
            Point location = _miniMode ? _normalLocation : Location;
            var settings = new UiPreferences(location.X, location.Y, TopMost, true, _shortcut, _opacityPercent);
            string file = _preferencesPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(settings));
            File.Move(file + ".tmp", file, overwrite: true);
            _preferences = settings;
            return true;
        }
        catch { return false; /* Placement preferences must never interrupt monitoring. */ }
    }

    private static Icon CreateTrayIcon(bool connected, bool partial)
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var downPen = new Pen(connected ? DirectColor : Muted, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var upPen = new Pen(connected && !partial ? ProxyColor : Color.FromArgb(233, 177, 92), 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLine(downPen, 9, 5, 9, 26);
            g.DrawLines(downPen, [new PointF(3, 20), new PointF(9, 26), new PointF(15, 20)]);
            g.DrawLine(upPen, 23, 26, 23, 5);
            g.DrawLines(upPen, [new PointF(17, 11), new PointF(23, 5), new PointF(29, 11)]);
        }
        nint handle = bitmap.GetHicon();
        try { using Icon borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SavePreferences();
        base.OnFormClosing(e);
        if (!e.Cancel) _lifetime.Cancel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            _panelOrder?.Dispose();
            _lifetime.Cancel();
            _tray.Visible = false;
            _tray.Dispose();
            _trayIcon?.Dispose();
            _hotkey.Dispose();
            _menu.Dispose();
            _font.Dispose();
            // The poller may still be unwinding its cancellation; keep its token source alive
            // until then so implementations can safely register cancellation while exiting.
            if (_pollTask is null || _pollTask.IsCompleted) _lifetime.Dispose();
            else _ = _pollTask.ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
        }
        base.Dispose(disposing);
    }

    // Older DisplayMode and Locked properties are ignored; position and shortcuts survive.
    private sealed record UiPreferences(int X, int Y, bool AlwaysOnTop, bool ShortcutConfigured = false,
        ShortcutDefinition? Shortcut = null, int OpacityPercent = DefaultOpacityPercent);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
