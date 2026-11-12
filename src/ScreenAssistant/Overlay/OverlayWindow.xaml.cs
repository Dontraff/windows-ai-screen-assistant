using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ScreenAssistant.Overlay;

public partial class OverlayWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint PositionOnly = 0x0001 | 0x0004 | 0x0010;
    private const int DisplayChanged = 0x007E;
    private const int SettingsChanged = 0x001A;
    private readonly OverlayViewModel _viewModel;
    private readonly Window? _mainWindow;
    private HwndSource? _source;
    private nint _hwnd;

    internal OverlayWindow(OverlayViewModel viewModel, Window? mainWindow)
    {
        _viewModel = viewModel;
        _mainWindow = mainWindow;
        InitializeComponent();
        DataContext = viewModel;
        Width = viewModel.Width;
        Height = viewModel.DisplayHeight;
        _viewModel.PropertyChanged += ViewModelChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WindowMessage);
        Place(_viewModel.Preferences.X, _viewModel.Preferences.Y);
        Dispatcher.InvokeAsync(() => Place(_viewModel.Preferences.X, _viewModel.Preferences.Y),
            DispatcherPriority.Loaded);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModelChanged;
        _source?.RemoveHook(WindowMessage);
        _source = null;
        _hwnd = 0;
        base.OnClosed(e);
    }

    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        DragMove();
        if (GetWindowRect(_hwnd, out var rect))
        {
            Place(rect.Left, rect.Top, MonitorFromWindow(_hwnd, MonitorDefaultToNearest));
            if (GetWindowRect(_hwnd, out var placed))
                _viewModel.SetPosition(placed.Left, placed.Top);
        }
    }

    private nint WindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg is DisplayChanged or SettingsChanged)
            Dispatcher.InvokeAsync(KeepOnScreen, DispatcherPriority.Loaded);
        return 0;
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OverlayViewModel.Width) or nameof(OverlayViewModel.DisplayHeight))
            KeepOnScreen();
        else if (e.PropertyName == nameof(OverlayViewModel.Preferences))
            Place(null, null);
    }

    private void KeepOnScreen()
    {
        if (_hwnd != 0 && GetWindowRect(_hwnd, out var rect))
            Place(rect.Left, rect.Top);
    }

    private void Place(int? x, int? y, nint preferredMonitor = 0)
    {
        if (_hwnd == 0)
            return;

        var monitor = preferredMonitor;
        if (monitor == 0 && x is int left && y is int top)
            monitor = MonitorFromPoint(new PointPixels(left, top), 0);
        var restore = monitor != 0;
        if (!restore)
        {
            var owner = _mainWindow is null ? _hwnd : new WindowInteropHelper(_mainWindow).Handle;
            monitor = MonitorFromWindow(owner, MonitorDefaultToNearest);
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
            return;

        // Win32 rects use physical pixels. WPF scales the content and size for each monitor.
        var area = info.Work;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        Width = Math.Min(_viewModel.Width, (area.Right - area.Left) / dpi.DpiScaleX);
        Height = Math.Min(_viewModel.DisplayHeight, (area.Bottom - area.Top) / dpi.DpiScaleY);
        var width = (int)Math.Ceiling(Width * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(Height * dpi.DpiScaleY);
        var maxX = Math.Max(area.Left, area.Right - width);
        var maxY = Math.Max(area.Top, area.Bottom - height);
        var targetX = restore ? x!.Value : maxX - 20;
        var targetY = restore ? y!.Value : area.Top + 20;
        SetWindowPos(_hwnd, 0, Math.Clamp(targetX, area.Left, maxX),
            Math.Clamp(targetY, area.Top, maxY), 0, 0, PositionOnly);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PointPixels(int X, int Y);

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

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromPoint(PointPixels point, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(nint hwnd, out RectPixels rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width,
        int height, uint flags);
}
