using System;
using System.Runtime.InteropServices;

namespace ClaudeUsageWidget;

/// <summary>
/// Saves/restores the widget position as "which monitor + physical-pixel offset
/// from that monitor's work area". WPF's DIP Left/Top is mapped with the DPI of
/// whatever monitor the window starts on, so it lands in the wrong place on a
/// secondary monitor with a different scale; raw pixels + device name don't.
/// </summary>
public static class WindowPosition
{
    public readonly record struct Saved(string Monitor, int X, int Y);

    public static Saved? Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return null;
        var info = MonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
        if (info is not { } m) return null;
        return new Saved(m.szDevice, rect.Left - m.rcWork.Left, rect.Top - m.rcWork.Top);
    }

    /// <summary>Moves the window back; false if the monitor is gone (caller keeps its default).</summary>
    public static bool Restore(IntPtr hwnd, Saved saved)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return false;

        MONITORINFOEX? found = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
        {
            if (MonitorInfo(hMon) is { } m && m.szDevice == saved.Monitor)
            {
                found = m;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        if (found is not { } mon) return false;

        // Keep the whole widget inside the work area (resolution may have changed).
        var work = mon.rcWork;
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        int x = Math.Clamp(work.Left + saved.X, work.Left, Math.Max(work.Left, work.Right - w));
        int y = Math.Clamp(work.Top + saved.Y, work.Top, Math.Max(work.Top, work.Bottom - h));
        return SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static MONITORINFOEX? MonitorInfo(IntPtr hMon)
    {
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return hMon != IntPtr.Zero && GetMonitorInfo(hMon, ref info) ? info : null;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
