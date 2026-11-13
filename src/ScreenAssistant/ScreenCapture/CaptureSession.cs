using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using ScreenAssistant.Core.Geometry;

namespace ScreenAssistant.ScreenCapture;

internal sealed class CaptureSession : IAsyncDisposable
{
    private const DirectXPixelFormat Format = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private readonly Channel<Direct3D11CaptureFrame> _frames = Channel.CreateBounded<Direct3D11CaptureFrame>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    private readonly object _gate = new();

    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private Windows.Graphics.SizeInt32 _size;
    private int _state; // 0 idle, 1 started, 2 disposing/disposed

    public async Task StartAsync(GraphicsCaptureItem item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state != 0)
                return;

            var size = item.Size;
            if (size.Width <= 0 || size.Height <= 0)
                throw new CaptureException("Выбранный источник сейчас недоступен.");

            _device = D3DDevice.Create();
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, Format, 2, size);
            _session = _pool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = false;
            TryDisableBorder(_session);
            _size = size;
            _pool.FrameArrived += FrameArrived;
            _session.StartCapture();
            _state = 1;
        }

        await Task.CompletedTask;
    }

    public async Task<CapturedImage> TakeAsync(CropRect? crop, CancellationToken token)
    {
        Direct3D11CaptureFramePool pool;
        IDirect3DDevice device;
        lock (_gate)
        {
            if (_state != 1 || _pool is null || _device is null)
                throw new CaptureException("Сессия захвата не запущена.");
            pool = _pool;
            device = _device;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var bitmap = await CopyFrameAsync(pool, device, timeout.Token);
            return await FrameEncoding.EncodeAsync(bitmap, crop, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new CaptureException("Кадр не получен за 10 секунд. Проверьте источник захвата.");
        }
        catch (Exception ex) when (ex is not (CaptureException or OperationCanceledException))
        {
            throw new CaptureException("Захват прервался. Выберите экран повторно.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Direct3D11CaptureFramePool? pool;
        GraphicsCaptureSession? session;
        IDirect3DDevice? device;
        lock (_gate)
        {
            if (_state == 2)
                return;
            _state = 2;
            pool = _pool;
            session = _session;
            device = _device;
            _pool = null;
            _session = null;
            _device = null;
        }

        if (pool is not null)
            pool.FrameArrived -= FrameArrived;

        while (_frames.Reader.TryRead(out var leftover))
            leftover.Dispose();

        try { session?.Dispose(); }
        catch { /* ignore teardown races */ }
        try { pool?.Dispose(); }
        catch { /* ignore teardown races */ }
        try { device?.Dispose(); }
        catch { /* ignore teardown races */ }

        await Task.CompletedTask;
    }

    private async Task<SoftwareBitmap> CopyFrameAsync(
        Direct3D11CaptureFramePool pool, IDirect3DDevice device, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _state) != 1)
                throw new CaptureException("Сессия захвата закрыта.");

            var frame = await _frames.Reader.ReadAsync(token);
            using (frame)
            {
                var content = frame.ContentSize;
                if (content.Width <= 0 || content.Height <= 0)
                    continue;

                Windows.Graphics.SizeInt32 size;
                lock (_gate)
                {
                    if (_state != 1)
                        throw new CaptureException("Сессия захвата закрыта.");
                    size = _size;
                }

                if (content.Width == size.Width && content.Height == size.Height)
                {
                    return await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                        frame.Surface, BitmapAlphaMode.Ignore).AsTask(token);
                }

                lock (_gate)
                {
                    if (_state != 1 || _pool is null || _device is null)
                        throw new CaptureException("Сессия захвата закрыта.");
                    _size = content;
                    _pool.Recreate(_device, Format, 2, _size);
                }
            }
        }
    }

    private void FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (Volatile.Read(ref _state) != 1)
            return;

        var frame = sender.TryGetNextFrame();
        if (frame is null)
            return;

        while (!_frames.Writer.TryWrite(frame))
        {
            if (_frames.Reader.TryRead(out var old))
                old.Dispose();
            else
            {
                frame.Dispose();
                return;
            }
        }
    }

    private static void TryDisableBorder(GraphicsCaptureSession session)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;

        try
        {
            session.GetType().GetProperty("IsBorderRequired")?.SetValue(session, false);
        }
        catch (Exception ex) when (ex is COMException or NotImplementedException or InvalidCastException
            or TargetInvocationException)
        {
        }
    }
}

internal static class GraphicsCapture
{
    public static async Task<GraphicsCaptureItem?> PickAsync(nint hwnd, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new CaptureException("Для захвата нужна Windows 10 версии 2004 или новее.");
        if (!GraphicsCaptureSession.IsSupported())
            throw new CaptureException("Windows Graphics Capture недоступен на этом устройстве.");

        var picker = new GraphicsCapturePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return await picker.PickSingleItemAsync().AsTask(token);
    }

    public static async Task<CapturedImage> TakeOnceAsync(GraphicsCaptureItem item, CropRect? crop,
        CancellationToken token)
    {
        await using var session = new CaptureSession();
        await session.StartAsync(item, token);
        return await session.TakeAsync(crop, token);
    }
}
