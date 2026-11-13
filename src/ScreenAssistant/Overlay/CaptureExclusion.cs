using System.ComponentModel;
using System.Runtime.InteropServices;
using ScreenAssistant.Configuration;

namespace ScreenAssistant.Overlay;

internal static class CaptureExclusion
{
    public const uint WdaNone = 0;
    public const uint WdaMonitor = 0x00000001;
    public const uint WdaExcludeFromCapture = 0x00000011;

    public static bool Apply(nint hwnd, bool exclude)
    {
        if (hwnd == 0)
            return false;

        uint affinity = 0;
        if (exclude)
        {
            affinity = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                ? WdaExcludeFromCapture
                : WdaMonitor;
        }

        if (SetWindowDisplayAffinity(hwnd, affinity))
            return true;

        var error = Marshal.GetLastWin32Error();
        LocalFiles.Log("capture-exclusion", new Win32Exception(error));
        return false;
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
}
