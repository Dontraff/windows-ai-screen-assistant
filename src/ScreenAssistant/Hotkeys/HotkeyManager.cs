using System.ComponentModel;
using System.Runtime.InteropServices;
using ScreenAssistant.Configuration;

namespace ScreenAssistant.Hotkeys;

internal sealed class HotkeyManager(nint hwnd, Action<int> onHotkey) : IDisposable
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModNoRepeat = 0x4000;
    public const int WmHotkey = 0x0312;

    private readonly HashSet<int> _ids = [];
    private bool _disposed;

    public bool Register(int id, uint modifiers, uint vk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!RegisterHotKey(hwnd, id, modifiers | ModNoRepeat, vk))
        {
            LocalFiles.Log("hotkey-register", new Win32Exception(Marshal.GetLastWin32Error()));
            return false;
        }

        _ids.Add(id);
        return true;
    }

    public void Handle(nint wParam)
    {
        var id = wParam.ToInt32();
        if (_ids.Contains(id))
            onHotkey(id);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var id in _ids)
            UnregisterHotKey(hwnd, id);
        _ids.Clear();
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
