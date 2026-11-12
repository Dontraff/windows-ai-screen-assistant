using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ScreenAssistant.Configuration;
using ScreenAssistant.Core.Overlay;

namespace ScreenAssistant.Overlay;

internal sealed class WpfOverlayDispatcher(Dispatcher dispatcher) : IOverlayDispatcher
{
    public Task InvokeAsync(Action action)
    {
        if (!dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;

        action();
        return Task.CompletedTask;
    }
}

internal sealed class WpfOverlayRenderer : IOverlayRenderer
{
    private readonly OverlayViewModel _viewModel;
    private readonly Action<OverlayPreferences> _save;
    private readonly DispatcherTimer _saveTimer;
    private OverlayWindow? _window;
    private Window? _mainWindow;
    private bool _dirty;

    public WpfOverlayRenderer(OverlayPreferences preferences, Action<OverlayPreferences> save)
    {
        _viewModel = new OverlayViewModel(preferences);
        _save = save;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _saveTimer.Tick += (_, _) => Flush();
        _viewModel.PreferencesChanged += (_, _) =>
        {
            _dirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        };
    }

    public event EventHandler? CloseRequested;
    public void Attach(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
        mainWindow.BindOverlaySettings(_viewModel);
    }
    public void SetEnabledPreference(bool enabled)
    {
        _viewModel.SetEnabled(enabled);
        Flush();
    }
    public void Render(OverlayState state) => _viewModel.Render(state);

    public void SetVisible(bool visible)
    {
        if (!visible)
        {
            if (_window is { } current)
            {
                _window = null;
                current.Closed -= UserClosed;
                current.Close();
            }
            Flush();
            return;
        }

        if (_window is not null)
            return;

        var window = new OverlayWindow(_viewModel, _mainWindow);
        window.Closed += UserClosed;
        _window = window;
        try
        {
            window.Show();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            window.Closed -= UserClosed;
            _window = null;
            window.Close();
            throw;
        }
    }

    public void Flush()
    {
        _saveTimer.Stop();
        if (!_dirty)
            return;

        try
        {
            _save(_viewModel.Preferences);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LocalFiles.Log("overlay-save", ex);
        }
    }

    private void UserClosed(object? sender, EventArgs e)
    {
        if (sender is OverlayWindow window)
            window.Closed -= UserClosed;

        _window = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
