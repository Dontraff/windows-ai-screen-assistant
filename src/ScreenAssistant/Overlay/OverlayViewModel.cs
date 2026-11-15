using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using ScreenAssistant.Core.Overlay;

namespace ScreenAssistant.Overlay;

internal sealed class OverlayViewModel(OverlayPreferences preferences) : INotifyPropertyChanged
{
    private OverlayState _state = new("Готово к тренировке", "", OverlayPhase.Idle);
    private OverlayPreferences _preferences = preferences;

    public string Status => _state.Status;
    public string Text => _state.Text;
    public bool HasError => _state.Phase == OverlayPhase.Error;

    public string CompactText
    {
        get
        {
            if (HasError || _state.Phase == OverlayPhase.Loading)
                return Status;
            foreach (var line in Text.Replace("\r\n", "\n").Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                    return trimmed;
            }
            return Status;
        }
    }

    public double DisplayHeight => Compact ? CompactHeight : Height;
    public double CompactFontSize => Math.Min(FontSize, CompactHeight - 14);

    public bool Compact
    {
        get => _preferences.Compact;
        set
        {
            SetAppearance(_preferences with { Compact = value });
            Changed(nameof(DisplayHeight));
        }
    }

    public bool ExcludeFromCapture
    {
        get => _preferences.ExcludeFromCapture;
        set => SetAppearance(_preferences with { ExcludeFromCapture = value });
    }

    public bool SolidBackground
    {
        get => _preferences.SolidBackground;
        set => SetAppearance(_preferences with { SolidBackground = value });
    }

    public double CompactHeight
    {
        get => _preferences.CompactHeight;
        set
        {
            SetAppearance(_preferences with { CompactHeight = value });
            Changed(nameof(DisplayHeight));
            Changed(nameof(CompactFontSize));
        }
    }

    public double Width
    {
        get => _preferences.Width;
        set => SetAppearance(_preferences with { Width = value });
    }

    public double Height
    {
        get => _preferences.Height;
        set
        {
            SetAppearance(_preferences with { Height = value });
            Changed(nameof(DisplayHeight));
        }
    }

    public double FontSize
    {
        get => _preferences.FontSize;
        set
        {
            SetAppearance(_preferences with { FontSize = value });
            Changed(nameof(CompactFontSize));
        }
    }

    // 0.85 → 9px, 1.0 → 14px; ниже 0.85 — без inset.
    public Thickness TextInset
    {
        get
        {
            if (PanelOpacity < .85)
                return new(24, 0, 0, 0);
            var t = (PanelOpacity - .85) / .15;
            var inset = 9 + t * 5;
            return new(inset, 0, 0, 0);
        }
    }

    public Brush HeaderBackground
    {
        get
        {
            if (PanelOpacity <= 0)
                return Brushes.Transparent;

            // Плавный alpha без разрыва на 0.65: добираем до ~65% суммарной непрозрачности.
            const double target = .65;
            if (PanelOpacity >= target)
                return Brushes.Transparent;

            var alpha = (byte)Math.Clamp(
                Math.Ceiling(255 * (target - PanelOpacity) / Math.Max(1e-6, 1 - PanelOpacity)), 0, 255);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 0x28, 0x28, 0x28));
            brush.Freeze();
            return brush;
        }
    }

    public double PanelOpacity
    {
        get => _preferences.PanelOpacity;
        set
        {
            SetAppearance(_preferences with { PanelOpacity = value });
            Changed(nameof(HeaderBackground));
            Changed(nameof(TextInset));
        }
    }

    public OverlayPreferences Preferences => _preferences;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? PreferencesChanged;

    public void Render(OverlayState state)
    {
        if (_state.Phase == state.Phase && _state.Text == state.Text && _state.Status == state.Status)
            return;

        var previous = _state;
        _state = state;
        if (previous.Status != state.Status)
            Changed(nameof(Status));
        if (previous.Text != state.Text)
            Changed(nameof(Text));
        if (previous.Phase != state.Phase)
            Changed(nameof(HasError));
        Changed(nameof(CompactText));
    }

    public void SetEnabled(bool enabled) =>
        SetPreferences(_preferences with { Enabled = enabled });

    public void SetPosition(int x, int y) =>
        SetPreferences(_preferences with { X = x, Y = y });

    public void ResetPosition()
    {
        SetPreferences(_preferences with { X = null, Y = null });
        Changed(nameof(Preferences));
    }

    private void SetAppearance(OverlayPreferences value, [CallerMemberName] string? name = null)
    {
        if (SetPreferences(value))
            Changed(name);
    }

    private bool SetPreferences(OverlayPreferences value)
    {
        if (_preferences == value)
            return false;

        _preferences = value;
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Changed(string? name) => PropertyChanged?.Invoke(this, new(name));
}
