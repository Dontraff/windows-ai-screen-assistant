using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenAssistant.Configuration;
using ScreenAssistant.Core.AIClient;
using ScreenAssistant.Core.Overlay;
using ScreenAssistant.Core.Scheduler;
using ScreenAssistant.Overlay;
using ScreenAssistant.ScreenCapture;
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
    private Uri? _keyEndpoint;
    private CancellationTokenSource? _operation;
    private bool _initialized;
    private bool _closing;
    private bool _sourceLost;
    private bool _operationUsesSource;
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
        HistoryList.ItemsSource = _history;
        OverlayEnabled.IsChecked = overlayEnabled;
        Loaded += async (_, _) => await ApplyOverlayVisibilityAsync();
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
        }
        catch (Exception ex)
        {
            LocalFiles.Log("settings-load", ex);
            BaseUrl.Text = AiClient.DefaultBaseUrl;
            Interval.Text = "120";
            Duration.Text = "50";
            SkipIdentical.IsChecked = true;
            UseStreaming.IsChecked = true;
            Status("Настройки не загрузились. Восстановлены значения по умолчанию.", error: true);
        }

        UpdateControls();
        UpdateStats();
    }

    internal void BindOverlaySettings(OverlayViewModel model) => OverlayOptions.DataContext = model;

    private void ResetOverlayPosition_Click(object sender, RoutedEventArgs e)
    {
        if (OverlayOptions.DataContext is OverlayViewModel model)
            model.ResetPosition();
    }

    internal void DisableOverlay() => OverlayEnabled.IsChecked = false;

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
            OverlayEnabled.IsChecked = false;
            Status("Не удалось открыть плавающее окно.", error: true);
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
            Status("Выберите монитор в системном окне…");
            var item = await GraphicsCapture.PickAsync(new WindowInteropHelper(this).Handle, token);
            token.ThrowIfCancellationRequested();
            if (item is null)
            {
                Status("Выбор источника отменён.");
                return;
            }

            DetachSource();
            _source = item;
            item.Closed += Source_Closed;
            SourceText.Text = item.DisplayName;
            ClearPreview();
            Status("Источник выбран.");
        });

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_source is not { } source)
            return;

        await RunOperationAsync("preview", async token =>
        {
            Status("Получаем локальный кадр…");
            await CaptureAsync(source, token);
            Status("Локальный кадр готов.");
        }, usesSource: true);
    }

    private async void Catalog_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync("catalog", async token =>
        {
            Status("Загружаем каталог AI Tunnel…");
            var current = ModelId.Text;
            var models = await _ai.ListVisionModelsAsync(token);
            token.ThrowIfCancellationRequested();
            ModelId.ItemsSource = models;
            ModelId.Text = current;
            Status(models.Length == 0
                ? "В каталоге нет моделей для изображений. Можно ввести ID вручную."
                : $"Моделей с поддержкой изображений: {models.Length}. Откройте список «Модель».");
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

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_source is not { } source || !ReadSettings(out var settings, out var endpoint))
            return;

        var key = ApiKey.Password.Trim();
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

        await RunOperationAsync("session", async token =>
        {
            Status("API проверяет модель на тестовой картинке…");
            await _overlay.BeginAsync("Проверяем модель…", reset: true);
            var (image, colors) = FrameEncoding.CreateVisionProbe();
            await _ai.VerifyVisionAsync(endpoint, key, settings.Model, image, colors, token);
            token.ThrowIfCancellationRequested();

            _history.Clear();
            HistoryEmpty.Visibility = Visibility.Visible;
            AnswerText.Text = "Ответ появится после первого анализа.";
            _lastSuccessfulFingerprint = null;
            _requests = _answers = _skipped = 0;
            UpdateStats();

            var end = await _scheduler.RunAsync(
                TimeSpan.FromSeconds(settings.IntervalSeconds),
                TimeSpan.FromMinutes(settings.DurationMinutes),
                sessionToken => AnalyzeOnceAsync(source, settings, endpoint, key, sessionToken), token);

            var finalStatus = _sourceLost ? "Источник захвата закрыт. Выберите экран повторно."
                : end == SessionEnd.DurationElapsed ? "Время тренировки истекло."
                : "Тренировка остановлена.";
            Status(finalStatus, error: _sourceLost);
            await _overlay.EndAsync(finalStatus, error: _sourceLost);
        }, usesSource: true);
    }

    private async Task<TimeSpan?> AnalyzeOnceAsync(GraphicsCaptureItem source, UserSettings settings,
        Uri endpoint, string key, CancellationToken token)
    {
        Status("Тренировка идёт · получаем кадр…");
        var image = await CaptureAsync(source, token);
        if (settings.SkipIdenticalFrames && image.Fingerprint == _lastSuccessfulFingerprint)
        {
            _skipped++;
            UpdateStats();
            Status("Тренировка идёт · кадр совпал с последним проанализированным.");
            return null;
        }

        Status("Тренировка идёт · ждём ответ API…");
        var requestId = await _overlay.BeginAsync("Ждём ответ модели…");
        _requests++;
        UpdateStats();
        try
        {
            var text = await Task.Run(() => settings.UseStreaming
                ? _ai.AnalyzeStreamingAsync(endpoint, key, settings.Model, image.Payload,
                    delta => token.IsCancellationRequested
                        ? Task.CompletedTask : _overlay.AppendAsync(requestId, delta), token)
                : _ai.AnalyzeAsync(endpoint, key, settings.Model, image.Payload, token), token);
            token.ThrowIfCancellationRequested();
            await _overlay.CompleteAsync(requestId, text);
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
        }
        catch (AiException ex) when (!ex.RequiresConfigurationChange)
        {
            LocalFiles.Log("analysis", ex);
            var next = ex.RetryAfter is { } delay && delay > TimeSpan.Zero
                ? $" Пауза API не менее {Math.Ceiling(delay.TotalSeconds):0} с."
                : " Следующая попытка по интервалу.";
            Status(ex.Message + next, error: true);
            await _overlay.FailAsync(requestId, ex.Message + next);
            return ex.RetryAfter;
        }
        finally { UpdateStats(); }

        return null;
    }

    private async Task<CapturedImage> CaptureAsync(GraphicsCaptureItem source, CancellationToken token)
    {
        CaptureStatus.Text = "Идёт захват";
        CaptureLight.Fill = Active;
        try
        {
            var image = await GraphicsCapture.TakeAsync(source, token);
            token.ThrowIfCancellationRequested();
            PreviewImage.Source = FrameEncoding.Decode(image.Payload);
            PreviewEmpty.Visibility = Visibility.Collapsed;
            FrameDetails.Text = $"{image.Width} × {image.Height}   ·   {DateTime.Now:HH:mm:ss}";
            return image;
        }
        finally
        {
            CaptureStatus.Text = "Захват выключен";
            CaptureLight.Fill = Idle;
        }
    }

    private async Task RunOperationAsync(string name, Func<CancellationToken, Task> action,
        bool usesSource = false)
    {
        if (_operation is not null || _closing)
            return;

        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        _operationUsesSource = usesSource;
        _sourceLost = false;
        UpdateControls();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var message = _sourceLost ? "Источник захвата закрыт. Выберите экран повторно."
                : "Операция остановлена.";
            Status(message, error: _sourceLost);
            if (name == "session")
                await _overlay.EndAsync(message, error: _sourceLost);
        }
        catch (CaptureException ex)
        {
            LocalFiles.Log(name, ex);
            Status(ex.Message, error: true);
            if (name == "session")
                await _overlay.EndAsync(ex.Message, error: true);
        }
        catch (AiException ex)
        {
            LocalFiles.Log(name, ex);
            Status(ex.Message, error: true);
            if (name == "session")
                await _overlay.EndAsync(ex.Message, error: true);
        }
        catch (Exception ex)
        {
            LocalFiles.Log(name, ex);
            const string message = "Операция завершилась ошибкой. Подробности в журнале.";
            Status(message, error: true);
            if (name == "session")
                await _overlay.EndAsync(message, error: true);
        }
        finally
        {
            _operation = null;
            UpdateControls();
            if (_closing)
                Close();
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
            UseStreaming = UseStreaming.IsChecked == true
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

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_operation is not null)
        {
            _closing = true;
            e.Cancel = true;
            _operation.Cancel();
        }
        else
        {
            DetachSource();
            _ = _overlay.ShutdownAsync();
        }
        base.OnClosing(e);
    }

    private sealed record HistoryEntry(DateTimeOffset Time, string Model, string Text)
    {
        public string Label => $"{Time:HH:mm:ss}   {Model}";
    }
}
