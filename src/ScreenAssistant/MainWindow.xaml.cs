using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenAssistant.Configuration;
using ScreenAssistant.Core.AIClient;
using ScreenAssistant.Core.Geometry;
using ScreenAssistant.Core.Overlay;
using ScreenAssistant.Core.Scheduler;
using ScreenAssistant.Hotkeys;
using ScreenAssistant.Overlay;
using ScreenAssistant.ScreenCapture;
using ScreenAssistant.Tray;
using Windows.Graphics.Capture;

namespace ScreenAssistant;

public partial class MainWindow : Window
{
    private static readonly Brush Idle = Brushes.SlateGray;
    private static readonly Brush Active = Brushes.SeaGreen;
    private static readonly Brush Error = Brushes.Firebrick;

    private readonly AiClient _ai;
    private readonly AnalysisScheduler _scheduler;
    private readonly IOverlayService _overlay;
    private readonly Action<bool> _persistOverlayEnabled;
    private readonly ObservableCollection<HistoryEntry> _history = new();
    private GraphicsCaptureItem? _source;
    private CaptureSession? _capture;
    private Uri? _keyEndpoint;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _requestCancel;
    private HotkeyManager? _hotkeys;
    private AppTrayIcon? _tray;
    private HwndSource? _hwndSource;
    private CropRect? _crop;
    private bool _initialized;
    private bool _closing;
    private bool _exitRequested;
    private bool _shutdownComplete;
    private bool _sourceLost;
    private bool _operationUsesSource;
    private bool _sessionActive;
    private int _requests;
    private int _answers;
    private int _skipped;
    private string? _lastSuccessfulFingerprint;

    internal MainWindow(AiClient ai, AnalysisScheduler scheduler, IOverlayService overlay,
        bool overlayEnabled, Action<bool> persistOverlayEnabled)
    {
        _ai = ai;
        _scheduler = scheduler;
        _overlay = overlay;
        _persistOverlayEnabled = persistOverlayEnabled;
        InitializeComponent();
        ShowInTaskbar = false;
        HistoryList.ItemsSource = _history;
        OverlayEnabled.IsChecked = overlayEnabled;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                HideToTray();
        };
        Loaded += async (_, _) =>
        {
            _tray = new AppTrayIcon(ShowFromTray, RequestExit);
            await ApplyOverlayVisibilityAsync();
        };
        _initialized = true;
        try
        {
            var settings = UserSettings.Load();
            BaseUrl.Text = settings.BaseUrl;
            ModelId.Text = settings.Model;
            Interval.Text = settings.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
            Duration.Text = settings.DurationMinutes.ToString(CultureInfo.InvariantCulture);
            SkipIdentical.IsChecked = settings.SkipIdenticalFrames;
            UseStreaming.IsChecked = settings.UseStreaming;
            _crop = settings.GetStoredCrop();
            if (_crop is { } stored && !stored.IsEmpty)
                FrameDetails.Text = $"область {stored.Width} × {stored.Height} · {stored.X}, {stored.Y}";
        }
        catch (Exception ex)
        {
            LocalFiles.Log("settings-load", ex);
            BaseUrl.Text = AiClient.DefaultBaseUrl;
            Interval.Text = "30";
            Duration.Text = "120";
            SkipIdentical.IsChecked = true;
            UseStreaming.IsChecked = true;
            Status("Настройки не загрузились. Восстановлены значения по умолчанию.", error: true);
        }

        UpdateControls();
        UpdateStats();
    }

    internal void BindOverlaySettings(OverlayViewModel model) => OverlayOptions.DataContext = model;

    internal void WarnOverlayCaptureInclusion() =>
        Status("Плавающее окно не удалось исключить из захвата. Включите плотный фон или перенесите окно.",
            error: true);

    private void ResetOverlayPosition_Click(object sender, RoutedEventArgs e)
    {
        if (OverlayOptions.DataContext is OverlayViewModel model)
            model.ResetPosition();
    }

    internal void DisableOverlay() => OverlayEnabled.IsChecked = false;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
        _hotkeys = new HotkeyManager(hwnd, id => _ = OnHotkeyAsync(id));
        foreach (var (id, modifiers, vk, label) in AppHotkeys.Defaults)
            TryRegister(id, modifiers, vk, label);
    }

    private void TryRegister(int id, uint modifiers, uint vk, string label)
    {
        if (_hotkeys is null || _hotkeys.Register(id, modifiers, vk))
            return;
        Status($"Горячая клавиша {label} занята.", error: true);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == HotkeyManager.WmHotkey)
        {
            _hotkeys?.Handle(wParam);
            handled = true;
        }

        return 0;
    }

    private async Task OnHotkeyAsync(int id)
    {
        try
        {
            switch (id)
            {
                case AppHotkeys.Analyze:
                    await HotkeyAnalyzeAsync();
                    break;
                case AppHotkeys.Overlay:
                    await OnUiAsync(() => OverlayEnabled.IsChecked = OverlayEnabled.IsChecked != true);
                    break;
                case AppHotkeys.Session:
                    await HotkeySessionAsync();
                    break;
                case AppHotkeys.Crop:
                    await OnUiAsync(SelectCrop);
                    break;
                case AppHotkeys.Panic:
                    await PanicAsync();
                    break;
                case AppHotkeys.ShowWindow:
                    await OnUiAsync(ShowFromTray);
                    break;
            }
        }
        catch (Exception ex)
        {
            LocalFiles.Log("hotkey", ex);
            await OnUiAsync(() => Status("Горячая клавиша завершилась ошибкой. Подробности в журнале.", error: true));
        }
    }

    private async Task HotkeyAnalyzeAsync()
    {
        if (_source is null)
        {
            await OnUiAsync(() => Status("Сначала выберите источник захвата.", error: true));
            return;
        }

        UserSettings settings = new();
        Uri endpoint = null!;
        var key = "";
        var ready = false;
        await OnUiAsync(() =>
        {
            if (!ReadSettings(out settings, out endpoint))
                return;
            key = ApiKey.Password.Trim();
            if (key.Length == 0)
            {
                Status("Для анализа нужны настройки и API-ключ.", error: true);
                return;
            }

            ready = true;
        });
        if (!ready)
            return;

        var source = _source;
        if (_sessionActive)
        {
            var triggered = await _scheduler.TriggerAsync(
                token => AnalyzeOnceAsync(source, settings, endpoint, key, token), default);
            if (!triggered)
                await OnUiAsync(() => Status("Анализ уже выполняется."));
            return;
        }

        if (_operation is not null)
            return;

        await RunOperationAsync("oneshot", async token =>
        {
            await AnalyzeOnceAsync(source, settings, endpoint, key, token);
        }, usesSource: true);
    }

    private async Task HotkeySessionAsync()
    {
        if (_operation is not null)
        {
            await OnUiAsync(() =>
            {
                Status("Останавливаем операцию…");
                _operation.Cancel();
                UpdateControls();
            });
            return;
        }

        await StartSessionAsync();
    }

    private void SelectCrop()
    {
        var picker = new CropSelectionWindow();
        if (picker.ShowDialog() != true)
        {
            _crop = null;
            FrameDetails.Text = "";
            PersistCrop(null);
            Status("Область анализа сброшена.");
            return;
        }

        var relative = MonitorGeometry.ToMonitorRelative(picker.SelectedPixels, picker.SelectedMonitor);
        _crop = relative.IsEmpty ? null : relative;
        if (_crop is null)
        {
            FrameDetails.Text = "";
            PersistCrop(null);
            Status("Область слишком маленькая.", error: true);
            return;
        }

        FrameDetails.Text = $"область {_crop.Value.Width} × {_crop.Value.Height} · {_crop.Value.X}, {_crop.Value.Y}";
        PersistCrop(_crop);
        Status("Область анализа выбрана.");
    }

    private void PersistCrop(CropRect? crop)
    {
        if (!ReadSettings(out var settings, out _))
            return;

        try
        {
            var next = settings with
            {
                CropX = crop?.X,
                CropY = crop?.Y,
                CropWidth = crop?.Width,
                CropHeight = crop?.Height
            };
            next.Save();
        }
        catch (Exception ex)
        {
            LocalFiles.Log("crop-save", ex);
        }
    }

    private async Task PanicAsync()
    {
        _requestCancel?.Cancel();
        try
        {
            await _overlay.HideForAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            LocalFiles.Log("panic-hide", ex);
        }

        await OnUiAsync(() => Status("Паника: оверлей скрыт, текущий запрос отменён."));
    }

    private async void OverlayToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            await ApplyOverlayVisibilityAsync();
    }

    private async Task ApplyOverlayVisibilityAsync()
    {
        var enabled = OverlayEnabled.IsChecked == true;
        try
        {
            await _overlay.SetVisibleAsync(enabled);
            _persistOverlayEnabled(enabled);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            LocalFiles.Log("overlay-window", ex);
            await OnUiAsync(() =>
            {
                OverlayEnabled.IsChecked = false;
                Status("Не удалось открыть плавающее окно.", error: true);
            });
        }
    }

    private void BaseUrl_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_initialized)
            return;

        AiClient.TryParseBaseUrl(BaseUrl.Text, out var endpoint);
        if (endpoint == _keyEndpoint)
            return;

        _keyEndpoint = endpoint;
        ApiKey.Clear();
        if (endpoint is null)
            return;

        try { ApiKey.Password = KeyStore.Read(endpoint); }
        catch (Exception ex)
        {
            LocalFiles.Log("key-load", ex);
            Status("Ключ не удалось прочитать. Введите его заново.", error: true);
        }
    }

    private async void Pick_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("picker", async token =>
        {
            await OnUiAsync(() => Status("Выберите монитор в системном окне…"));
            var item = await GraphicsCapture.PickAsync(new WindowInteropHelper(this).Handle, token);
            token.ThrowIfCancellationRequested();
            if (item is null)
            {
                await OnUiAsync(() => Status("Выбор источника отменён."));
                return;
            }

            await OnUiAsync(() =>
            {
                DetachSource();
                _source = item;
                item.Closed += Source_Closed;
                SourceText.Text = item.DisplayName;
                ClearPreview();
                Status("Источник выбран.");
            });
        });

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_source is not { } source)
            return;

        await RunOperationAsync("preview", async token =>
        {
            await OnUiAsync(() => Status("Получаем локальный кадр…"));
            await CaptureAsync(source, token);
            await OnUiAsync(() => Status("Локальный кадр готов."));
        }, usesSource: true);
    }

    private async void Catalog_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("catalog", async token =>
        {
            await OnUiAsync(() => Status("Загружаем каталог AI Tunnel…"));
            var current = await OnUiAsync(() => ModelId.Text);
            var models = await _ai.ListVisionModelsAsync(token);
            token.ThrowIfCancellationRequested();
            await OnUiAsync(() =>
            {
                ModelId.ItemsSource = models;
                ModelId.Text = current;
                Status(models.Length == 0
                    ? "В каталоге нет моделей для изображений. Можно ввести ID вручную."
                    : $"Моделей с поддержкой изображений: {models.Length}. Откройте список «Модель».");
            });
        });

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadSettings(out var settings, out var endpoint))
            return;
        try
        {
            KeyStore.Save(endpoint, ApiKey.Password.Trim());
            settings.Save();
            Status("Настройки сохранены.");
        }
        catch (Exception ex)
        {
            LocalFiles.Log("settings-save", ex);
            Status("Не удалось сохранить настройки. Проверьте доступ к профилю Windows.", error: true);
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await StartSessionAsync();

    private async Task StartSessionAsync()
    {
        if (_operation is not null)
            return;

        GraphicsCaptureItem? source = null;
        UserSettings settings = new();
        Uri endpoint = null!;
        var key = "";
        var ready = false;
        await OnUiAsync(() =>
        {
            if (_source is not { } item || !ReadSettings(out settings, out endpoint))
                return;

            key = ApiKey.Password.Trim();
            if (key.Length == 0 || key.Any(char.IsWhiteSpace) || key.Any(char.IsControl))
            {
                Status("Введите действительный API-ключ без пробелов.", error: true);
                ApiKey.Focus();
                return;
            }
            if (settings.Model.Length == 0)
            {
                Status("Выберите модель из каталога или введите её ID.", error: true);
                ModelId.Focus();
                return;
            }

            try
            {
                KeyStore.Save(endpoint, key);
                settings.Save();
            }
            catch (Exception ex)
            {
                LocalFiles.Log("settings-autosave", ex);
                Status("Не удалось сохранить ключ и настройки перед запуском.", error: true);
                return;
            }

            source = item;
            ready = true;
        });
        if (!ready || source is null)
            return;

        await RunOperationAsync("session", async token =>
        {
            await OnUiAsync(() => Status("API проверяет модель на тестовой картинке…"));
            await _overlay.BeginAsync("Проверяем модель…", reset: true);
            var (image, colors) = FrameEncoding.CreateVisionProbe();
            await _ai.VerifyVisionAsync(endpoint, key, settings.Model, image, colors, token);
            token.ThrowIfCancellationRequested();

            await OnUiAsync(() =>
            {
                _history.Clear();
                HistoryEmpty.Visibility = Visibility.Visible;
                AnswerText.Text = "Ответ появится после первого анализа.";
                _lastSuccessfulFingerprint = null;
                _requests = _answers = _skipped = 0;
                UpdateStats();
            });

            await using var capture = new CaptureSession();
            await capture.StartAsync(source, token);
            _capture = capture;
            _sessionActive = true;
            try
            {
                var end = await _scheduler.RunAsync(
                    TimeSpan.FromSeconds(settings.IntervalSeconds),
                    TimeSpan.FromMinutes(settings.DurationMinutes),
                    sessionToken => AnalyzeOnceAsync(source, settings, endpoint, key, sessionToken), token);

                var finalStatus = _sourceLost ? "Источник захвата закрыт. Выберите экран повторно."
                    : end == SessionEnd.DurationElapsed ? "Время тренировки истекло."
                    : "Тренировка остановлена.";
                await OnUiAsync(() => Status(finalStatus, error: _sourceLost));
                await _overlay.EndAsync(finalStatus, error: _sourceLost);
            }
            finally
            {
                _sessionActive = false;
                _capture = null;
            }
        }, usesSource: true);
    }

    private async Task<TimeSpan?> AnalyzeOnceAsync(GraphicsCaptureItem source, UserSettings settings,
        Uri endpoint, string key, CancellationToken token)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        _requestCancel = request;
        try
        {
            await OnUiAsync(() => Status("Тренировка идёт · получаем кадр…"));
            var image = await CaptureAsync(source, request.Token);
            if (settings.SkipIdenticalFrames && image.Fingerprint == _lastSuccessfulFingerprint)
            {
                await OnUiAsync(() =>
                {
                    _skipped++;
                    UpdateStats();
                    Status("Тренировка идёт · кадр совпал с последним проанализированным.");
                });
                return null;
            }

            await OnUiAsync(() => Status("Тренировка идёт · ждём ответ API…"));
            var requestId = await _overlay.BeginAsync("Ждём ответ модели…");
            await OnUiAsync(() =>
            {
                _requests++;
                UpdateStats();
            });
            try
            {
                var text = await Task.Run(() => settings.UseStreaming
                    ? _ai.AnalyzeStreamingAsync(endpoint, key, settings.Model, image.Payload,
                        delta => request.IsCancellationRequested
                            ? Task.CompletedTask : _overlay.AppendAsync(requestId, delta), request.Token)
                    : _ai.AnalyzeAsync(endpoint, key, settings.Model, image.Payload, request.Token),
                    request.Token);
                request.Token.ThrowIfCancellationRequested();
                await _overlay.CompleteAsync(requestId, text);
                await OnUiAsync(() =>
                {
                    _answers++;
                    _lastSuccessfulFingerprint = image.Fingerprint;
                    AnswerText.Text = text;
                    AnswerText.ScrollToHome();
                    _history.Insert(0, new HistoryEntry(DateTimeOffset.Now, settings.Model, text));
                    if (_history.Count > 200)
                        _history.RemoveAt(_history.Count - 1);
                    HistoryEmpty.Visibility = Visibility.Collapsed;
                    HistoryList.SelectedIndex = 0;
                    Status("Тренировка идёт · ответ получен.");
                });
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested && !token.IsCancellationRequested)
            {
                await _overlay.FailAsync(requestId, "Запрос отменён.");
                return null;
            }
            catch (AiException ex) when (!ex.RequiresConfigurationChange)
            {
                LocalFiles.Log("analysis", ex);
                var next = ex.RetryAfter is { } delay && delay > TimeSpan.Zero
                    ? $" Пауза API не менее {Math.Ceiling(delay.TotalSeconds):0} с."
                    : " Следующая попытка по интервалу.";
                var message = ex.Message + next;
                await OnUiAsync(() => Status(message, error: true));
                await _overlay.FailAsync(requestId, message);
                return ex.RetryAfter;
            }
            finally { await OnUiAsync(UpdateStats); }

            return null;
        }
        finally
        {
            if (ReferenceEquals(_requestCancel, request))
                _requestCancel = null;
        }
    }

    private async Task<CapturedImage> CaptureAsync(GraphicsCaptureItem source, CancellationToken token)
    {
        await OnUiAsync(() =>
        {
            CaptureStatus.Text = "Идёт захват";
            CaptureLight.Fill = Active;
        });
        try
        {
            CapturedImage image;
            if (_capture is not null)
                image = await _capture.TakeAsync(_crop, token);
            else
                image = await GraphicsCapture.TakeOnceAsync(source, _crop, token);

            token.ThrowIfCancellationRequested();
            await OnUiAsync(() =>
            {
                PreviewImage.Source = FrameEncoding.Decode(image.Payload);
                PreviewEmpty.Visibility = Visibility.Collapsed;
                var cropLabel = _crop is { } c
                    ? $"область {c.Width} × {c.Height} · {c.X}, {c.Y}"
                    : $"{image.Width} × {image.Height}";
                FrameDetails.Text = $"{cropLabel}   ·   {DateTime.Now:HH:mm:ss}";
            });
            return image;
        }
        finally
        {
            await OnUiAsync(() =>
            {
                CaptureStatus.Text = "Захват выключен";
                CaptureLight.Fill = Idle;
            });
        }
    }

    private async Task RunOperationAsync(string name, Func<CancellationToken, Task> action,
        bool usesSource = false)
    {
        if (_operation is not null || _closing || _shutdownComplete)
            return;

        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        _operationUsesSource = usesSource;
        _sourceLost = false;
        await OnUiAsync(UpdateControls);
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var message = _sourceLost ? "Источник захвата закрыт. Выберите экран повторно."
                : "Операция остановлена.";
            await OnUiAsync(() => Status(message, error: _sourceLost));
            if (name == "session")
                await _overlay.EndAsync(message, error: _sourceLost);
        }
        catch (CaptureException ex)
        {
            LocalFiles.Log(name, ex);
            await OnUiAsync(() => Status(ex.Message, error: true));
            if (name == "session")
                await _overlay.EndAsync(ex.Message, error: true);
        }
        catch (AiException ex)
        {
            LocalFiles.Log(name, ex);
            await OnUiAsync(() => Status(ex.Message, error: true));
            if (name == "session")
                await _overlay.EndAsync(ex.Message, error: true);
        }
        catch (Exception ex)
        {
            LocalFiles.Log(name, ex);
            const string message = "Операция завершилась ошибкой. Подробности в журнале.";
            await OnUiAsync(() => Status(message, error: true));
            if (name == "session")
                await _overlay.EndAsync(message, error: true);
        }
        finally
        {
            _operation = null;
            _sessionActive = false;
            await OnUiAsync(UpdateControls);
            if (_closing)
                await FinishCloseAsync();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is null)
            return;

        Status("Останавливаем операцию…");
        _operation.Cancel();
        UpdateControls();
    }

    private void Source_Closed(GraphicsCaptureItem item, object args) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (_source is null || !Equals(item, _source))
                return;

            _sourceLost = _operationUsesSource && _operation is not null;
            if (_sourceLost)
                _operation?.Cancel();
            DetachSource();
            ClearPreview();
            SourceText.Text = "Источник закрыт. Выберите монитор.";
            Status("Доступ к источнику захвата потерян.", error: true);
            UpdateControls();
        });

    private void ClearPreview()
    {
        PreviewImage.Source = null;
        PreviewEmpty.Visibility = Visibility.Visible;
        if (_crop is null)
            FrameDetails.Text = "";
    }

    private void DetachSource()
    {
        if (_source is { } source)
            source.Closed -= Source_Closed;
        _source = null;
    }

    private void History_Changed(object sender, SelectionChangedEventArgs e)
    {
        HistoryText.Text = (HistoryList.SelectedItem as HistoryEntry)?.Text ?? "";
        HistoryText.ScrollToHome();
    }

    private bool ReadSettings(out UserSettings settings, out Uri endpoint)
    {
        settings = new();
        endpoint = null!;
        if (!AiClient.TryParseBaseUrl(BaseUrl.Text, out var parsed))
        {
            Status("Введите HTTPS Base URL API, например https://api.aitunnel.ru/v1.", error: true);
            BaseUrl.Focus();
            return false;
        }
        if (!int.TryParse(Interval.Text, out var interval) || interval is < 5 or > 3600)
        {
            Status("Интервал должен быть от 5 до 3600 секунд.", error: true);
            Interval.Focus();
            return false;
        }
        if (!int.TryParse(Duration.Text, out var duration) || duration is < 1 or > 720)
        {
            Status("Длительность должна быть от 1 до 720 минут.", error: true);
            Duration.Focus();
            return false;
        }

        endpoint = parsed!;
        settings = new UserSettings
        {
            BaseUrl = endpoint.AbsoluteUri.TrimEnd('/'),
            Model = ModelId.Text.Trim(),
            IntervalSeconds = interval,
            DurationMinutes = duration,
            SkipIdenticalFrames = SkipIdentical.IsChecked == true,
            UseStreaming = UseStreaming.IsChecked == true,
            CropX = _crop?.X,
            CropY = _crop?.Y,
            CropWidth = _crop?.Width,
            CropHeight = _crop?.Height
        };
        return true;
    }

    private void UpdateControls()
    {
        var idle = _operation is null;
        PickButton.IsEnabled = SettingsPanel.IsEnabled = idle;
        PreviewButton.IsEnabled = StartButton.IsEnabled = idle && _source is not null;
        StopButton.IsEnabled = _operation is { IsCancellationRequested: false };
    }

    private void UpdateStats() =>
        StatsText.Text = $"Запросы кадров {_requests}   ·   Ответы {_answers}   ·   Пропущено {_skipped}";

    private void Status(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = error ? Error : Foreground;
    }

    private Task OnUiAsync(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
    }

    private Task<T> OnUiAsync<T>(Func<T> action)
    {
        if (Dispatcher.CheckAccess())
            return Task.FromResult(action());

        return Dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
    }

    private void HideToTray()
    {
        WindowState = WindowState.Normal;
        Hide();
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void RequestExit()
    {
        _exitRequested = true;
        if (_operation is not null)
        {
            _closing = true;
            _operation.Cancel();
            return;
        }

        _ = FinishCloseAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (!_exitRequested)
        {
            HideToTray();
            return;
        }

        if (_operation is not null)
        {
            _closing = true;
            _operation.Cancel();
            return;
        }

        _ = FinishCloseAsync();
    }

    private async Task FinishCloseAsync()
    {
        if (_shutdownComplete)
            return;

        _closing = true;
        _exitRequested = true;
        DetachSource();
        _hotkeys?.Dispose();
        _hotkeys = null;
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        _tray?.Dispose();
        _tray = null;
        if (_capture is not null)
        {
            try { await _capture.DisposeAsync(); }
            catch (Exception ex) { LocalFiles.Log("capture-dispose", ex); }
            _capture = null;
        }

        try { await _overlay.ShutdownAsync(); }
        catch (Exception ex) { LocalFiles.Log("overlay-shutdown", ex); }

        _shutdownComplete = true;
        await OnUiAsync(Close);
    }

    private sealed record HistoryEntry(DateTimeOffset Time, string Model, string Text)
    {
        public string Label => $"{Time:HH:mm:ss}   {Model}";
    }
}
