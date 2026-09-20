using System.Runtime.InteropServices;

namespace RouterSpeed;

/// <summary>Raises only our overlay, and only when Explorer has moved its taskbar above it.</summary>
internal static class TaskbarWindowOrder
{
    private const uint PreviousWindow = 3;
    private const uint NextWindow = 2;
    private const uint RootWindow = 2;
    private const uint PositionFlags = 0x0001 | 0x0002 | 0x0010 | 0x0200; // NOSIZE, NOMOVE, NOACTIVATE, NOOWNERZORDER
    private const int MaximumWindows = 4096;

    internal static bool KeepAboveTaskbar(nint overlay) => EnsureAbove(overlay, FindWindow("Shell_TrayWnd", null));

    // The explicit target allows native checks to use their own windows without touching Explorer.
    // True means the overlay was already above the target, or a single adjustment succeeded.
    internal static bool EnsureAbove(nint overlay, nint taskbar)
    {
        if (!IsOurVisibleTopLevelWindow(overlay) || !IsVisibleTopLevelWindow(taskbar) || overlay == taskbar)
            return false;
        if (!TryReadOrder(overlay, taskbar, out bool above, out nint predecessor)) return false;
        if (above) return true;

        // Never jump ahead of a popup already above the taskbar. If the shell changed
        // its order while we inspected it, wait for the next timer tick instead.
        if (!IsOurVisibleTopLevelWindow(overlay) || !IsVisibleTopLevelWindow(taskbar) ||
            GetWindow(taskbar, PreviousWindow) != predecessor ||
            (predecessor != 0 && (!IsWindow(predecessor) || GetWindow(predecessor, NextWindow) != taskbar)))
            return false;
        if (!TryReadOrder(overlay, taskbar, out above, out nint latestPredecessor)) return false;
        if (above) return true;
        if (latestPredecessor != predecessor) return false;

        return SetWindowPos(overlay, predecessor == 0 ? new nint(-1) : predecessor, 0, 0, 0, 0, PositionFlags);
    }

    private static bool TryReadOrder(nint overlay, nint target, out bool above, out nint predecessor)
    {
        above = false;
        predecessor = 0;
        bool seenOverlay = false, seenTarget = false;
        var visited = new HashSet<nint>();
        nint previous = 0;
        for (nint window = GetTopWindow(0); window != 0 && visited.Count < MaximumWindows; window = GetWindow(window, NextWindow))
        {
            if (!visited.Add(window) || !IsWindow(window)) return false;
            if (window == overlay)
            {
                if (seenTarget) return true;
                seenOverlay = true;
            }
            if (window == target)
            {
                predecessor = previous;
                if (seenOverlay) { above = true; return true; }
                seenTarget = true;
            }
            previous = window;
        }
        return false;
    }

    private static bool IsOurVisibleTopLevelWindow(nint window)
    {
        if (!IsVisibleTopLevelWindow(window)) return false;
        return GetWindowThreadProcessId(window, out uint processId) != 0 && processId == Environment.ProcessId;
    }

    private static bool IsVisibleTopLevelWindow(nint window) => window != 0 && IsWindow(window) &&
        IsWindowVisible(window) && GetAncestor(window, RootWindow) == window;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? caption);
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint parent);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
