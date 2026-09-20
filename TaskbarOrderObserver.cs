using System.Runtime.InteropServices;

namespace RouterSpeed;

/// <summary>
/// Observes shell events without injecting code or changing Explorer. The UI
/// callback repairs only our window; the placement timer remains the fallback.
/// All lifecycle methods run on the dispatcher's UI thread.
/// </summary>
internal sealed class TaskbarOrderObserver : IDisposable
{
    private const uint ForegroundEvent = 0x0003;
    private const uint ShowEvent = 0x8002;
    private const uint ReorderEvent = 0x8004;
    private const uint SkipOwnProcess = 0x0002; // OUTOFCONTEXT is zero.
    private readonly Control _dispatcher;
    private readonly Action _onOrderChanged;
    private readonly Func<nint> _findTaskbar;
    private readonly WinEventCallback _callback;
    private nint _foregroundHook;
    private nint _objectHook;
    private nint _taskbar;
    private uint _shellProcess;
    private bool _enabled;
    private bool _disposed;
    private bool _queued;
    private long _generation;

    internal TaskbarOrderObserver(Control dispatcher, Action onOrderChanged, Func<nint>? findTaskbar = null)
    {
        _dispatcher = dispatcher;
        _onOrderChanged = onOrderChanged;
        _findTaskbar = findTaskbar ?? (() => FindWindow("Shell_TrayWnd", null));
        _callback = OnWinEvent;
        dispatcher.HandleCreated += OnHandleCreated;
        dispatcher.HandleDestroyed += OnHandleDestroyed;
    }

    internal bool IsBound => _foregroundHook != 0 && _objectHook != 0;

    internal void Start()
    {
        if (_disposed) return;
        _enabled = true;
        RefreshBinding();
    }

    // Called by the existing timer to recover after Explorer restarts or a hook
    // cannot be installed. Stable ticks do not replace hooks or adjust Z order.
    internal void RefreshBinding()
    {
        if (_disposed || !_enabled || !_dispatcher.IsHandleCreated || _dispatcher.IsDisposed) return;
        nint taskbar = _findTaskbar();
        uint process = 0;
        if (taskbar != 0) GetWindowThreadProcessId(taskbar, out process);
        if (taskbar != 0 && taskbar == _taskbar && process == _shellProcess && IsBound) return;
        Unbind();
        if (taskbar == 0 || process == 0 || process == Environment.ProcessId) return;
        _taskbar = taskbar;
        _shellProcess = process;
        _foregroundHook = SetWinEventHook(ForegroundEvent, ForegroundEvent, 0, _callback, process, 0, SkipOwnProcess);
        _objectHook = SetWinEventHook(ShowEvent, ReorderEvent, 0, _callback, process, 0, SkipOwnProcess);
        if (!IsBound) Unbind();
    }

    internal void Stop()
    {
        _enabled = false;
        Unbind();
    }

    private void OnWinEvent(nint hook, uint eventType, nint window, int objectId, int childId,
        uint sourceThread, uint eventTime)
    {
        // Top-level reorders may name the desktop rather than Shell_TrayWnd.
        // Filter by the emitting shell process in SetWinEventHook, not by HWND.
        if (_disposed || !_enabled || !IsBound || (hook != _foregroundHook && hook != _objectHook) ||
            _queued || !_dispatcher.IsHandleCreated || _dispatcher.IsDisposed) return;
        _queued = true;
        long generation = _generation;
        try
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation != _generation) return;
                _queued = false;
                if (!_disposed && _enabled && IsBound && _dispatcher.IsHandleCreated && !_dispatcher.IsDisposed)
                    _onOrderChanged();
            }));
        }
        catch (InvalidOperationException) { if (generation == _generation) _queued = false; }
    }

    private void OnHandleCreated(object? sender, EventArgs e) => RefreshBinding();
    private void OnHandleDestroyed(object? sender, EventArgs e) => Unbind();

    private void Unbind()
    {
        _generation++;
        _queued = false;
        nint foreground = _foregroundHook, objects = _objectHook;
        _foregroundHook = _objectHook = 0;
        _taskbar = 0;
        _shellProcess = 0;
        if (foreground != 0) UnhookWinEvent(foreground);
        if (objects != 0) UnhookWinEvent(objects);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _dispatcher.HandleCreated -= OnHandleCreated;
        _dispatcher.HandleDestroyed -= OnHandleDestroyed;
        GC.KeepAlive(_callback);
    }

    private delegate void WinEventCallback(nint hook, uint eventType, nint window, int objectId,
        int childId, uint sourceThread, uint eventTime);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? caption);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint eventMin, uint eventMax,
        nint module, WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(nint hook);
}
