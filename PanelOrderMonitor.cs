using System.Runtime.InteropServices;

namespace RouterSpeed;

/// <summary>Maintains the selected foreground overlay or desktop background placement.</summary>
internal sealed class PanelOrderMonitor : IDisposable
{
    private readonly Form _owner;
    private readonly Func<bool> _suspended;
    private readonly Func<nint> _foreground;
    private readonly Action<nint>? _updateLayout;
    private readonly WinEventProc _callback;
    private readonly List<nint> _hooks = [];
    private readonly System.Windows.Forms.Timer _fallback = new() { Interval = 500 };
    private bool _queued;
    private bool _disposed;
    private int _generation;

    internal PanelOrderMonitor(Form owner, Func<bool> suspended, Func<nint>? foreground = null, Action<nint>? updateLayout = null)
    {
        _owner = owner;
        _suspended = suspended;
        _foreground = foreground ?? GetForegroundWindow;
        _updateLayout = updateLayout;
        _callback = OnWindowEvent;
        // Receive desktop order changes on this UI thread, without entering other processes.
        foreach (uint eventId in new uint[] { 3, 0x8002, 0x8004 })
        {
            nint hook = SetWinEventHook(eventId, eventId, 0, _callback, 0, 0, 2);
            if (hook != 0) _hooks.Add(hook);
        }
        _owner.HandleDestroyed += HandleDestroyed;
        _owner.VisibleChanged += VisibilityChanged;
        _fallback.Tick += (_, _) => ApplyOrder();
        _fallback.Start();
    }

    private void VisibilityChanged(object? sender, EventArgs e) => ApplyOrder();
    private void HandleDestroyed(object? sender, EventArgs e)
    {
        _generation++;
        _queued = false;
    }

    private void OnWindowEvent(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || _queued || !_owner.IsHandleCreated || !_owner.Visible) return;
        // Z-order events may refer to the desktop's client object, not OBJID_WINDOW.
        if (eventId == 0x8002 && (objectId != 0 || childId != 0)) return;
        _queued = true;
        int generation = _generation;
        try
        {
            _owner.BeginInvoke((Action)(() =>
            {
                if (_disposed || generation != _generation) return;
                _queued = false;
                ApplyOrder();
            }));
        }
        catch (InvalidOperationException) { _queued = false; }
    }

    internal void ApplyOrder()
    {
        if (_disposed || !_owner.IsHandleCreated || !_owner.Visible || _suspended()) return;
        nint foreground = _foreground();
        _updateLayout?.Invoke(foreground);
        if (_owner.TopMost) RepairTopmost(foreground);
        else PlaceAtBottom();
    }

    private void RepairTopmost(nint activeWindow)
    {
        nint foreground = GetAncestor(activeWindow, 2);
        if (foreground == 0 || foreground == _owner.Handle || !IsWindowVisible(foreground)) return;
        GetWindowThreadProcessId(foreground, out uint processId);
        if (processId == Environment.ProcessId) return;
        if (!GetWindowRect(foreground, out Rect rect) ||
            !_owner.Bounds.IntersectsWith(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom))) return;
        nint above = GetWindow(_owner.Handle, 3);
        for (int i = 0; above != 0 && i < 1024; i++, above = GetWindow(above, 3))
        {
            if (above != foreground) continue;
            SetWindowPos(_owner.Handle, -1, 0, 0, 0, 0, 0x0013);
            break;
        }
    }

    private void PlaceAtBottom()
    {
        nint handle = _owner.Handle;
        // HWND_BOTTOM can put a window behind Explorer's wallpaper. Instead insert
        // immediately above the desktop surface while leaving all applications above it.
        nint desktop = 0;
        var name = new System.Text.StringBuilder(256);
        nint window = GetTopWindow(0);
        for (int i = 0; window != 0 && i < 1024; i++, window = GetWindow(window, 2))
        {
            if (window == handle || !IsWindowVisible(window)) continue;
            GetClassName(window, name, name.Capacity);
            if (name.ToString() is not ("Progman" or "WorkerW")) continue;
            if (GetWindowRect(window, out Rect rect) &&
                _owner.Bounds.IntersectsWith(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)))
            { desktop = window; break; }
        }
        nint after = desktop == 0 ? (nint)1 : GetWindow(desktop, 3);
        if (after == handle || (desktop == 0 && GetWindow(handle, 2) == 0)) return;
        // Never inherit the topmost band from an Explorer window during Show Desktop.
        if (after != 0 && after != 1 && (GetWindowLongPtrW(after, -20).ToInt64() & 8) != 0) after = 0;
        SetWindowPos(handle, after, 0, 0, 0, 0, 0x0013); // NOMOVE | NOSIZE | NOACTIVATE
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        _fallback.Dispose();
        _owner.HandleDestroyed -= HandleDestroyed;
        _owner.VisibleChanged -= VisibilityChanged;
        foreach (nint hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
        GC.KeepAlive(_callback);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate void WinEventProc(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, System.Text.StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
