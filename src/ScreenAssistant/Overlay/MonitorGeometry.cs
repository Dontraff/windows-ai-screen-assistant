using System.Runtime.InteropServices;
using System.Windows;
using ScreenAssistant.Core.Geometry;

namespace ScreenAssistant.Overlay;

internal static class MonitorGeometry
{
    public static CropRect ToMonitorRelative(Int32Rect screen, nint monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
        {
            return FrameGeometry.NormalizeCrop(
                new CropRect(screen.X, screen.Y, screen.Width, screen.Height),
                Math.Max(screen.X + screen.Width, 1),
                Math.Max(screen.Y + screen.Height, 1));
        }

        var area = info.Monitor;
        var width = Math.Max(1, area.Right - area.Left);
        var height = Math.Max(1, area.Bottom - area.Top);
        return FrameGeometry.NormalizeCrop(
            new CropRect(screen.X - area.Left, screen.Y - area.Top, screen.Width, screen.Height),
            width, height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectPixels
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RectPixels Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
