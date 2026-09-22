using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RouterSpeed;

/// <summary>
/// A small desktop panel in the TrafficMonitor style: two "label: value" rows on a dark
/// semi-transparent plate with square corners. It can be dragged, locked in place, kept on
/// top, hidden with a shortcut, and it never touches the taskbar. All rates supplied by the
/// poller are bytes per second.
/// </summary>
public sealed class SpeedBarForm : Form
{
    private const int LogicalWidth = 270;
    private const int LogicalHeight = 56;
    private const int Inset = 5;
    // Sampled from the TrafficMonitor skin this panel imitates: a dark teal plate at 80%
    // window opacity, square corners, a hairline border and a divider between the rows.
    private static readonly Color Surface = Color.FromArgb(16, 50, 60);
    private static readonly Color Foreground = Color.FromArgb(246, 250, 252);
    private static readonly Color Edge = Color.FromArgb(46, 255, 255, 255);
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
    private readonly Font _font = new("Microsoft YaHei UI", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly string _preferencesPath;
    private readonly ToolStripMenuItem _topmostItem;
    private readonly ToolStripMenuItem _lockItem;
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
    private bool _dragging;
    private Point _dragStartCursor;
    private Point _dragStartLocation;
    private UiPreferences? _preferences;
    private Task? _pollTask;
    private bool _resourcesDisposed;
    private bool _userHidden;
    private bool _started;
    private bool _locked;
    private int _opacityPercent = DefaultOpacityPercent;

    public SpeedBarForm(Func<CancellationToken, Task<SpeedSnapshot>> poll, Action? configure = null)
        : this(poll, configure, PreferencesPath) { }

    // A separate preferences path lets UI checks run without touching user state.
    internal SpeedBarForm(Func<CancellationToken, Task<SpeedSnapshot>> poll, Action? configure, string preferencesPath)
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
        _locked = _preferences?.Locked ?? false;
        _opacityPercent = Math.Clamp(_preferences?.OpacityPercent ?? DefaultOpacityPercent, 30, 100);
        Opacity = _opacityPercent / 100d;
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
            SavePreferences();
        };
        _lockItem = new ToolStripMenuItem("锁定位置") { Checked = _locked, AccessibleDescription = "锁定后不能拖动，位置固定不变。" };
        _lockItem.Click += (_, _) =>
        {
            _locked = !_locked;
            _lockItem.Checked = _locked;
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
                Opacity = value / 100d;
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
        _menu.Items.Add(_lockItem);
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
        ContextMenuStrip = _menu;
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
        UpdatePresentation();
        _tray.Visible = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_started) return;
        _started = true;
        RestorePosition();
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
        float scale = DeviceDpi / 96f;
        g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float width = ClientSize.Width / scale;
        float height = ClientSize.Height / scale;
        using var edge = new Pen(Edge);
        g.DrawRectangle(edge, .5f, .5f, width - 1, height - 1);
        // A disconnected panel greys out completely instead of showing a warning marker.
        Color text = _snapshot.Connected ? Foreground : Muted;
        float rowHeight = (height - 2 * Inset) / 2;
        PaintRow(g, "直连:", Inset, rowHeight, _snapshot.DirectDown, _snapshot.DirectUp, text);
        g.DrawLine(edge, 8, Inset + rowHeight, width - 8, Inset + rowHeight);
        PaintRow(g, "代理:", Inset + rowHeight, rowHeight, _snapshot.ProxyDown, _snapshot.ProxyUp, text);
    }

    private void PaintRow(Graphics g, string label, float y, float height, double down, double up, Color text)
    {
        using var brush = new SolidBrush(text);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center
        };
        float middle = y + height / 2;
        // The partial-classification marker has its own slot in the left margin, so the
        // label and colon stay put when it appears or disappears.
        if (HasUnclassifiedTraffic) g.DrawString("*", _font, brush, new RectangleF(4, y, 8, height), format);
        g.DrawString(label, _font, brush, new RectangleF(12, y, 60, height), format);
        DrawArrow(g, brush, 62, middle, down: true);
        g.DrawString(FormatRate(down, _snapshot.Connected), _font, brush, new RectangleF(73, y, 90, height), format);
        DrawArrow(g, brush, 165, middle, down: false);
        g.DrawString(FormatRate(up, _snapshot.Connected), _font, brush, new RectangleF(176, y, 90, height), format);
    }

    /// <summary>A squat filled triangle (7×5 logical px) reads as up/down at small sizes better than a text arrow.</summary>
    private static void DrawArrow(Graphics g, Brush brush, float left, float middle, bool down)
    {
        const float w = 7, h = 5;
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

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _locked) return;
        _dragging = true;
        _dragStartCursor = Cursor.Position;
        _dragStartLocation = Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        Point cursor = Cursor.Position;
        Location = new Point(_dragStartLocation.X + cursor.X - _dragStartCursor.X,
            _dragStartLocation.Y + cursor.Y - _dragStartCursor.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.Button != MouseButtons.Left) return;
        _dragging = false;
        Capture = false;
        ClampToWorkArea();
        SavePreferences();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) _dragging = false;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { _userHidden = true; Hide(); return true; }
        if (keyData == Keys.Enter) { _menu.Show(this, new Point(0, Height)); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ClientSize = new Size((int)Math.Round(LogicalWidth * DeviceDpi / 96f), (int)Math.Round(LogicalHeight * DeviceDpi / 96f));
        Invalidate();
    }

    private void ToggleVisibility()
    {
        if (!_userHidden) { _userHidden = true; Hide(); }
        else ShowBar();
    }

    private void ShowBar()
    {
        _userHidden = false;
        ClampToWorkArea();
        Show();
        if (TopMost) BringToFront();
    }

    private void UpdateMenu()
    {
        _visibilityItem.Text = _userHidden ? "显示网速条" : "隐藏网速条";
        _visibilityItem.ShortcutKeyDisplayString = _hotkey.IsRegistered ? _hotkey.Current?.DisplayText ?? string.Empty : string.Empty;
        _hotkeyErrorItem.Text = _hotkey.RegistrationError ?? string.Empty;
        _hotkeyErrorItem.Visible = _hotkey.RegistrationError is not null;
        _lockItem.Checked = _locked;
        _topmostItem.Checked = TopMost;
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
            _locked ? "位置已锁定；右键菜单可解锁。" : "拖动可移动位置；右键打开菜单。"
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
        Location = new Point(area.Right - Width - margin, area.Bottom - Height - margin);
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
            var settings = new UiPreferences(Location.X, Location.Y, TopMost, true, _shortcut, _locked, _opacityPercent);
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

    // Older files also carry a DisplayMode field; unknown properties are ignored on read.
    private sealed record UiPreferences(int X, int Y, bool AlwaysOnTop, bool ShortcutConfigured = false,
        ShortcutDefinition? Shortcut = null, bool Locked = false, int OpacityPercent = DefaultOpacityPercent);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
