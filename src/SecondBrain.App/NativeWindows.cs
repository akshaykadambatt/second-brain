using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SecondBrain.Core;

namespace SecondBrain.App;

internal static class NativeWindows
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    { public int Size; public Rect Monitor, Work; public uint Flags; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

    public static bool ExcludeFromCapture(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return SetWindowDisplayAffinity(hwnd, 0x11) && GetWindowDisplayAffinity(hwnd, out var affinity) && affinity == 0x11;
    }

    public static IReadOnlyList<PanelPlacement> WorkAreas()
    {
        var result = new List<PanelPlacement>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info)) result.Add(new(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static PanelPlacement GetBounds(Window window)
    {
        if (!GetWindowRect(new WindowInteropHelper(window).Handle, out var rect)) throw new InvalidOperationException("Cannot read panel bounds.");
        return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public static void Restore(Window window, PanelPlacement placement)
    {
        var areas = WorkAreas();
        if (areas.Count == 0) return;
        var best = areas.OrderByDescending(a => Math.Max(0, Math.Min(a.Left + a.Width, placement.Left + placement.Width) - Math.Max(a.Left, placement.Left))
            * (double)Math.Max(0, Math.Min(a.Top + a.Height, placement.Top + placement.Height) - Math.Max(a.Top, placement.Top))).First();
        var fit = placement.FitTo(best);
        SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, fit.Left, fit.Top, fit.Width, fit.Height, 0x0014);
    }
}
