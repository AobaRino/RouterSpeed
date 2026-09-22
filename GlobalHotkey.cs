using System.Runtime.InteropServices;

namespace RouterSpeed;

/// <summary>
/// Registers a hotkey on a form's UI thread and restores it after the form recreates its handle.
/// Call TrySet and Dispose on that same UI thread.
/// </summary>
public sealed class GlobalHotkey : IMessageFilter, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private static int _idSequence;
    private readonly Form _owner;
    private readonly Action _toggle;
    private nint _registeredHandle;
    private int? _registeredId;
    private bool _disposed;

    public GlobalHotkey(Form owner, Action toggle)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _toggle = toggle ?? throw new ArgumentNullException(nameof(toggle));
        ObjectDisposedException.ThrowIf(owner.IsDisposed, owner);
        _owner.HandleCreated += OnHandleCreated;
        _owner.HandleDestroyed += OnHandleDestroyed;
        Application.AddMessageFilter(this);
    }

    public ShortcutDefinition? Current { get; private set; }
    public string? RegistrationError { get; private set; }
    public bool IsRegistered => _registeredId.HasValue;

    /// <summary>Registers the replacement before releasing the old hotkey. Null disables it.</summary>
    public bool TrySet(ShortcutDefinition? shortcut, out string? error)
    {
        error = null;
        if (_disposed || _owner.IsDisposed)
        {
            error = "快捷键服务已关闭，请重新打开网速条。";
            return false;
        }
        if (_owner.InvokeRequired)
        {
            error = "请在网速条的快捷键设置窗口中修改组合。";
            return false;
        }
        if (shortcut is null)
        {
            UnregisterCurrent();
            Current = null;
            RegistrationError = null;
            return true;
        }
        if (shortcut.Validate() is { } invalid)
        {
            error = invalid;
            RecordUnavailable(invalid);
            return false;
        }
        if (shortcut == Current && IsRegistered)
            return true;

        // Creating the handle does not show the form. A real registration attempt here
        // lets the settings dialog report conflicts before it saves the preference.
        nint handle;
        try { handle = _owner.Handle; }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            error = "程序窗口暂不可用，请稍后重试。";
            RecordUnavailable(error);
            return false;
        }
        // HandleCreated may already have restored this same desired shortcut while
        // retrieving Handle above. Do not try to register its combination twice.
        if (shortcut == Current && IsRegistered)
            return true;
        int candidateId = 0x4000 + (Interlocked.Increment(ref _idSequence) & 0x3fff);
        uint modifiers = ModNoRepeat | (shortcut.Alt ? 0x0001u : 0) | (shortcut.Control ? 0x0002u : 0) |
            (shortcut.Shift ? 0x0004u : 0) | (shortcut.Win ? 0x0008u : 0);
        if (!RegisterHotKey(handle, candidateId, modifiers, (uint)shortcut.Key))
        {
            int code = Marshal.GetLastWin32Error();
            error = code == 1409
                ? "这个快捷键已被其他程序或系统占用，请换一个组合。"
                : "无法注册这个快捷键，它可能被系统保留或已被占用，请换一个组合。";
            RecordUnavailable(error);
            return false;
        }

        UnregisterCurrent();
        _registeredHandle = handle;
        _registeredId = candidateId;
        Current = shortcut;
        RegistrationError = null;
        return true;
    }

    private void RecordUnavailable(string error)
    {
        // A failed replacement does not invalidate the old working shortcut.
        if (IsRegistered) return;
        RegistrationError = error;
    }

    private void OnHandleCreated(object? sender, EventArgs e)
    {
        if (!_disposed && Current is { } shortcut)
            TrySet(shortcut, out _);
    }

    private void OnHandleDestroyed(object? sender, EventArgs e)
    {
        UnregisterCurrent();
        if (!_disposed && Current is not null)
            RegistrationError = "窗口正在更新，快捷键会在窗口恢复后重新注册。";
    }

    public bool PreFilterMessage(ref Message message)
    {
        if (_disposed || message.Msg != WmHotkey || !_registeredId.HasValue ||
            message.HWnd != _registeredHandle || message.WParam != _registeredId.Value)
            return false;
        _toggle();
        return true;
    }

    private void UnregisterCurrent()
    {
        if (_registeredId is { } id && _registeredHandle != 0)
            UnregisterHotKey(_registeredHandle, id);
        _registeredId = null;
        _registeredHandle = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.HandleCreated -= OnHandleCreated;
        _owner.HandleDestroyed -= OnHandleDestroyed;
        Application.RemoveMessageFilter(this);
        UnregisterCurrent();
        Current = null;
        RegistrationError = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
