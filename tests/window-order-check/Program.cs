using System.Runtime.InteropServices;
using System.Text.Json;
using RouterSpeed;

internal static class Program
{
    private const uint Flags = 0x0001 | 0x0002 | 0x0010 | 0x0200;
    private static readonly List<object> Results = [];
    private static readonly List<string> Failures = [];

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        if (args.Contains("--event-source")) return EventLatencyExperiment.RunSource();
        if (args.Contains("--event-latency")) return EventLatencyExperiment.Run();
        if (args.Contains("--observer-integration")) return EventLatencyExperiment.Run(productionObserver: true);
        using var target = new TransparentWindow();
        using var overlay = new TransparentWindow();
        using var popup = new TransparentWindow();
        nint foreground = GetForegroundWindow();
        target.Show(); overlay.Show(); popup.Show();
        Application.DoEvents();
        Check(GetForegroundWindow() == foreground, "Transparent test windows activated during setup.");

        Run("Stable overlay sends zero window-position requests", () =>
        {
            Arrange(target, overlay, popup);
            overlay.Requests = 0;
            for (int i = 0; i < 30; i++) Check(TaskbarWindowOrder.EnsureAbove(overlay.Handle, target.Handle), "Stable order was not recognized.");
            Check(overlay.Requests == 0, $"Stable refresh sent {overlay.Requests} positioning requests.");
            Check(Above(popup.Handle, overlay.Handle) && Above(overlay.Handle, target.Handle), "Stable order changed.");
        });
        Run("One repair keeps existing popup above overlay", () =>
        {
            Arrange(overlay, target, popup);
            overlay.Requests = 0;
            Rectangle bounds = overlay.Bounds;
            Check(TaskbarWindowOrder.EnsureAbove(overlay.Handle, target.Handle), "Repair failed.");
            Check(overlay.Requests == 1, $"Expected one request, got {overlay.Requests}.");
            Check(Above(popup.Handle, overlay.Handle) && Above(overlay.Handle, target.Handle), "Popup order was not preserved.");
            Check(GetWindow(overlay.Handle, 2) == target.Handle, "Overlay was not inserted immediately above target.");
            Check(overlay.LastInsertAfter == popup.Handle, "Repair did not use the taskbar's existing predecessor.");
            Check((overlay.LastFlags & Flags) == Flags && overlay.Bounds == bounds, "Repair moved, resized, or activated the overlay.");
            for (int i = 0; i < 30; i++) TaskbarWindowOrder.EnsureAbove(overlay.Handle, target.Handle);
            Check(overlay.Requests == 1, "Stable state was repeatedly repositioned after repair.");
        });
        Run("Topmost target without predecessor needs only one repair", () =>
        {
            Arrange(popup, overlay, target);
            Check(GetWindow(target.Handle, 3) == 0, "Test target was not the topmost window.");
            overlay.Requests = 0;
            Check(TaskbarWindowOrder.EnsureAbove(overlay.Handle, target.Handle), "Topmost repair failed.");
            // Win32 normalizes HWND_TOPMOST to HWND_TOP in WM_WINDOWPOSCHANGING.
            Check(overlay.Requests == 1 && (GetWindowLongPtr(overlay.Handle, -20).ToInt64() & 8) != 0,
                $"Topmost repair did not retain the topmost style with one request: requests={overlay.Requests}.");
            Check(Above(overlay.Handle, target.Handle), "Overlay stayed below the target.");
        });
        Run("Hidden and destroyed targets cause no window-position requests", () =>
        {
            overlay.Requests = 0;
            target.Hide();
            Check(!TaskbarWindowOrder.EnsureAbove(overlay.Handle, target.Handle), "Hidden target should be skipped.");
            using var vanished = new TransparentWindow(); nint vanishedHandle = vanished.Handle; vanished.Dispose();
            Check(!TaskbarWindowOrder.EnsureAbove(overlay.Handle, vanishedHandle), "Destroyed target should be skipped.");
            Check(!TaskbarWindowOrder.EnsureAbove(overlay.Handle, overlay.Handle), "Identical windows should be skipped.");
            Check(overlay.Requests == 0, "Unsafe input triggered a window-position request.");
        });
        Run("All checks preserve the active foreground window", () => Check(GetForegroundWindow() == foreground, "Foreground activation changed."));

        string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../results.json"));
        File.WriteAllText(output, JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{Results.Count - Failures.Count}/{Results.Count} passed; report={output}");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void Arrange(params Form[] bottomToTop)
    {
        foreach (Form form in bottomToTop)
            Check(SetWindowPos(form.Handle, new nint(-1), 0, 0, 0, 0, Flags), "Test arrangement failed.");
        Application.DoEvents();
    }

    private static bool Above(nint upper, nint lower)
    {
        var seen = new HashSet<nint>();
        for (nint next = GetWindow(lower, 3); next != 0 && seen.Count < 4096; next = GetWindow(next, 3))
        {
            if (!seen.Add(next)) return false;
            if (next == upper) return true;
        }
        return false;
    }

    private static void Run(string name, Action test)
    {
        try { test(); Results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { Failures.Add(name); Results.Add(new { name, passed = false, reason = ex.Message }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class TransparentWindow : Form
    {
        internal int Requests;
        internal nint LastInsertAfter;
        internal uint LastFlags;
        internal TransparentWindow()
        {
            Text = "RouterSpeed isolated order check";
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(0, 0, 2, 2); Opacity = 0; TopMost = true;
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x00000020; return value; } }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0046 && message.LParam != 0)
            {
                WindowPosition position = Marshal.PtrToStructure<WindowPosition>(message.LParam);
                Requests++; LastInsertAfter = position.InsertAfter; LastFlags = position.Flags;
            }
            base.WndProc(ref message);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPosition { internal nint Window, InsertAfter; internal int X, Y, Width, Height; internal uint Flags; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
