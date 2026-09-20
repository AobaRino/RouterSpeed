using System.Drawing;

namespace RouterSpeed;

/// <summary>Pure geometry for an overlay in the clear interval immediately before the notification area.</summary>
public static class TaskbarLayout
{
    public static bool TryCalculate(Rectangle taskbar, Rectangle monitor, Rectangle tray,
        IReadOnlyList<Rectangle> occupied, int dpi, out Rectangle available, out string? error)
        => TryCalculate(taskbar, monitor, tray, occupied, dpi, out available, out error, out _);

    public static bool TryCalculate(Rectangle taskbar, Rectangle monitor, Rectangle tray,
        IReadOnlyList<Rectangle> occupied, int dpi, out Rectangle available, out string? error, out bool layoutConfirmed)
    {
        available = Rectangle.Empty;
        error = null;
        layoutConfirmed = false;
        if (dpi is < 48 or > 768 || taskbar.Width <= 0 || taskbar.Height <= 0 || monitor.Width <= 0 || monitor.Height <= 0)
        {
            error = "任务栏尺寸暂不可用。";
            return false;
        }
        if (taskbar.Width < taskbar.Height * 4 || taskbar.Height > monitor.Height / 3)
        {
            layoutConfirmed = IsFullyVisible(taskbar, monitor);
            error = "当前仅支持主屏幕的横向任务栏。";
            return false;
        }
        if (!IsFullyVisible(taskbar, monitor))
        {
            error = "任务栏尚未完整显示。";
            return false;
        }
        Rectangle trayIntersection = Rectangle.Intersect(taskbar, tray);
        if (tray.Width <= 0 || trayIntersection.Width <= 0 || trayIntersection.Height < taskbar.Height / 2 ||
            tray.Left < taskbar.Left || tray.Right > taskbar.Right + 2)
        {
            error = "无法确认系统托盘的位置。";
            return false;
        }
        int occupiedRight = taskbar.Left;
        bool observed = false;
        foreach (Rectangle bounds in occupied)
        {
            if (!IsTaskbarRowControl(bounds, taskbar, dpi)) continue;
            Rectangle intersection = Rectangle.Intersect(bounds, taskbar);
            if (intersection.Width <= 0 || intersection.Height <= 0 || intersection.Left >= tray.Left) continue;
            observed = true;
            occupiedRight = Math.Max(occupiedRight, intersection.Right);
        }
        if (!observed)
        {
            error = "无法确认任务栏按钮的占用范围。";
            return false;
        }
        int horizontalMargin = Math.Max(4, (int)Math.Ceiling(6 * dpi / 96.0));
        int verticalMargin = Math.Max(1, (int)Math.Ceiling(2 * dpi / 96.0));
        int left = Math.Max(taskbar.Left + horizontalMargin, occupiedRight + horizontalMargin);
        int right = tray.Left - horizontalMargin;
        int top = taskbar.Top + verticalMargin;
        int bottom = taskbar.Bottom - verticalMargin;
        layoutConfirmed = true;
        if (right <= left || bottom <= top)
        {
            error = "托盘左侧没有安全的连续空白。";
            return false;
        }
        available = Rectangle.FromLTRB(left, top, right, bottom);
        return true;
    }

    /// <summary>Popup/flyout controls crossing the taskbar edge are not part of its button row.</summary>
    public static bool IsTaskbarRowControl(Rectangle control, Rectangle taskbar, int dpi)
    {
        int tolerance = Math.Max(1, (int)Math.Ceiling(dpi / 96.0));
        return control.Width > 0 && control.Height > 0 && taskbar.Width > 0 && taskbar.Height > 0 &&
            control.Top >= taskbar.Top - tolerance && control.Bottom <= taskbar.Bottom + tolerance &&
            control.Left >= taskbar.Left - tolerance && control.Right <= taskbar.Right + tolerance &&
            control.Top + control.Height / 2 >= taskbar.Top && control.Top + control.Height / 2 < taskbar.Bottom;
    }

    public static bool IsFullyVisible(Rectangle taskbar, Rectangle monitor)
    {
        Rectangle visible = Rectangle.Intersect(taskbar, monitor);
        return taskbar.Width > 0 && taskbar.Height > 0 &&
            visible.Width >= taskbar.Width - 2 && visible.Height >= taskbar.Height - 2;
    }

    public static bool IsFullscreenCover(Rectangle window, Rectangle monitor, bool maximized, bool hasCaption)
    {
        // A normal maximized window on an auto-hiding taskbar can fill the monitor.
        // Its caption distinguishes it from the borderless/fullscreen case.
        if (maximized && hasCaption) return false;
        return monitor.Width > 0 && monitor.Height > 0 && window.Width > 0 && window.Height > 0 &&
            window.Left <= monitor.Left + 2 && window.Top <= monitor.Top + 2 &&
            window.Right >= monitor.Right - 2 && window.Bottom >= monitor.Bottom - 2;
    }
}
