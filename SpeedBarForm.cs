using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RouterSpeed;

/// <summary>A small desktop overlay. All rates supplied by the poller are bytes per second.</summary>
public sealed class SpeedBarForm : Form
{
    // Floating panel without a skin (TrafficMonitor proportions: two "label: value" rows).
    private const int LogicalWidth = 270;
    private const int LogicalHeight = 56;
    private const int FloatingPadding = 5;
    private const int DockWidth = 150;
    private const int DockHeight = 40;
    private static readonly Color Surface = Color.FromArgb(22, 27, 38);
    private static readonly Color Foreground = Color.FromArgb(237, 243, 252);
    // Taskbar text is plain bright white on the dark taskbar; the opaque fallback matches its tone.
    private static readonly Color CompactText = Color.FromArgb(242, 242, 242);
    private static readonly Color CompactFallback = Color.FromArgb(24, 31, 40);
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
    private readonly Font _floatingFont = new("Microsoft YaHei UI", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
    // One family for the whole taskbar bar keeps baselines aligned; Segoe UI Variable is the Windows 11 shell font.
    private readonly Font _compactFont = new("Segoe UI Variable Display", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _compactLabelFont = new("Segoe UI Variable Display", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Image? _skin;
    private readonly Color _skinTextColor;
    private readonly System.Windows.Forms.Timer _dockTimer = new() { Interval = 350 };
    private readonly TaskbarDockMonitor? _dockMonitor;
    private readonly TaskbarOrderObserver? _taskbarOrderObserver;
    private readonly Func<TaskbarDockSnapshot> _dockSnapshotSource;
    private readonly TaskbarPlacementPolicy _dockPlacement;
    private readonly string _preferencesPath;
    private readonly ToolStripMenuItem _floatingModeItem;
    private readonly ToolStripMenuItem _taskbarModeItem;
    private readonly ToolStripMenuItem _modeItem = new("显示模式");
    private readonly ToolStripLabel _placementItem = new();
    private readonly ToolStripMenuItem _topmostItem;
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
    private bool _taskbarMode;
    private bool _isDocked;
    private bool _userHidden;
    private bool _placementInProgress;
    private bool _started;
    private bool _floatingTopMost;
    private Point _floatingLocation;
    private string _placementDetail = string.Empty;
    // True while this form added WS_EX_LAYERED for the docked per-pixel-alpha surface. A
    // window that was already layered (e.g. Opacity in UI checks) is left alone.
    private bool _layeredByUs;

    public SpeedBarForm(Func<CancellationToken, Task<SpeedSnapshot>> poll, Action? configure = null)
        : this(poll, configure, PreferencesPath, null) { }

    // A separate preferences path and snapshot source let UI checks run without touching user state or Explorer.
    internal SpeedBarForm(Func<CancellationToken, Task<SpeedSnapshot>> poll, Action? configure,
        string preferencesPath, Func<TaskbarDockSnapshot>? dockSnapshotSource, Func<long>? placementClock = null)
    {
        _poll = poll ?? throw new ArgumentNullException(nameof(poll));
        _configure = configure;
        _preferencesPath = preferencesPath;
        _dockPlacement = new TaskbarPlacementPolicy(placementClock);
        if (dockSnapshotSource is null)
        {
            _dockMonitor = new TaskbarDockMonitor();
            _taskbarOrderObserver = new TaskbarOrderObserver(this, RepairTaskbarOrder);
        }
        _dockSnapshotSource = dockSnapshotSource ?? (() => _dockMonitor!.Current);
        Text = "RouterSpeed · 本机网速";
        AccessibleName = "本机直连和代理网速";
        AccessibleRole = AccessibleRole.Indicator;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        (_skin, _skinTextColor) = LoadSkin();
        ClientSize = FloatingLogicalSize;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Surface;
        ForeColor = Foreground;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        _preferences = ReadPreferences();
        _floatingTopMost = _preferences?.AlwaysOnTop ?? true;
        TopMost = _floatingTopMost;
        _taskbarMode = _preferences?.DisplayMode == "taskbar";
        _shortcut = _preferences is { ShortcutConfigured: true } ? _preferences.Shortcut : ShortcutDefinition.Default;

        _visibilityItem = new ToolStripMenuItem("隐藏网速条", null, (_, _) => ToggleVisibility());
        _hotkeyItem = new ToolStripMenuItem("快捷键设置…", null, (_, _) => OpenHotkeySettings());
        _startupItem = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleStartup())
        {
            AccessibleDescription = "登录当前 Windows 账户时自动显示网速条。"
        };
        _topmostItem = new ToolStripMenuItem("保持置顶") { Checked = _floatingTopMost };
        _topmostItem.Click += (_, _) =>
        {
            _floatingTopMost = !_floatingTopMost;
            if (!_isDocked) TopMost = _floatingTopMost;
            _topmostItem.Checked = _floatingTopMost;
            SavePreferences();
        };
        _floatingModeItem = new ToolStripMenuItem("悬浮模式", null, (_, _) => SetDisplayMode(false));
        _taskbarModeItem = new ToolStripMenuItem("任务栏模式", null, (_, _) => SetDisplayMode(true));
        _modeItem.DropDown.ShowItemToolTips = false;
        _modeItem.DropDownItems.AddRange([_floatingModeItem, _taskbarModeItem]);
        _dockTimer.Tick += (_, _) => RefreshTaskbarPlacement();
        _menu.ShowItemToolTips = false;
        _detailsItem.DropDown.ShowItemToolTips = false;
        _detailsItem.DropDownOpening += (_, _) => UpdateDetailValues();
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(_detailsItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_visibilityItem);
        _menu.Items.Add(_hotkeyItem);
        _menu.Items.Add(_hotkeyErrorItem);
        _menu.Items.Add(_modeItem);
        _menu.Items.Add(_placementItem);
        _menu.Items.Add(_topmostItem);
        _menu.Items.Add(_startupItem);
        _menu.Items.Add("重置悬浮位置", null, (_, _) => { ResetFloatingPosition(); ShowBar(); SavePreferences(); });
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
        ApplyDisplayMode();
        RefreshWindowRegion();
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
        if (_isDocked)
        {
            // Opaque fallback when the per-pixel-alpha surface is unavailable.
            PaintCompact(g, width, height);
            return;
        }
        PaintFloating(g, width, height);
    }

    private bool HasUnclassifiedTraffic => _snapshot.Connected && _snapshot.Status.Contains("未分类", StringComparison.Ordinal);

    // Floating mode follows the TrafficMonitor layout: a skinned panel with "label: value"
    // cells. Without a skin file the panel is a dark rounded card.
    private void PaintFloating(Graphics g, float width, float height)
    {
        Color text = Foreground;
        if (_skin is { } skin)
        {
            g.DrawImage(skin, new RectangleF(0, 0, width, height));
            text = _skinTextColor;
        }
        else
        {
            using var outline = RoundedRectangle(new RectangleF(.5f, .5f, width - 1, height - 1), 9);
            using var border = new Pen(Color.FromArgb(62, 73, 91));
            g.DrawPath(border, outline);
        }
        float rowHeight = (height - 2 * FloatingPadding) / 2;
        PaintFloatingRow(g, HasUnclassifiedTraffic ? "直连*:" : "直连:", FloatingPadding, rowHeight, _snapshot.DirectDown, _snapshot.DirectUp, text);
        PaintFloatingRow(g, HasUnclassifiedTraffic ? "代理*:" : "代理:", FloatingPadding + rowHeight, rowHeight, _snapshot.ProxyDown, _snapshot.ProxyUp, text);
        if (!_snapshot.Connected || HasUnclassifiedTraffic)
        {
            using var warning = new SolidBrush(Color.FromArgb(233, 177, 92));
            g.FillEllipse(warning, width - 9, 5, 4, 4);
        }
    }

    private void PaintFloatingRow(Graphics g, string label, float y, float height, double down, double up, Color text)
    {
        using var brush = new SolidBrush(text);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center
        };
        float middle = y + height / 2;
        g.DrawString(label, _floatingFont, brush, new RectangleF(12, y, 60, height), format);
        DrawArrow(g, brush, 62, middle, down: true);
        g.DrawString(FormatRate(down, _snapshot.Connected), _floatingFont, brush, new RectangleF(73, y, 90, height), format);
        DrawArrow(g, brush, 165, middle, down: false);
        g.DrawString(FormatRate(up, _snapshot.Connected), _floatingFont, brush, new RectangleF(176, y, 90, height), format);
    }

    // Taskbar mode: two rows of plain bright text, one letter per class, a squat triangle
    // glued to each value. Everything shares one font so baselines line up.
    private void PaintCompact(Graphics g, float width, float height)
    {
        PaintCompactRow(g, "D", 0, height / 2, _snapshot.DirectDown, _snapshot.DirectUp);
        PaintCompactRow(g, "P", height / 2, height / 2, _snapshot.ProxyDown, _snapshot.ProxyUp);
        if (!_snapshot.Connected || HasUnclassifiedTraffic)
        {
            using var warning = new SolidBrush(Color.FromArgb(233, 177, 92));
            g.FillEllipse(warning, width - 4, 2, 3, 3);
        }
    }

    private void PaintCompactRow(Graphics g, string label, float y, float height, double down, double up)
    {
        using var brush = new SolidBrush(CompactText);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center
        };
        float middle = y + height / 2;
        g.DrawString(label, _compactLabelFont, brush, new RectangleF(5, y, 14, height), format);
        DrawArrow(g, brush, 19, middle, down: true);
        g.DrawString(FormatCompactRate(down, _snapshot.Connected), _compactFont, brush, new RectangleF(28, y, 56, height), format);
        DrawArrow(g, brush, 84, middle, down: false);
        g.DrawString(FormatCompactRate(up, _snapshot.Connected), _compactFont, brush, new RectangleF(93, y, 56, height), format);
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

    /// <summary>
    /// Draws the docked bar onto a per-pixel-alpha surface so only the text sits on the
    /// taskbar material. ClearType needs an opaque background, so the text is rendered over
    /// the taskbar colour sampled next to the bar and every pixel the text touched is then
    /// made opaque; untouched pixels keep alpha 1 so the whole bar stays clickable.
    /// Returns false when the window is not ours to layer, leaving the opaque OnPaint path.
    /// </summary>
    private bool RenderLayered()
    {
        if (!_isDocked || !_layeredByUs || !IsHandleCreated || IsDisposed || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return false;
        float scale = DeviceDpi / 96f;
        int width = ClientSize.Width, height = ClientSize.Height;
        nint screen = GetDC(0);
        Color backdrop = SampleBackdrop(screen);
        using var surface = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(surface))
        {
            g.Clear(backdrop);
            g.ScaleTransform(scale, scale);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            PaintCompact(g, width / scale, height / scale);
        }
        KeyOutBackdrop(surface, backdrop);
        nint memory = CreateCompatibleDC(screen);
        nint bitmap = surface.GetHbitmap(Color.FromArgb(0));
        nint previous = SelectObject(memory, bitmap);
        try
        {
            var size = new NativeSize(width, height);
            var origin = new NativePoint(0, 0);
            var blend = new BlendFunction { BlendOp = 0, Flags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            bool updated = UpdateLayeredWindow(Handle, screen, 0, ref size, memory, ref origin, 0, ref blend, 2);
            if (!updated) StartupTrace.Write("layered-update-failed 0x" + Marshal.GetLastWin32Error().ToString("X"));
            return updated;
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    // The taskbar material is uniform enough next to the bar that one pixel just outside its
    // left edge stands in for the whole background. Falls back to the known dark taskbar tone.
    private Color SampleBackdrop(nint screen)
    {
        uint value = GetPixel(screen, Left - 2, Top + 2);
        if (value == 0xFFFFFFFF) return CompactFallback;
        return Color.FromArgb((int)(value & 0xFF), (int)((value >> 8) & 0xFF), (int)((value >> 16) & 0xFF));
    }

    private static void KeyOutBackdrop(Bitmap surface, Color backdrop)
    {
        var bounds = new Rectangle(0, 0, surface.Width, surface.Height);
        var data = surface.LockBits(bounds, System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int length = data.Stride * data.Height;
            byte[] pixels = new byte[length];
            Marshal.Copy(data.Scan0, pixels, 0, length);
            for (int offset = 0; offset < length; offset += 4)
            {
                if (pixels[offset] == backdrop.B && pixels[offset + 1] == backdrop.G && pixels[offset + 2] == backdrop.R)
                {
                    pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0;
                    pixels[offset + 3] = 1;
                }
                else pixels[offset + 3] = 255;
            }
            Marshal.Copy(pixels, 0, data.Scan0, length);
        }
        finally { surface.UnlockBits(data); }
    }

    private void Repaint()
    {
        Invalidate();
        RenderLayered();
    }

    // Docked: add WS_EX_LAYERED for the transparent surface. Floating: give it back so the
    // ordinary opaque painting resumes. Both are style bit changes; the handle survives.
    private void ApplyDockedSurface()
    {
        if (!IsHandleCreated || IsDisposed) return;
        long style = GetWindowLongPtr(Handle, -20).ToInt64();
        if (_isDocked)
        {
            if ((style & WsExLayered) == 0)
            {
                SetWindowLongPtr(Handle, -20, new nint(style | WsExLayered));
                _layeredByUs = true;
            }
            RenderLayered();
        }
        else if (_layeredByUs)
        {
            SetWindowLongPtr(Handle, -20, new nint(style & ~WsExLayered));
            _layeredByUs = false;
            Invalidate();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _layeredByUs = false;
        if (_isDocked) ApplyDockedSurface();
    }

    /// <summary>
    /// Optional TrafficMonitor-style skin: a PNG next to the executable or in the local
    /// application data folder. Its pixel size becomes the floating panel's logical size, and
    /// the text colour is chosen from the skin's brightness so dark and light skins both read.
    /// </summary>
    private static (Image? Skin, Color Text) LoadSkin()
    {
        foreach (string directory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(PreferencesPath)! })
        {
            string path = Path.Combine(directory, "skin.png");
            if (!File.Exists(path)) continue;
            try
            {
                using var stream = File.OpenRead(path);
                using var loaded = Image.FromStream(stream);
                if (loaded.Width < 120 || loaded.Height < 32 || loaded.Width > 1200 || loaded.Height > 400) continue;
                var skin = new Bitmap(loaded);
                using var probe = new Bitmap(skin, new Size(16, 4));
                double luminance = 0;
                for (int x = 0; x < 12; x++)
                    for (int y = 0; y < 4; y++)
                    {
                        Color c = probe.GetPixel(x, y);
                        luminance += (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) * c.A / 255;
                    }
                bool bright = luminance / 48 > 140;
                return (skin, bright ? Color.FromArgb(28, 28, 28) : Foreground);
            }
            catch { /* An unreadable skin means the default panel. */ }
        }
        return (null, Foreground);
    }

    private Size FloatingLogicalSize => _skin is { } skin ? skin.Size : new Size(LogicalWidth, LogicalHeight);

    internal static string FormatCompactRate(double bytesPerSecond, bool connected = true)
    {
        if (!connected || !double.IsFinite(bytesPerSecond) || bytesPerSecond < 0) return "—";
        string[] units = ["B", "K", "M", "G", "T"];
        int unit = 0;
        while (bytesPerSecond >= 1024 && unit < units.Length - 1) { bytesPerSecond /= 1024; unit++; }
        if (bytesPerSecond >= 999.5 && unit < units.Length - 1)
        {
            bytesPerSecond /= 1024;
            unit++;
        }
        if (bytesPerSecond >= 999.5) return "999T+";
        return bytesPerSecond.ToString(unit == 0 || bytesPerSecond >= 99.95 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
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
        if (e.Button != MouseButtons.Left || _isDocked) return;
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
        _floatingLocation = Location;
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

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RefreshWindowRegion();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        RefreshWindowRegion();
        Repaint();
        if (_started && !_placementInProgress && !_resourcesDisposed && !IsDisposed)
            BeginInvoke(new Action(() => { if (!IsDisposed && !_resourcesDisposed) ApplyDisplayMode(); }));
    }

    private void RefreshWindowRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var shape = RoundedRectangle(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), (_isDocked ? 3 : 10) * DeviceDpi / 96f);
        Region? previous = Region;
        Region = new Region(shape);
        previous?.Dispose();
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void ToggleVisibility()
    {
        if (!_userHidden) { _userHidden = true; Hide(); }
        else ShowBar();
    }

    private void ShowBar()
    {
        _userHidden = false;
        if (_taskbarMode) { RefreshTaskbarPlacement(); return; }
        ClampToWorkArea();
        Show();
        if (TopMost) BringToFront();
    }

    private void SetDisplayMode(bool taskbarMode)
    {
        if (_taskbarMode == taskbarMode) return;
        if (!_isDocked) _floatingLocation = Location;
        _taskbarMode = taskbarMode;
        _dockPlacement.Reset();
        _dragging = false;
        Capture = false;
        ApplyDisplayMode();
        SavePreferences();
        UpdateMenu();
    }

    private void ApplyDisplayMode()
    {
        if (_taskbarMode)
        {
            _dockMonitor?.Start();
            _taskbarOrderObserver?.Start();
            _dockTimer.Start();
            RefreshTaskbarPlacement();
        }
        else
        {
            _dockTimer.Stop();
            _taskbarOrderObserver?.Stop();
            _dockMonitor?.Stop();
            _placementDetail = string.Empty;
            RestoreFloatingPresentation();
            if (!_userHidden) Show();
        }
    }

    private void RefreshTaskbarPlacement()
    {
        if (!_taskbarMode || _resourcesDisposed || _placementInProgress || _dragging) return;
        _placementInProgress = true;
        try
        {
            _taskbarOrderObserver?.RefreshBinding();
            TaskbarDockSnapshot snapshot = _dockSnapshotSource();
            _placementDetail = snapshot.Detail;
            if (snapshot.ShouldHide)
            {
                // Automatic hiding never changes the user's explicit shortcut/menu visibility choice.
                _dockPlacement.Reset();
                if (Visible) Hide();
                if (_menu.Visible) UpdatePlacementMenu();
                return;
            }
            float scale = Math.Clamp(snapshot.Dpi, 48, 768) / 96f;
            int width = (int)Math.Round(DockWidth * scale);
            int height = Math.Min((int)Math.Round(DockHeight * scale), snapshot.AvailableArea.Height);
            Rectangle? target = null;
            if (snapshot.IsAvailable && snapshot.AvailableArea.Width >= width && height >= (int)Math.Round(30 * scale))
            {
                Rectangle available = snapshot.AvailableArea;
                target = new(available.Right - width, available.Top + (available.Height - height) / 2, width, height);
            }
            TaskbarPlacementAction action = _dockPlacement.Evaluate(snapshot, target, _isDocked, Bounds);
            if (action == TaskbarPlacementAction.Dock && target is { } dockBounds)
            {
                bool changed = !_isDocked;
                _isDocked = true;
                if (BackColor != CompactFallback) BackColor = CompactFallback;
                if (!TopMost) TopMost = true;
                bool moved = Bounds != dockBounds;
                if (moved) Bounds = dockBounds;
                if (changed) { RefreshWindowRegion(); ApplyDockedSurface(); }
                else if (moved) RenderLayered();
                if (!_userHidden && !Visible) Show();
                if (!_userHidden) TaskbarWindowOrder.KeepAboveTaskbar(Handle);
                _placementDetail = "已贴靠任务栏 · 托盘左侧";
            }
            else if (action == TaskbarPlacementAction.Float)
            {
                _placementDetail = "暂用悬浮位置：" + (snapshot.IsAvailable ? "任务栏空间不足" : snapshot.Detail);
                RestoreFloatingPresentation();
                if (!_userHidden && !Visible) Show();
            }
            else if (action == TaskbarPlacementAction.Wait)
            {
                _placementDetail = "正在等待任务栏布局稳定，暂时隐藏网速条。";
                if (Visible) Hide();
            }
            else
            {
                _placementDetail = snapshot.Covered ? snapshot.Detail
                    : _isDocked ? "任务栏布局更新中，保持已验证的位置。" : "正在等待任务栏布局稳定。";
                if (!_userHidden && !Visible) Show();
                // Do not compete with the tray popup's Z order during layout changes. A raised
                // taskbar is not a layout change: keep asking until it is back in our band.
                if (snapshot.Covered && _isDocked && !_userHidden) TaskbarWindowOrder.KeepAboveTaskbar(Handle);
            }
            if (_userHidden && Visible) Hide();
            if (_menu.Visible) UpdatePlacementMenu();
        }
        finally { _placementInProgress = false; }
    }

    private void RepairTaskbarOrder()
    {
        if (!_taskbarMode || !_isDocked || _userHidden || !Visible ||
            _resourcesDisposed || _placementInProgress || IsDisposed || !IsHandleCreated) return;
        // Current performs only bounded native reads on this thread. Do not run
        // placement, change visibility, or synchronously query UI Automation here.
        TaskbarDockSnapshot snapshot = _dockSnapshotSource();
        // A shell event while the taskbar is raised may be it returning to the desktop band.
        // Re-sample right away so the next placement pass sees the live layout, and try the
        // Z-order repair now: it succeeds only once the taskbar is enumerable again.
        if (snapshot.Covered) _dockMonitor?.RequestRefresh();
        if (!snapshot.ShouldHide && (snapshot.IsAvailable || snapshot.CanKeepPlacement) &&
            snapshot.AvailableArea.Contains(Bounds))
            TaskbarWindowOrder.KeepAboveTaskbar(Handle);
    }

    private void RestoreFloatingPresentation()
    {
        bool wasDocked = _isDocked;
        _isDocked = false;
        BackColor = Surface;
        if (TopMost != _floatingTopMost) TopMost = _floatingTopMost;
        Size logical = FloatingLogicalSize;
        Size target = new((int)Math.Round(logical.Width * DeviceDpi / 96f), (int)Math.Round(logical.Height * DeviceDpi / 96f));
        if (ClientSize != target) ClientSize = target;
        if (wasDocked || Location != _floatingLocation) Location = _floatingLocation;
        ClampToWorkArea();
        _floatingLocation = Location;
        if (wasDocked) { RefreshWindowRegion(); ApplyDockedSurface(); }
    }

    private void UpdateMenu()
    {
        _visibilityItem.Text = _userHidden ? "显示网速条" : "隐藏网速条";
        _visibilityItem.ShortcutKeyDisplayString = _hotkey.IsRegistered ? _hotkey.Current?.DisplayText ?? string.Empty : string.Empty;
        _hotkeyErrorItem.Text = _hotkey.RegistrationError ?? string.Empty;
        _hotkeyErrorItem.Visible = _hotkey.RegistrationError is not null;
        UpdateStartupMenu();
        UpdatePlacementMenu();
        UpdateDetailValues();
    }

    private void UpdatePlacementMenu()
    {
        _floatingModeItem.Checked = !_taskbarMode;
        _taskbarModeItem.Checked = _taskbarMode;
        _placementItem.Text = _isDocked ? "位置：任务栏托盘左侧" : "位置：暂用悬浮位置";
        _placementItem.Visible = _taskbarMode;
        _topmostItem.Enabled = !_taskbarMode;
        _topmostItem.Checked = _floatingTopMost;
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
            $"直连  ↓ {FormatRate(_snapshot.DirectDown, _snapshot.Connected)}   ↑ {FormatRate(_snapshot.DirectUp, _snapshot.Connected)}",
            $"代理  ↓ {FormatRate(_snapshot.ProxyDown, _snapshot.Connected)}   ↑ {FormatRate(_snapshot.ProxyUp, _snapshot.Connected)}",
            .. (_taskbarMode ? WrapMenuLine(_placementDetail) : []),
            .. _snapshot.Detail.Split('\n', StringSplitOptions.RemoveEmptyEntries).SelectMany(WrapMenuLine),
            HasUnclassifiedTraffic ? "* 仅显示已确认的直连和代理流量。" : "只统计这台 Windows 电脑的 IPv4 公网流量。",
            "↓ 下载 · ↑ 上传 · 1 KB = 1024 B",
            _isDocked ? "任务栏模式：K / M / G 为 KB/s / MB/s / GB/s；右键切换显示模式。" : "拖动可移动位置；右键打开菜单。"
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
        if (_preferences is null) ResetFloatingPosition();
        else { Location = new Point(_preferences.X, _preferences.Y); ClampToWorkArea(); }
        _floatingLocation = Location;
    }

    private void ResetFloatingPosition()
    {
        Rectangle area = (Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position)).WorkingArea;
        int margin = (int)Math.Round(12 * DeviceDpi / 96f);
        int width = (int)Math.Round(FloatingLogicalSize.Width * DeviceDpi / 96f);
        int height = (int)Math.Round(FloatingLogicalSize.Height * DeviceDpi / 96f);
        _floatingLocation = new Point(area.Right - width - margin, area.Bottom - height - margin);
        if (!_isDocked) Location = _floatingLocation;
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
            if (!_isDocked) _floatingLocation = Location;
            var settings = new UiPreferences(_floatingLocation.X, _floatingLocation.Y, _floatingTopMost, true, _shortcut,
                _taskbarMode ? "taskbar" : "floating");
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
            _dockTimer.Stop();
            _dockTimer.Dispose();
            _taskbarOrderObserver?.Dispose();
            _dockMonitor?.Dispose();
            _lifetime.Cancel();
            _tray.Visible = false;
            _tray.Dispose();
            _trayIcon?.Dispose();
            _hotkey.Dispose();
            _menu.Dispose();
            _floatingFont.Dispose();
            _compactLabelFont.Dispose();
            _skin?.Dispose();
            _compactFont.Dispose();
            // The poller may still be unwinding its cancellation; keep its token source alive
            // until then so implementations can safely register cancellation while exiting.
            if (_pollTask is null || _pollTask.IsCompleted) _lifetime.Dispose();
            else _ = _pollTask.ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
        }
        base.Dispose(disposing);
    }

    private sealed record UiPreferences(int X, int Y, bool AlwaysOnTop, bool ShortcutConfigured = false, ShortcutDefinition? Shortcut = null,
        string DisplayMode = "floating");

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    private const long WsExLayered = 0x00080000;
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize(int width, int height) { public int Width = width, Height = height; }
    [StructLayout(LayoutKind.Sequential)] private struct BlendFunction { public byte BlendOp, Flags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(nint window, nint destination, nint destinationPoint, ref NativeSize size,
        nint source, ref NativePoint sourcePoint, uint colorKey, ref BlendFunction blend, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint handle);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint handle);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
}
