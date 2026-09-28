using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using RouterSpeed;

internal static class OverlayUiCheck
{
    private static readonly List<string> Passed = [];
    [STAThread]
    private static void Main(string[] args)
    {
        if (args[0] == "--isolated") { RunOnPrivateDesktop(args[1]); return; }
        ApplicationConfiguration.Initialize();
        if (args[0] == "--peer")
        {
            using var peer = new Peer { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(40, 40, 320, 130), TopMost = true, Opacity = 0.01, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None };
            peer.Shown += (_, _) => File.WriteAllText(args[1], peer.Handle.ToInt64().ToString());
            Application.Run(peer);
            return;
        }
        string folder = args[0];
        Directory.CreateDirectory(folder);
        Process? child = null;
        try
        {
            string handleFile = Path.Combine(folder, "peer.txt");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("--peer"); start.ArgumentList.Add(handleFile);
            child = Process.Start(start)!;
            Wait(() => File.Exists(handleFile), 5000);
            nint peer = (nint)long.Parse(File.ReadAllText(handleFile));
            nint foreground = GetForegroundWindow();
            string preferences = Path.Combine(folder, "ui.json");
            File.WriteAllText(preferences, "{\"X\":40,\"Y\":40,\"AlwaysOnTop\":true,\"Locked\":false,\"ShortcutConfigured\":true,\"Shortcut\":null,\"OpacityPercent\":80}");
            using var bar = new SpeedBarForm(_ => Task.FromResult(new SpeedSnapshot(1, 2, 3, 4, "已连接", "测试", true)), null, preferences);
            Field<PanelOrderMonitor>(bar, "_panelOrder").Dispose();
            bar.Show();
            Application.DoEvents();
            bool suspended = false;
            using var monitor = new PanelOrderMonitor(bar, () => suspended, () => peer, bar.UpdateFullscreenLayout);
            // Force the event path to prove recovery does not depend on the fallback timer.
            Field<System.Windows.Forms.Timer>(monitor, "_fallback").Stop();
            Check(bar.ContextMenuStrip is null, "Panel has no context menu");
            var menu = Field<ContextMenuStrip>(bar, "_menu");
            Check(ReferenceEquals(Field<NotifyIcon>(bar, "_tray").ContextMenuStrip, menu), "Tray retains its menu");
            Check(bar.Location == new Point(40, 40), "Legacy position survives migration");
            Check(menu.Items.Cast<ToolStripItem>().Any(x => x.Text == "位置设置…"), "Position control remains available in tray");
            var opacity = Field<ToolStripMenuItem>(bar, "_opacityItem");
            foreach (ToolStripMenuItem choice in opacity.DropDownItems)
            {
                choice.PerformClick();
                SetWindowPos(bar.Handle, -1, 0, 0, 0, 0, 0x13);
                Application.DoEvents();
                long style = GetWindowLongPtrW(bar.Handle, -20).ToInt64();
                Check((style & 0x08080020) == 0x08080020, $"Native click-through and no-activate styles at {choice.Tag}%");
                Check(GetLayeredWindowAttributes(bar.Handle, out _, out byte alpha, out _) && alpha == (byte)Math.Round(255 * (int)choice.Tag! / 100d), $"Native alpha correct at {choice.Tag}%");
                nint hit = WindowFromPoint(new Point(80, 65));
                GetWindowRect(peer, out var peerRect);
                GetWindowRect(bar.Handle, out var barRect);
                var hitClass = new System.Text.StringBuilder(256);
                GetClassName(hit, hitClass, hitClass.Capacity);
                Check(hit == peer, $"Hit testing passes to another process at {choice.Tag}% (hit={hitClass}, peerRect={peerRect}, panelRect={barRect}, peerAbovePanel={IsAbove(peer, bar.Handle)})");
            }
            Rectangle bounds = bar.Bounds;
            for (int i = 0; i < 8; i++)
            {
                Check(MovePeer(peer, -1) != 0, $"Peer requests topmost order #{i}");
                Wait(() => IsAbove(bar.Handle, peer), 1500);
            }
            Check(bar.Bounds == bounds && bar.Visible, "Recovery preserves position, size and visibility");
            Check(GetForegroundWindow() == foreground, "Showing and raising panel never steals foreground focus");
            Rectangle normal = bar.Bounds;
            SendMessage(peer, 0x8042, 1, 0);
            monitor.ApplyOrder();
            Rectangle screen = Screen.FromHandle(peer).Bounds;
            Check(Field<bool>(bar, "_miniMode"), "Fullscreen foreground selects mini mode");
            Check(normal.Size == new Size((int)Math.Round(168 * bar.DeviceDpi / 96f), (int)Math.Round(32 * bar.DeviceDpi / 96f)), $"Normal panel is the compact 168x32 logical size (got {normal.Size})");
            Check(bar.Right == screen.Right && bar.Bottom == screen.Bottom, "Mini bars sit in the bottom-right corner of the fullscreen monitor");
            Check(bar.Width == (int)Math.Round(4 * bar.DeviceDpi / 96f) && bar.Height == screen.Height / 4, $"Mini strip is 4 logical px wide and a quarter of the screen tall (got {bar.Size})");
            Check(GetLayeredWindowAttributes(bar.Handle, out _, out byte miniAlpha, out uint miniFlags) && miniFlags == 2 && miniAlpha == (byte)Math.Round(255 * Field<int>(bar, "_opacityPercent") / 100d) && bar.BackColor == Color.Black,
                "Mini strip is black and follows the tray opacity setting");
            using (var bitmap = new Bitmap(bar.Width, bar.Height))
            { bar.DrawToBitmap(bitmap, bar.ClientRectangle); bitmap.Save(Path.Combine(folder, "mini.png")); }
            typeof(SpeedBarForm).GetMethod("SavePreferences", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bar, null);
            using (var miniSaved = JsonDocument.Parse(File.ReadAllText(preferences)))
                Check(miniSaved.RootElement.GetProperty("X").GetInt32() == normal.X && miniSaved.RootElement.GetProperty("Y").GetInt32() == normal.Y,
                    "Saving in mini mode preserves the normal position");
            SendMessage(peer, 0x8042, 0, 0);
            monitor.ApplyOrder();
            Check(!Field<bool>(bar, "_miniMode") && bar.Bounds == normal, "Leaving fullscreen restores original size and position");
            Check(GetLayeredWindowAttributes(bar.Handle, out _, out _, out uint normalFlags) && normalFlags == 2 && bar.BackColor == Color.Black,
                "Leaving fullscreen restores the black, uniformly translucent panel");
            typeof(SpeedBarForm).GetMethod("ResetPosition", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bar, null);
            Rectangle work = (Screen.PrimaryScreen ?? Screen.FromHandle(bar.Handle)).WorkingArea;
            Check(bar.Right == work.Right && bar.Bottom == work.Bottom, "Reset position is flush with the bottom-right corner");
            bar.Location = normal.Location;
            var area1440 = new Rectangle(0, 0, 2560, 1392);
            var compact = new Size(168, 32);
            Check(SpeedBarForm.StickToEdges(new Rectangle(2290, 1336, 270, 56), compact, area1440, 12) == new Point(2392, 1360), "A corner-flush panel stays in the corner after shrinking");
            Check(SpeedBarForm.StickToEdges(new Rectangle(2278, 1324, 270, 56), compact, area1440, 12) == new Point(2392, 1360), "The old 12px reset margin snaps to the corner");
            Check(SpeedBarForm.StickToEdges(new Rectangle(1000, 500, 270, 56), compact, area1440, 12) == new Point(1000, 500), "A free-floating position is kept as saved");
            Check(!FullscreenWindow.CoversScreen(new Rectangle(0, 0, 1920, 1040), new Rectangle(0, 0, 1920, 1080)), "Taskbar-sized gap is not fullscreen");
            Check(FullscreenWindow.CoversScreen(new Rectangle(-1920, -200, 1920, 1080), new Rectangle(-1920, -200, 1920, 1080)), "Fullscreen geometry supports negative monitor coordinates");
            // Under an auto-hidden taskbar a maximized window spans the monitor plus its invisible
            // border. Windows clamps test windows to the real work area, so check the geometry directly.
            var autoHideMaximized = Rectangle.FromLTRB(-8, -8, 1928, 1088);
            var monitor1080 = new Rectangle(0, 0, 1920, 1080);
            Check(FullscreenWindow.CoversScreen(autoHideMaximized, monitor1080), "Auto-hide maximized geometry alone looks fullscreen");
            Check(!FullscreenWindow.IsFullscreen(autoHideMaximized, monitor1080, maximizedWithCaption: true), "A maximized window with a title bar is not fullscreen");
            Check(FullscreenWindow.IsFullscreen(monitor1080, monitor1080, maximizedWithCaption: false), "A borderless monitor-sized window is fullscreen");
            SendMessage(peer, 0x8043, 1, 0);
            Check(!FullscreenWindow.TryGetScreen(peer, out _), "A live maximized window with a title bar is not fullscreen");
            SendMessage(peer, 0x8043, 0, 0);
            SendMessage(peer, 0x8042, 1, 0);
            Check(FullscreenWindow.TryGetScreen(peer, out _), "A borderless screen-sized window is fullscreen");
            SendMessage(peer, 0x8042, 0, 0);
            Check(SpeedBarForm.MiniBarPixels(0, 100) == 0 && SpeedBarForm.MiniBarPixels(128d * 1024 * 1024, 100) == 100 &&
                SpeedBarForm.MiniBarPixels(1024 * 1024, 100) is > 50 and < 70, "Mini bar length is logarithmic up to 1 Gbit/s");
            // Your screenshot: direct 113 MB/s + 1.2 MB/s, proxy 4.9 + 4.8 KB/s, on a 360px strip.
            var (shotDirect, shotProxy) = SpeedBarForm.MiniSegments(114.2 * 1024 * 1024, 9.7 * 1024, 360);
            Check(shotProxy == 2 && shotDirect + shotProxy == SpeedBarForm.MiniBarPixels(114.2 * 1024 * 1024 + 9.7 * 1024, 360), "A dwarfed class keeps 2px without changing the total length");
            var (evenDirect, evenProxy) = SpeedBarForm.MiniSegments(1024 * 1024, 3 * 1024 * 1024, 360);
            Check(Math.Abs(evenProxy - 3 * evenDirect) <= 2, $"Direct and proxy split the length by their real share (got {evenDirect}:{evenProxy})");
            Check(SpeedBarForm.MiniSegments(0, 0, 360) == (0, 0) && SpeedBarForm.MiniSegments(double.NaN, -5, 360) == (0, 0), "No or invalid traffic draws nothing");
            suspended = true;
            using var positionSpy = new PositionSpy(bar.Handle);
            Check(MovePeer(peer, -1) != 0, "Peer can reorder while monitor is suspended");
            monitor.ApplyOrder();
            Pump(100);
            Check(positionSpy.Changes == 0, "Menus and dialogs suspend all panel placement requests");
            suspended = false;
            var topmost = Field<ToolStripMenuItem>(bar, "_topmostItem");
            topmost.PerformClick();
            monitor.ApplyOrder();
            Check(!bar.TopMost && IsAbove(peer, bar.Handle), "Disabling topmost sends the panel behind other windows");
            SendMessage(peer, 0x8042, 1, 0);
            monitor.ApplyOrder();
            Check(!Field<bool>(bar, "_miniMode") && bar.Bounds == normal, "Background mode never selects fullscreen mini mode");
            SendMessage(peer, 0x8042, 0, 0);
            MovePeer(peer, -2);
            monitor.ApplyOrder();
            Check(IsAbove(peer, bar.Handle), "Normal non-topmost window covers background panel");
            for (int i = 0; i < 4; i++)
            {
                MovePeer(peer, bar.Handle);
                Wait(() => IsAbove(peer, bar.Handle), 1500);
            }
            Check(IsAbove(peer, bar.Handle), "Background order recovers after window reorder events");
            topmost.PerformClick();
            monitor.ApplyOrder();
            Check(bar.TopMost && IsAbove(bar.Handle, peer), "Tray toggle returns the panel to topmost");
            topmost.PerformClick();
            monitor.ApplyOrder();
            bar.Hide();
            monitor.ApplyOrder();
            Check(!bar.Visible, "Manual hide is respected");
            bar.Show();
            typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bar, null);
            Check((GetWindowLongPtrW(bar.Handle, -20).ToInt64() & 0x08080020) == 0x08080020, "Handle recreation preserves click-through");
            monitor.Dispose();
            MovePeer(peer, bar.Handle);
            Pump(100);
            Check(IsAbove(bar.Handle, peer), "Disposed monitor cannot reorder the panel");
            bar.Close();
            var saved = JsonDocument.Parse(File.ReadAllText(preferences));
            Check(saved.RootElement.GetProperty("Shortcut").ValueKind == JsonValueKind.Null, "Migration preserves disabled shortcut");
            Check(!saved.RootElement.GetProperty("AlwaysOnTop").GetBoolean(), "Selected background mode is persisted");
            SendMessage(peer, 0x8042, 1, 0);
            File.WriteAllText(preferences, "{\"X\":40,\"Y\":40,\"AlwaysOnTop\":true,\"ShortcutConfigured\":true,\"Shortcut\":null}");
            using (var startupBar = new SpeedBarForm(_ => Task.FromResult(new SpeedSnapshot(1000, 2000, 3000, 4000, "已连接", "测试", true)), null, preferences, () => peer))
            {
                startupBar.Show();
                Wait(() => Field<bool>(startupBar, "_miniMode"), 1500);
                Check(Field<bool>(startupBar, "_miniMode"), "Starting during fullscreen immediately selects mini mode");
                Check(Field<Point>(startupBar, "_normalLocation") == new Point(40, 40), "Fullscreen startup preserves saved normal location");
                using (var bars = new Bitmap(startupBar.Width, startupBar.Height))
                {
                    startupBar.DrawToBitmap(bars, startupBar.ClientRectangle);
                    bars.Save(Path.Combine(folder, "mini-bars.png"));
                    int bottom = bars.Height - 1;
                    int cyan = Color.FromArgb(88, 214, 215).ToArgb(), violet = Color.FromArgb(180, 152, 255).ToArgb(), black = Color.Black.ToArgb();
                    // Direct 3000 B/s and proxy 7000 B/s: direct fills the bottom, proxy sits above it.
                    var (expectDirect, expectProxy) = SpeedBarForm.MiniSegments(3000, 7000, bars.Height);
                    Check(Enumerable.Range(0, bars.Width).All(x => bars.GetPixel(x, bottom).ToArgb() == cyan), "Direct fills the full 4px width from the bottom edge");
                    Check(bars.GetPixel(0, bottom - expectDirect).ToArgb() == violet && bars.GetPixel(0, bottom - expectDirect + 1).ToArgb() == cyan, "Proxy starts right above direct");
                    Check(bars.GetPixel(0, bottom - expectDirect - expectProxy).ToArgb() == black && bars.GetPixel(0, 0).ToArgb() == black, "Unused length shows the black strip");
                }
                SendMessage(peer, 0x8042, 0, 0);
                startupBar.UpdateFullscreenLayout(peer);
                Check(startupBar.Location == new Point(40, 40) && !Field<bool>(startupBar, "_miniMode"), "Fullscreen startup restores saved normal location on exit");
                startupBar.Close();
            }
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { success = true, passed = Passed }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { success = false, passed = Passed, error = ex.ToString() }));
        }
        finally { if (child is { HasExited: false }) { child.Kill(); child.WaitForExit(5000); } child?.Dispose(); }
    }
    private static void RunOnPrivateDesktop(string folder)
    {
        string name = "RouterSpeedTest-" + Guid.NewGuid().ToString("N");
        nint desktop = CreateDesktopW(name, null, 0, 0, 0x10000000, 0);
        if (desktop == 0) throw new System.ComponentModel.Win32Exception();
        try
        {
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), desktop = name };
            var command = new System.Text.StringBuilder($"\"{Environment.ProcessPath}\" \"{folder}\"");
            if (!CreateProcessW(null, command, 0, 0, false, 0, 0, null, ref startup, out var process))
                throw new System.ComponentModel.Win32Exception();
            CloseHandle(process.thread);
            try
            {
                if (WaitForSingleObject(process.process, 45000) != 0)
                { TerminateProcess(process.process, 2); throw new Exception("Private-desktop test timed out"); }
            }
            finally { CloseHandle(process.process); }
        }
        finally { CloseDesktop(desktop); }
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Passed.Add(name); }
    private static void Wait(Func<bool> condition, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < milliseconds) Pump(10);
        if (!condition()) throw new Exception("Condition timed out");
    }
    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(2); } while (watch.ElapsedMilliseconds < milliseconds);
    }
    private static bool IsAbove(nint first, nint second)
    {
        for (nint w = GetWindow(second, 3); w != 0; w = GetWindow(w, 3)) if (w == first) return true;
        return false;
    }
    private static nint MovePeer(nint window, nint after) => SendMessage(window, 0x8041, after == -1 ? 1 : after == -2 ? 2 : 3, after);
    private sealed class PositionSpy : NativeWindow, IDisposable
    {
        internal int Changes;
        internal PositionSpy(nint window) => AssignHandle(window);
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x46) Changes++;
            base.WndProc(ref message);
        }
        public void Dispose() => ReleaseHandle();
    }
    private static string DesktopOf(nint window)
    {
        uint thread = GetWindowThreadProcessId(window, out _);
        var name = new System.Text.StringBuilder(256);
        GetUserObjectInformationW(GetThreadDesktop(thread), 2, name, 512, out _);
        return name.ToString();
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool GetUserObjectInformationW(nint handle, int index, System.Text.StringBuilder info, uint length, out uint needed);
    private sealed class Peer : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x8041)
            {
                nint after = message.WParam == 1 ? -1 : message.WParam == 2 ? -2 : message.LParam;
                if (message.WParam is 1 or 2) TopMost = message.WParam == 1;
                message.Result = SetWindowPos(Handle, after, 0, 0, 0, 0, 0x13) ? 1 : 0;
                return;
            }
            if (message.Msg == 0x8042)
            {
                Bounds = message.WParam != 0 ? Screen.FromHandle(Handle).Bounds : new Rectangle(40, 40, 320, 130);
                return;
            }
            if (message.Msg == 0x8043)
            {
                bool maximize = message.WParam != 0;
                FormBorderStyle = maximize ? FormBorderStyle.Sizable : FormBorderStyle.None;
                WindowState = maximize ? FormWindowState.Maximized : FormWindowState.Normal;
                if (!maximize) Bounds = new Rectangle(40, 40, 320, 130);
                return;
            }
            base.WndProc(ref message);
        }
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb; public string? reserved, desktop, title;
        public uint x, y, width, height, charsX, charsY, fill, flags;
        public short show, reserved2; public nint reservedPtr, input, output, error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public nint process, thread; public uint pid, tid; }
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern nint CreateDesktopW(string name, string? device, nint mode, uint flags, uint access, nint security);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcessW(string? app, System.Text.StringBuilder command, nint processSecurity, nint threadSecurity, bool inherit, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint cmd);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; public override string ToString() => $"{Left},{Top},{Right},{Bottom}"; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(nint window, System.Text.StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint window, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
