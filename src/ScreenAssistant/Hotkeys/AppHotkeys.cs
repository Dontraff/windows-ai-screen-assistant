namespace ScreenAssistant.Hotkeys;

internal static class AppHotkeys
{
    public const int Analyze = 1;
    public const int Overlay = 2;
    public const int Session = 3;
    public const int Crop = 4;
    public const int Panic = 5;
    public const int ShowWindow = 6;

    public static readonly (int Id, uint Modifiers, uint Vk, string Label)[] Defaults =
    [
        (Analyze, HotkeyManager.ModControl | HotkeyManager.ModAlt, 0x41, "Ctrl+Alt+A"),
        (Overlay, HotkeyManager.ModControl | HotkeyManager.ModAlt, 0x48, "Ctrl+Alt+H"),
        (Session, HotkeyManager.ModControl | HotkeyManager.ModAlt, 0x53, "Ctrl+Alt+S"),
        (Crop, HotkeyManager.ModControl | HotkeyManager.ModAlt, 0x52, "Ctrl+Alt+R"),
        (Panic, HotkeyManager.ModControl | HotkeyManager.ModAlt, 0x20, "Ctrl+Alt+Пробел"),
        (ShowWindow, HotkeyManager.ModControl | HotkeyManager.ModAlt, 0x4D, "Ctrl+Alt+M")
    ];
}
