using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace RouterSpeed;

public sealed record TaskbarDockSnapshot(Rectangle TaskbarBounds, Rectangle AvailableArea, int Dpi,
    bool ShouldHide, string Detail, bool IsAvailable, DateTimeOffset CapturedAtUtc = default,
    bool LayoutConfirmed = false, bool CanKeepPlacement = false)
{
    internal TaskbarDockAnchors? Anchors { get; init; }
}

internal sealed record TaskbarDockAnchors(nint TaskbarWindow, Rectangle TaskbarBounds, Rectangle MonitorBounds,
    nint TrayWindow, Rectangle TrayBounds, int Dpi, nint TaskListWindow, Rectangle TaskListBounds);

/// <summary>
/// Read-only primary-taskbar observer. UI Automation runs on its own background MTA thread;
/// callers only read immutable snapshots and never wait for Explorer or UI Automation.
/// </summary>
public sealed class TaskbarDockMonitor : IDisposable
{
    private const int RefreshMilliseconds = 700;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Func<TaskbarDockSnapshot> _capture;
    private readonly Func<bool> _fastShouldHide;
    private readonly Func<TaskbarDockSnapshot, bool> _anchorsCurrent;
    private TaskbarDockSnapshot _snapshot = Unavailable("正在读取任务栏布局。");
    private long _publishedAt;
    private TaskbarDockSnapshot? _lastAvailable;
    private long _lastAvailableAt;
    private Thread? _worker;
    private bool _enabled;
    private bool _disposed;
    private int _generation;

    public TaskbarDockMonitor() : this(null, null) { }

    internal TaskbarDockMonitor(Func<TaskbarDockSnapshot>? capture, Func<bool>? fastShouldHide,
        Func<TaskbarDockSnapshot, bool>? anchorsCurrent = null)
    {
        _capture = capture ?? Capture;
        _fastShouldHide = fastShouldHide ?? ShouldHideNow;
        _anchorsCurrent = anchorsCurrent ?? AnchorsAreCurrent;
    }

    public TaskbarDockSnapshot Current
    {
        get
        {
            TaskbarDockSnapshot current;
            TaskbarDockSnapshot? lastAvailable;
            long lastAvailableAt;
            lock (_gate)
            {
                if (!_enabled || _disposed) return Unavailable("任务栏跟随已停用。");
                current = _publishedAt == 0 || Stopwatch.GetElapsedTime(_publishedAt).TotalSeconds > 3
                    ? _snapshot with { AvailableArea = Rectangle.Empty, IsAvailable = false, LayoutConfirmed = false, CanKeepPlacement = false, Detail = "任务栏布局尚未更新，暂不使用旧位置。" }
                    : _snapshot;
                lastAvailable = _lastAvailable;
                lastAvailableAt = _lastAvailableAt;
            }
            try
            {
                if ((current.IsAvailable || current.LayoutConfirmed) && !_anchorsCurrent(current))
                    current = current with { AvailableArea = Rectangle.Empty, IsAvailable = false, LayoutConfirmed = false,
                        CanKeepPlacement = false, Detail = "任务栏锚点已变化，等待重新确认布局。" };
                if (!current.IsAvailable && !current.LayoutConfirmed)
                {
                    bool canKeep = lastAvailable is not null && lastAvailableAt != 0 &&
                        Stopwatch.GetElapsedTime(lastAvailableAt).TotalSeconds <= 2 && _anchorsCurrent(lastAvailable);
                    current = canKeep ? current with { TaskbarBounds = lastAvailable!.TaskbarBounds,
                        AvailableArea = lastAvailable.AvailableArea, Dpi = lastAvailable.Dpi, CanKeepPlacement = true }
                        : current with { AvailableArea = Rectangle.Empty, CanKeepPlacement = false };
                }
            }
            catch
            {
                current = current with { AvailableArea = Rectangle.Empty, IsAvailable = false,
                    LayoutConfirmed = false, CanKeepPlacement = false };
            }
            // These bounded Win32 reads never call UI Automation. A stalled provider
            // must not make the floating fallback appear over a newly fullscreen app.
            try { current = current with { ShouldHide = _fastShouldHide() }; }
            catch { /* Keep the last safe visibility decision if the shell is unavailable. */ }
            return current;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_enabled) return;
            _enabled = true;
            _generation++;
            _publishedAt = 0;
            _snapshot = Unavailable("正在读取任务栏布局。");
            _lastAvailable = null;
            _lastAvailableAt = 0;
            if (_worker is null)
            {
                _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "RouterSpeed.TaskbarObserver" };
                _worker.SetApartmentState(ApartmentState.MTA);
                _worker.Start();
            }
            _wake.Set();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _enabled = false;
            _generation++;
            _snapshot = Unavailable("任务栏跟随已停用。");
            _publishedAt = 0;
            _lastAvailable = null;
            _lastAvailableAt = 0;
            _wake.Set();
        }
    }

    private void WorkerLoop()
    {
        try
        {
            while (true)
            {
                bool enabled;
                int generation;
                lock (_gate)
                {
                    if (_disposed) return;
                    enabled = _enabled;
                    generation = _generation;
                }
                if (!enabled) { _wake.WaitOne(); continue; }
                TaskbarDockSnapshot next;
                try { next = _capture(); }
                catch
                {
                    next = Unavailable("暂时无法可靠读取任务栏按钮，等待下次采样。");
                }
                lock (_gate)
                {
                    if (_disposed) return;
                    if (_enabled && generation == _generation)
                    {
                        _snapshot = next;
                        _publishedAt = Stopwatch.GetTimestamp();
                        if (next.IsAvailable && next.LayoutConfirmed)
                        {
                            _lastAvailable = next;
                            _lastAvailableAt = _publishedAt;
                        }
                    }
                }
                _wake.WaitOne(RefreshMilliseconds);
            }
        }
        finally { _wake.Dispose(); }
    }

    private static TaskbarDockSnapshot Capture()
    {
        if (!TryReadAnchors(out TaskbarDockAnchors? anchors) || anchors is null)
            return Unavailable("任务栏或托盘布局尚未就绪，等待下一次采样。");
        Rectangle taskbar = anchors.TaskbarBounds;
        Rectangle monitor = anchors.MonitorBounds;
        Rectangle tray = anchors.TrayBounds;
        int dpi = anchors.Dpi;
        if (ShouldHideNow())
            return new(taskbar, Rectangle.Empty, dpi, true, "任务栏自动隐藏或当前程序处于全屏。", false, DateTimeOffset.UtcNow);
        if (taskbar.Width < taskbar.Height * 4 || taskbar.Height > monitor.Height / 3)
            return new(taskbar, Rectangle.Empty, dpi, false, "当前仅支持主屏幕的横向任务栏。", false,
                DateTimeOffset.UtcNow, LayoutConfirmed: TaskbarLayout.IsFullyVisible(taskbar, monitor)) { Anchors = anchors };

        // Limit discovery to the actual taskbar frame. Descendants of the shell root
        // can include temporary flyouts; an intersecting rectangle alone is not proof
        // that the control belongs to the taskbar's permanent button row.
        if (!TryReadButtonRow(anchors.TaskbarWindow, taskbar, tray, dpi, out Rectangle[] first))
            return new(taskbar, Rectangle.Empty, dpi, false, "任务栏按钮正在更新，尚未确认布局。", false, DateTimeOffset.UtcNow);
        if (!TryReadButtonRow(anchors.TaskbarWindow, taskbar, tray, dpi, out Rectangle[] second) || !first.SequenceEqual(second))
            return new(taskbar, Rectangle.Empty, dpi, false, "任务栏按钮边界正在变化，等待布局稳定。", false, DateTimeOffset.UtcNow);
        if (!TryReadAnchors(out TaskbarDockAnchors? latest) || latest != anchors)
            return new(taskbar, Rectangle.Empty, dpi, false, "任务栏或托盘锚点正在变化，等待位置稳定。", false, DateTimeOffset.UtcNow);

        bool available = TaskbarLayout.TryCalculate(taskbar, monitor, tray, second, dpi,
            out Rectangle area, out string? error, out bool confirmed);
        return new(taskbar, area, dpi, ShouldHideNow(),
            available ? "已确认应用按钮与系统托盘之间的空白。" : error ?? "任务栏布局尚未确认。",
            available, DateTimeOffset.UtcNow, confirmed) { Anchors = anchors };
    }

    private static bool TryReadButtonRow(nint taskbarWindow, Rectangle taskbar, Rectangle tray, int dpi,
        out Rectangle[] occupied)
    {
        occupied = [];
        var boundsList = new List<Rectangle>();
        int appButtons = 0;
        bool startButton = false;
        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.ClassNameProperty);
        cache.Add(AutomationElement.AutomationIdProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        using (cache.Activate())
        {
            AutomationElement root = AutomationElement.FromHandle(taskbarWindow);
            AutomationElement? frame = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "TaskbarFrame"));
            if (frame is null || !PhysicalBounds(frame.Cached.BoundingRectangle).Contains(taskbar)) return false;
            AutomationElementCollection elements = frame.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            if (elements.Count is 0 or > 512) return false;
            for (int index = 0; index < elements.Count; index++)
            {
                AutomationElement.AutomationElementInformation info = elements[index].Cached;
                if (info.IsOffscreen || !IsRowButton(info.ControlType)) continue;
                Rectangle bounds = PhysicalBounds(info.BoundingRectangle);
                if (!TaskbarLayout.IsTaskbarRowControl(bounds, taskbar, dpi)) continue;
                // Controls wholly inside the tray cannot bound its left-side gap.
                if (bounds.Left >= tray.Left) continue;
                boundsList.Add(bounds);
                if (info.AutomationId == "StartButton") startButton = true;
                if (info.ClassName.Contains("TaskListButton", StringComparison.OrdinalIgnoreCase) ||
                    info.AutomationId.StartsWith("Appid:", StringComparison.OrdinalIgnoreCase)) appButtons++;
            }
        }
        if (!startButton || appButtons == 0) return false;
        occupied = boundsList.Distinct().OrderBy(bounds => bounds.Left).ThenBy(bounds => bounds.Top)
            .ThenBy(bounds => bounds.Right).ThenBy(bounds => bounds.Bottom).ToArray();
        return true;
    }

    private static bool IsRowButton(ControlType type) => type == ControlType.Button || type == ControlType.SplitButton ||
        type == ControlType.ListItem || type == ControlType.TabItem || type == ControlType.CheckBox || type == ControlType.RadioButton;

    private static bool TryReadAnchors(out TaskbarDockAnchors? anchors)
    {
        anchors = null;
        nint taskbar = Native.FindWindow("Shell_TrayWnd", null);
        if (taskbar == 0 || !TryBounds(taskbar, out Rectangle taskbarBounds)) return false;
        nint monitor = Native.MonitorFromWindow(taskbar, 0);
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (monitor == 0 || !Native.GetMonitorInfo(monitor, ref info) || (info.Flags & 1) == 0) return false;
        nint tray = FindChildByClass(taskbar, "TrayNotifyWnd");
        if (tray == 0 || !TryBounds(tray, out Rectangle trayBounds)) return false;
        int dpi = (int)Native.GetDpiForWindow(taskbar);
        if (dpi is < 48 or > 768) return false;
        nint taskList = FindChildByClass(taskbar, "MSTaskListWClass");
        Rectangle taskListBounds = Rectangle.Empty;
        if (taskList != 0 && !TryBounds(taskList, out taskListBounds)) return false;
        anchors = new(taskbar, taskbarBounds, info.Monitor.ToRectangle(), tray, trayBounds, dpi, taskList, taskListBounds);
        return true;
    }

    private static bool AnchorsAreCurrent(TaskbarDockSnapshot snapshot) => snapshot.Anchors is { } expected &&
        Native.IsWindowVisible(expected.TaskbarWindow) && TaskbarLayout.IsFullyVisible(expected.TaskbarBounds, expected.MonitorBounds) &&
        TryReadAnchors(out TaskbarDockAnchors? current) && current == expected;
    private static Rectangle PhysicalBounds(System.Windows.Rect rect)
    {
        if (rect.IsEmpty || !double.IsFinite(rect.Left) || !double.IsFinite(rect.Top) ||
            !double.IsFinite(rect.Right) || !double.IsFinite(rect.Bottom) || rect.Width <= 0 || rect.Height <= 0 ||
            rect.Left < -1000000 || rect.Top < -1000000 || rect.Right > 1000000 || rect.Bottom > 1000000)
            return Rectangle.Empty;
        return Rectangle.FromLTRB((int)Math.Floor(rect.Left), (int)Math.Floor(rect.Top),
            (int)Math.Ceiling(rect.Right), (int)Math.Ceiling(rect.Bottom));
    }

    private static bool IsForegroundFullscreen(Rectangle monitor)
    {
        nint window = Native.GetForegroundWindow();
        if (window == 0 || !Native.IsWindowVisible(window) || Native.IsIconic(window)) return false;
        Native.GetWindowThreadProcessId(window, out uint processId);
        if (processId == Environment.ProcessId) return false;
        string className = WindowClass(window);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        Rectangle bounds;
        if (Native.DwmGetWindowAttribute(window, 9, out Native.Rect extended, Marshal.SizeOf<Native.Rect>()) == 0)
            bounds = extended.ToRectangle();
        else if (!TryBounds(window, out bounds)) return false;
        long style = Native.GetWindowLongPtr(window, -16).ToInt64();
        return TaskbarLayout.IsFullscreenCover(bounds, monitor, Native.IsZoomed(window), (style & 0x00c00000L) != 0);
    }

    private static bool ShouldHideNow()
    {
        nint taskbar = Native.FindWindow("Shell_TrayWnd", null);
        nint monitor = taskbar != 0 ? Native.MonitorFromWindow(taskbar, 1) : Native.MonitorFromPoint(default, 1);
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (monitor == 0 || !Native.GetMonitorInfo(monitor, ref info)) return false;
        Rectangle monitorBounds = info.Monitor.ToRectangle();
        if (IsForegroundFullscreen(monitorBounds)) return true;
        if (taskbar == 0) return false;
        if (!Native.IsWindowVisible(taskbar)) return true;
        var appBar = new Native.AppBarData { Size = Marshal.SizeOf<Native.AppBarData>() };
        bool autoHide = (Native.SHAppBarMessage(4, ref appBar).ToUInt64() & 1) != 0;
        return autoHide && (!TryBounds(taskbar, out Rectangle bounds) || !TaskbarLayout.IsFullyVisible(bounds, monitorBounds));
    }

    private static nint FindChildByClass(nint parent, string className)
    {
        nint result = 0;
        Native.EnumChildWindows(parent, (window, _) =>
        {
            if (WindowClass(window) != className) return true;
            result = window;
            return false;
        }, 0);
        return result;
    }

    private static string WindowClass(nint window)
    {
        var value = new StringBuilder(128);
        Native.GetClassName(window, value, value.Capacity);
        return value.ToString();
    }

    private static bool TryBounds(nint window, out Rectangle rectangle)
    {
        rectangle = Rectangle.Empty;
        if (!Native.GetWindowRect(window, out Native.Rect bounds)) return false;
        rectangle = bounds.ToRectangle();
        return rectangle.Width > 0 && rectangle.Height > 0;
    }

    private static TaskbarDockSnapshot Unavailable(string detail) =>
        new(Rectangle.Empty, Rectangle.Empty, 96, false, detail, false, DateTimeOffset.UtcNow);

    public void Dispose()
    {
        Thread? worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            worker = _worker;
            if (worker is not null) _wake.Set();
            else _wake.Dispose();
        }
        // A hung accessibility provider must never delay form shutdown. The background
        // worker owns its event until it returns; it cannot publish after disposal.
        if (worker is not null && worker != Thread.CurrentThread) worker.Join(100);
    }

    private static class Native
    {
        internal delegate bool WindowCallback(nint window, nint parameter);
        [StructLayout(LayoutKind.Sequential)] internal struct Rect
        {
            public int Left, Top, Right, Bottom;
            public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
        }
        [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct AppBarData
        {
            public int Size;
            public nint Window;
            public uint CallbackMessage;
            public uint Edge;
            public Rect Bounds;
            public nint Parameter;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindow(string className, string? name);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumChildWindows(nint parent, WindowCallback callback, nint parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint window, StringBuilder value, int size);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint window, out Rect rectangle);
        [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint window, uint flags);
        [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Point point, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsZoomed(nint window);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint window, int index);
        [DllImport("shell32.dll")] internal static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint window, uint attribute, out Rect value, int size);
    }
}
