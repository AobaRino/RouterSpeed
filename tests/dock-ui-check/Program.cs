using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using RouterSpeed;

internal static partial class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private const int WmHotkey = 0x0312;
    private static readonly string Output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../artifacts"));
    private static readonly string Sandbox = Path.Combine(Output, "preferences-" + Guid.NewGuid().ToString("N"));
    private static readonly List<object> Results = [];
    private static readonly List<string> Failures = [];
    private static readonly SpeedSnapshot Normal = new(1258291, 3870, 19327352, 190054, "已连接 · IPv4", "本机 192.168.233.10 · 测试用合成数据", true);

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Directory.CreateDirectory(Sandbox);
        string realPrefs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RouterSpeed", "ui.json");
        string before = Fingerprint(realPrefs);
        if (args.Contains("--characterize-transient"))
        {
            CharacterizeTransientFlaps();
            return 0;
        }
        if (args.Contains("--render-only"))
        {
            Run("Menus show exclusive mode checks and no hover tooltip", MenuAndTooltip);
            Run("Disconnected, partial and extreme rates render compactly", RenderStates);
            return Failures.Count == 0 ? 0 : 1;
        }
        Run("Legacy preferences default to floating mode", LegacyPreferences);
        Run("Mode menu round trips preserve floating location and topmost", ModeRoundTrip);
        Run("Taskbar selection persists and reloads", ModePersistence);
        Run("Manual hide survives auto hide, recovery and mode switches", ExplicitHide);
        Run("Automatic hide recovers only when user visibility allows", AutomaticHide);
        Run("Insufficient taskbar space falls back and recovers", SpaceFallback);
        Run("Repeated placement and handle recreation preserve hidden intent", HandleAndRepeatedRefresh);
        Run("Menus show exclusive mode checks and no hover tooltip", MenuAndTooltip);
        Run("Actual WM_HOTKEY toggles taskbar visibility", HotkeyMessageLoop);
        Run("DPI and threshold placement remain within available bounds", DpiBounds);
        Run("Disconnected, partial and extreme rates render compactly", RenderStates);
        Run("Compact formatting bounds invalid and extreme values", Formatting);
        RunTimingChecks();
        Run("User preferences were not modified", () => Check(Fingerprint(realPrefs) == before, "Real user ui.json changed."));
        File.WriteAllText(Path.Combine(Output, "results.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{Results.Count - Failures.Count}/{Results.Count} passed; artifacts={Output}");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); Results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
        catch (Exception ex)
        {
            string reason = ex.InnerException?.Message ?? ex.Message;
            Failures.Add(name); Results.Add(new { name, passed = false, reason }); Console.WriteLine("FAIL " + name + ": " + reason);
        }
        Application.DoEvents();
    }

    private static void CharacterizeTransientFlaps()
    {
        using var h = new Harness(taskbar: true);
        using var native = new PlacementWatch(h.Bar);
        for(int i=0;i<20;i++) h.Refresh();
        int stablePositionRequests=native.PositionRequests, stableTopmostRequests=native.TopmostRequests;
        int resizes = 0, moves = 0, visibilityChanges = 0, modeChanges = 0;
        h.Bar.Resize += (_, _) => resizes++;
        h.Bar.LocationChanged += (_, _) => moves++;
        h.Bar.VisibleChanged += (_, _) => visibilityChanges++;
        bool lastDocked = Field<bool>(h.Bar,"_isDocked");
        TaskbarDockSnapshot stable = h.Snapshot;
        for (int i=0; i<20; i++)
        {
            h.Snapshot = stable with { IsAvailable=false, AvailableArea=Rectangle.Empty, Detail="测试：托盘展开期间短暂布局变化" };
            h.Refresh();
            bool current = Field<bool>(h.Bar,"_isDocked");
            if(current!=lastDocked) modeChanges++;
            lastDocked=current;
            h.Snapshot = stable; h.Refresh();
            current=Field<bool>(h.Bar,"_isDocked");
            if(current!=lastDocked) modeChanges++;
            lastDocked=current;
        }
        var report = new { cycles=20,stablePositionRequests,stableTopmostRequests,resizes,moves,visibilityChanges,modeChanges,finalDocked=lastDocked };
        string json=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});
        File.WriteAllText(Path.Combine(Output,"transient-before.json"),json);
        Console.WriteLine(json);
    }

    private sealed class PlacementWatch : NativeWindow, IDisposable
    {
        internal int PositionRequests,TopmostRequests;
        internal PlacementWatch(Form owner) => AssignHandle(owner.Handle);
        protected override void WndProc(ref Message message)
        {
            if(message.Msg == 0x0046 && message.LParam != 0)
            {
                PositionRequests++;
                WindowPosition position=Marshal.PtrToStructure<WindowPosition>(message.LParam);
                if(position.InsertAfter == new nint(-1)) TopmostRequests++;
            }
            base.WndProc(ref message);
        }
        public void Dispose() => ReleaseHandle();
        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPosition { internal nint Window,InsertAfter; internal int X,Y,Width,Height; internal uint Flags; }
    }

    private sealed class Harness : IDisposable
    {
        internal SpeedBarForm Bar { get; }
        internal TaskbarDockSnapshot Snapshot;
        internal string PreferencesPath { get; }
        internal long NowMilliseconds = 10000;
        private long _sampleSequence;
        internal Harness(string? path = null, bool taskbar = false, bool topmost = false, bool legacy = false)
        {
            PreferencesPath = path ?? Path.Combine(Sandbox, Guid.NewGuid().ToString("N") + ".json");
            if (!File.Exists(PreferencesPath))
            {
                Point origin = FloatingOrigin;
                if (legacy) Write(PreferencesPath, new { X = origin.X, Y = origin.Y, AlwaysOnTop = topmost, ShortcutConfigured = true, Shortcut = (ShortcutDefinition?)null });
                else Write(PreferencesPath, new { X = origin.X, Y = origin.Y, AlwaysOnTop = topmost, ShortcutConfigured = true, Shortcut = (ShortcutDefinition?)null, DisplayMode = taskbar ? "taskbar" : "floating" });
            }
            Snapshot = AvailableSnapshot(96);
            Bar = new SpeedBarForm(WaitForever, null, PreferencesPath, () => Snapshot, () => NowMilliseconds) { Opacity = 0 };
            Field<NotifyIcon>(Bar, "_tray").Visible = false;
            Check(!Field<GlobalHotkey>(Bar, "_hotkey").IsRegistered, "A normal test acquired a global shortcut.");
            Snapshot = AvailableSnapshot(Bar.DeviceDpi);
            Bar.Show(); Application.DoEvents();
            Field<System.Windows.Forms.Timer>(Bar, "_dockTimer").Stop();
            if(Field<bool>(Bar,"_taskbarMode")) Settle(700);
            SetField(Bar, "_snapshot", Normal); Call(Bar, "UpdatePresentation");
        }
        internal void Refresh() { Call(Bar, "RefreshTaskbarPlacement"); Application.DoEvents(); }
        internal void Step(long milliseconds, bool newSample = true)
        {
            NowMilliseconds += milliseconds;
            if(newSample) Snapshot=Snapshot with { CapturedAtUtc=DateTimeOffset.UnixEpoch.AddMilliseconds(NowMilliseconds).AddTicks(++_sampleSequence) };
            Refresh();
        }
        internal void Settle(long milliseconds=2800)
        {
            for(long elapsed=0;elapsed<milliseconds;elapsed+=350) Step(Math.Min(350,milliseconds-elapsed));
        }
        internal void Mode(bool taskbar) { Call(Bar, "SetDisplayMode", taskbar); Field<System.Windows.Forms.Timer>(Bar, "_dockTimer").Stop(); Application.DoEvents(); if(taskbar) Settle(700); }
        public void Dispose() { Bar.Dispose(); Application.DoEvents(); }
    }

    private static async Task<SpeedSnapshot> WaitForever(CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token);
        return Normal;
    }

    private static Point FloatingOrigin
    {
        get { Rectangle area = Screen.PrimaryScreen!.WorkingArea; return new(area.Left + 70, area.Top + 90); }
    }

    private static TaskbarDockSnapshot AvailableSnapshot(int dpi, int? width = null, int? height = null)
    {
        float scale = Math.Clamp(dpi, 48, 768) / 96f;
        Rectangle screen = Screen.PrimaryScreen!.Bounds;
        int h = height ?? (int)Math.Round(48 * scale);
        Rectangle taskbar = new(screen.Left, screen.Bottom - h, screen.Width, h);
        Rectangle available = new(screen.Left + 40, taskbar.Top, width ?? (int)Math.Round(300 * scale), h);
        return new(taskbar, available, dpi, false, "测试任务栏快照", true, DateTimeOffset.UtcNow, true, false);
    }

    private static void LegacyPreferences()
    {
        using var h = new Harness(legacy: true);
        Check(!Field<bool>(h.Bar, "_taskbarMode") && !Field<bool>(h.Bar, "_isDocked"), "Missing DisplayMode did not default to floating.");
        Check(h.Bar.Location == FloatingOrigin && !h.Bar.TopMost, "Legacy location/topmost was lost.");
        Check(Field<ShortcutDefinition?>(h.Bar, "_shortcut") is null, "Disabled legacy shortcut was not retained.");
    }

    private static void ModeRoundTrip()
    {
        foreach (bool topmost in new[] { false, true })
        {
            using var h = new Harness(topmost: topmost);
            Point original = h.Bar.Location;
            ToolStripMenuItem dock = Field<ToolStripMenuItem>(h.Bar, "_taskbarModeItem");
            ToolStripMenuItem floating = Field<ToolStripMenuItem>(h.Bar, "_floatingModeItem");
            for (int i = 0; i < 3; i++)
            {
                dock.PerformClick(); Field<System.Windows.Forms.Timer>(h.Bar, "_dockTimer").Stop();
                h.Settle(700);
                Check(Field<bool>(h.Bar, "_taskbarMode") && Field<bool>(h.Bar, "_isDocked"), "Taskbar menu did not select compact mode.");
                Check(h.Bar.TopMost, "Dock is not above taskbar.");
                Check(Field<Point>(h.Bar, "_floatingLocation") == original, "Docking overwrote floating location.");
                Check(!Field<ToolStripMenuItem>(h.Bar, "_topmostItem").Enabled, "Taskbar allows conflicting topmost setting.");
                floating.PerformClick();
                Check(!Field<bool>(h.Bar, "_isDocked") && h.Bar.Location == original, "Return lost original floating location.");
                Check(h.Bar.TopMost == topmost, "Return lost original floating topmost preference.");
            }
        }
    }

    private static void ModePersistence()
    {
        string path;
        Point original;
        using (var h = new Harness())
        {
            path = h.PreferencesPath; original = h.Bar.Location; h.Mode(true);
            Check((bool)Call(h.Bar, "SavePreferences")!, "Save failed.");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Check(json.RootElement.GetProperty("DisplayMode").GetString() == "taskbar", "Taskbar setting was not persisted.");
            Check(json.RootElement.GetProperty("X").GetInt32() == original.X && json.RootElement.GetProperty("Y").GetInt32() == original.Y, "Saved dock position replaced floating position.");
        }
        using var reopened = new Harness(path);
        Check(Field<bool>(reopened.Bar, "_taskbarMode") && Field<bool>(reopened.Bar, "_isDocked"), "Saved taskbar mode was not restored.");
        reopened.Mode(false);
        Check(reopened.Bar.Location == original && !reopened.Bar.TopMost, "Saved floating placement/topmost was not retained.");
    }

    private static void ExplicitHide()
    {
        using var h = new Harness(taskbar: true);
        Check(h.Bar.Visible, "Taskbar bar did not show.");
        Call(h.Bar, "ToggleVisibility");
        Check(Field<bool>(h.Bar, "_userHidden") && !h.Bar.Visible, "Manual hide did not establish hidden intent.");
        h.Snapshot = h.Snapshot with { ShouldHide = true }; h.Refresh();
        h.Snapshot = h.Snapshot with { ShouldHide = false }; h.Refresh();
        for (int i = 0; i < 4; i++) { h.Mode(i % 2 == 0); h.Refresh(); Check(!h.Bar.Visible && Field<bool>(h.Bar, "_userHidden"), "Mode/timer revived a manually hidden bar."); }
        h.Mode(true);
        h.Snapshot = h.Snapshot with { IsAvailable = false, Detail = "测试快照不可用" }; h.Refresh();
        Check(!h.Bar.Visible, "Fallback revived a hidden bar.");
        h.Snapshot = AvailableSnapshot(h.Bar.DeviceDpi); h.Refresh();
        h.Step(650);
        Check(!h.Bar.Visible, "Recovery revived a hidden bar.");
        Field<ToolStripMenuItem>(h.Bar, "_visibilityItem").PerformClick();
        Check(h.Bar.Visible && !Field<bool>(h.Bar, "_userHidden"), "Explicit show did not recover visibility.");
    }

    private static void AutomaticHide()
    {
        using var h = new Harness(taskbar: true);
        h.Snapshot = h.Snapshot with { ShouldHide = true }; h.Refresh();
        Check(!h.Bar.Visible && !Field<bool>(h.Bar, "_userHidden"), "Automatic hide incorrectly changed user visibility preference.");
        h.Snapshot = h.Snapshot with { ShouldHide = false }; h.Refresh();
        Check(h.Bar.Visible, "Automatic hide did not recover.");
        h.Snapshot = h.Snapshot with { ShouldHide = true }; h.Refresh();
        Call(h.Bar, "ToggleVisibility");
        h.Snapshot = h.Snapshot with { ShouldHide = false }; h.Refresh();
        Check(!h.Bar.Visible && Field<bool>(h.Bar, "_userHidden"), "Manual hide during auto-hide was ignored.");
    }

    private static void SpaceFallback()
    {
        using var h = new Harness();
        Point original = h.Bar.Location;
        h.Mode(true);
        h.Snapshot = AvailableSnapshot(h.Bar.DeviceDpi, 20); h.Refresh(); h.Settle();
        Check(Field<bool>(h.Bar, "_taskbarMode") && !Field<bool>(h.Bar, "_isDocked"), "Narrow taskbar did not retain selection with floating fallback.");
        Check(h.Bar.Location == original && h.Bar.Visible && !h.Bar.TopMost, "Fallback changed placement/visibility/topmost.");
        Check(Field<string>(h.Bar, "_placementDetail").Contains("空间不足"), "Fallback reason missing.");
        h.Snapshot = AvailableSnapshot(h.Bar.DeviceDpi); h.Refresh(); h.Settle(700);
        Check(Field<bool>(h.Bar, "_isDocked") && h.Bar.Visible, "Space recovery did not restore dock.");
        h.Mode(false); Check(h.Bar.Location == original, "Fallback cycle overwrote saved floating location.");
    }

    private static void HandleAndRepeatedRefresh()
    {
        using var h = new Harness(taskbar: true);
        Call(h.Bar, "ToggleVisibility");
        typeof(Control).GetMethod("RecreateHandle", PrivateInstance)!.Invoke(h.Bar, null);
        for (int i = 0; i < 20; i++)
        {
            h.Snapshot = AvailableSnapshot(h.Bar.DeviceDpi, i % 3 == 0 ? 1 : null) with { ShouldHide = i % 5 == 0 };
            h.Refresh();
            Check(!h.Bar.Visible && Field<bool>(h.Bar, "_userHidden"), "Handle/snapshot change lost manual hidden intent.");
        }
    }

    private static void MenuAndTooltip()
    {
        using var h = new Harness();
        foreach (bool dock in new[] { false, true, false })
        {
            h.Mode(dock); Call(h.Bar, "UpdateMenu");
            Check(Field<ToolStripMenuItem>(h.Bar, "_taskbarModeItem").Checked == dock && Field<ToolStripMenuItem>(h.Bar, "_floatingModeItem").Checked != dock, "Mode checks are not exclusive or accurate.");
            Check(Field<ToolStripMenuItem>(h.Bar, "_topmostItem").Enabled != dock, "Topmost menu enabled state is wrong.");
        }
        var menu = Field<ContextMenuStrip>(h.Bar, "_menu");
        Check(!menu.ShowItemToolTips && !Field<ToolStripMenuItem>(h.Bar, "_modeItem").DropDown.ShowItemToolTips && !Field<ToolStripMenuItem>(h.Bar, "_detailsItem").DropDown.ShowItemToolTips, "A menu tooltip remains enabled.");
        Check(Field<NotifyIcon>(h.Bar, "_tray").Text == "", "Tray tooltip remains enabled.");
        Check(!typeof(SpeedBarForm).GetFields(PrivateInstance).Any(field => typeof(ToolTip).IsAssignableFrom(field.FieldType)), "Bar owns a hover tooltip.");
        h.Mode(true); Call(h.Bar, "UpdateMenu");
        SaveImage(menu, "taskbar-context-menu.png");
        SaveImage(Field<ToolStripMenuItem>(h.Bar, "_modeItem").DropDown, "display-mode-menu.png");
        SaveImage(Field<ToolStripMenuItem>(h.Bar, "_detailsItem").DropDown, "taskbar-details-menu.png");
    }

    private static void HotkeyMessageLoop()
    {
        using var h = new Harness(taskbar: true);
        var hotkey = Field<GlobalHotkey>(h.Bar, "_hotkey");
        ShortcutDefinition? selected = null;
        for (Keys key = Keys.F13; key <= Keys.F24; key++)
        {
            var candidate = new ShortcutDefinition(key, true, true, true, false);
            if (hotkey.TrySet(candidate, out _)) { selected = candidate; break; }
        }
        Check(selected is not null && hotkey.IsRegistered, "No free isolated test shortcut was available.");
        try
        {
            int id = Field<int?>(hotkey, "_registeredId")!.Value;
            Check(PostMessage(h.Bar.Handle, WmHotkey, id, 0), "Post WM_HOTKEY failed."); Application.DoEvents();
            Check(!h.Bar.Visible && Field<bool>(h.Bar, "_userHidden"), "WM_HOTKEY did not hide taskbar bar."); h.Refresh();
            Check(!h.Bar.Visible, "Timer revived shortcut-hidden bar.");
            Check(PostMessage(h.Bar.Handle, WmHotkey, id, 0), "Post WM_HOTKEY restore failed."); Application.DoEvents();
            Check(h.Bar.Visible && !Field<bool>(h.Bar, "_userHidden"), "WM_HOTKEY did not restore taskbar bar.");
            PostMessage(h.Bar.Handle, WmHotkey, id + 1, 0); Application.DoEvents();
            Check(h.Bar.Visible, "Unrelated hotkey ID toggled the bar.");
        }
        finally { hotkey.TrySet(null, out _); }
        Check(!hotkey.IsRegistered, "Test hotkey leaked.");
    }

    private static void DpiBounds()
    {
        using var h = new Harness(taskbar: true);
        foreach (int dpi in new[] { 32, 48, 72, 96, 120, 144, 192, 240, 288, 480, 600, 768, 900 })
        {
            float scale = Math.Clamp(dpi, 48, 768) / 96f;
            int expectedWidth = (int)Math.Round(160 * scale);
            int minHeight = (int)Math.Round(30 * scale);
            h.Snapshot = AvailableSnapshot(dpi, expectedWidth, minHeight); h.Refresh(); h.Settle(700);
            Check(Field<bool>(h.Bar, "_isDocked"), $"Minimum accepted space rejected at DPI {dpi}.");
            Check(h.Bar.Width == expectedWidth && h.Bar.Height == minHeight, $"Size threshold wrong at DPI {dpi}.");
            Check(h.Snapshot.AvailableArea.Contains(h.Bar.Bounds), $"Dock left available bounds at DPI {dpi}.");
            h.Snapshot = AvailableSnapshot(dpi, expectedWidth - 1, minHeight); h.Refresh(); h.Settle();
            Check(!Field<bool>(h.Bar, "_isDocked"), $"One-pixel-too-narrow space accepted at DPI {dpi}.");
            h.Snapshot = AvailableSnapshot(dpi, expectedWidth, minHeight - 1); h.Refresh(); h.Settle();
            Check(!Field<bool>(h.Bar, "_isDocked"), $"Too-short space accepted at DPI {dpi}.");
        }
        h.Snapshot = AvailableSnapshot(h.Bar.DeviceDpi); h.Refresh();
    }

    private static void RenderStates()
    {
        using var h = new Harness(taskbar: true);
        var states = new[]
        {
            ("compact-normal.png", Normal),
            ("compact-partial.png", Normal with { Status = "已连接 · 部分未分类", Detail = "有流量暂时未分类，单独统计。" }),
            ("compact-disconnected.png", new SpeedSnapshot(0, 0, 0, 0, "连接中断", "正在重试读取路由器。", false)),
            ("compact-extreme.png", new SpeedSnapshot(double.MaxValue, 999.95 * 1024, 999.5, double.NaN, "已连接 · IPv4", "测试极端速率与无效值。", true))
        };
        foreach (var (name, snapshot) in states)
        {
            SetField(h.Bar, "_snapshot", snapshot); Call(h.Bar, "UpdatePresentation"); Call(h.Bar, "UpdateMenu");
            SaveImage(h.Bar, name);
            Check(h.Bar.AccessibleDescription!.Contains(snapshot.Status), "Accessible status was not updated.");
            Check(Field<ToolStripMenuItem>(h.Bar, "_detailsItem").DropDownItems.Count > 0, "Explicit details are empty.");
        }
        SetField(h.Bar, "_snapshot", Normal); Call(h.Bar, "UpdatePresentation");
        using var bitmap = new Bitmap(h.Bar.Width, h.Bar.Height);
        h.Bar.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        using var enlarged = new Bitmap(bitmap.Width * 3, bitmap.Height * 3);
        using (var graphics = Graphics.FromImage(enlarged)) { graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor; graphics.DrawImage(bitmap, new Rectangle(Point.Empty, enlarged.Size)); }
        enlarged.Save(Path.Combine(Output, "compact-normal-3x.png"));
    }

    private static void Formatting()
    {
        foreach (double value in new[] { -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Check(SpeedBarForm.FormatCompactRate(value) == "—", "Invalid compact rate was shown as traffic.");
        Check(SpeedBarForm.FormatCompactRate(100, false) == "—", "Disconnected compact rate retained old traffic.");
        foreach (double value in new[] { 0, 999, 999.5, 1023.99, 1024, 99.94 * 1024, 99.95 * 1024, 999.5 * 1024, 1e12, double.MaxValue })
            Check(SpeedBarForm.FormatCompactRate(value).Length <= 6, "Compact rate overflows its design width.");
        Check(SpeedBarForm.FormatCompactRate(double.MaxValue) == "999T+", "Extreme rates were not bounded.");
    }

    private static void SaveImage(Control control, string name)
    {
        if (control is ToolStrip strip)
        {
            strip.CreateControl(); strip.PerformLayout();
            strip.Size = strip.GetPreferredSize(Size.Empty); strip.PerformLayout();
            File.WriteAllText(Path.Combine(Output,name + ".layout.json"),JsonSerializer.Serialize(new {
                strip.Width,strip.Height,Items=strip.Items.Cast<ToolStripItem>().Select(item=>new {item.Text,item.Bounds,item.AutoSize,Preferred=item.GetPreferredSize(Size.Empty)})
            },new JsonSerializerOptions{WriteIndented=true}));
        }
        using var bitmap = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height));
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(Output, name));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value));
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, PrivateInstance)!.GetValue(instance)!;
    private static void SetField(object instance, string name, object? value) => instance.GetType().GetField(name, PrivateInstance)!.SetValue(instance, value);
    private static object? Call(object instance, string method, params object?[] args) => instance.GetType().GetMethod(method, PrivateInstance)!.Invoke(instance, args);
    private static string Fingerprint(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "absent";
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint hwnd, int message, nint wparam, nint lparam);
}
