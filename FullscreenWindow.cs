using System.Runtime.InteropServices;
using System.Text;

namespace RouterSpeed;

internal static class FullscreenWindow
{
    internal static bool TryGetScreen(nint window, out Rectangle screen)
    {
        screen = Rectangle.Empty;
        window = GetAncestor(window, 2);
        if (window == 0 || !IsWindowVisible(window) || IsIconic(window)) return false;
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        GetWindowThreadProcessId(window, out uint process);
        if (process == Environment.ProcessId) return false;
        if (!GetWindowRect(window, out Rect bounds)) return false;
        screen = Screen.FromHandle(window).Bounds;
        const long caption = 0x00C00000; // WS_CAPTION
        bool maximizedWithCaption = IsZoomed(window) && (GetWindowLongPtr(window, -16).ToInt64() & caption) == caption;
        return IsFullscreen(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), screen, maximizedWithCaption);
    }

    // A maximized window that still has its title bar is an ordinary window, even when an
    // auto-hidden taskbar lets it (and its invisible resize border) span the whole monitor.
    internal static bool IsFullscreen(Rectangle window, Rectangle screen, bool maximizedWithCaption) =>
        !maximizedWithCaption && CoversScreen(window, screen);

    // Use full monitor bounds: a maximized window leaving room for the taskbar
    // must not be classified as borderless fullscreen.
    internal static bool CoversScreen(Rectangle window, Rectangle screen) =>
        screen.Width > 0 && screen.Height > 0 &&
        window.Left <= screen.Left + 2 && window.Top <= screen.Top + 2 &&
        window.Right >= screen.Right - 2 && window.Bottom >= screen.Bottom - 2;

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
}
