using System.Threading.Channels;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using WinRT.Interop;

namespace ScreenAssistant.ScreenCapture;

internal sealed class CaptureException(string message, Exception? inner = null)
    : Exception(message, inner);

internal static class GraphicsCapture
{
    public static async Task<GraphicsCaptureItem?> PickAsync(nint hwnd, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new CaptureException("Для захвата нужна Windows 10 версии 2004 или новее.");
        if (!GraphicsCaptureSession.IsSupported())
            throw new CaptureException("Windows Graphics Capture недоступен на этом устройстве.");

        var picker = new GraphicsCapturePicker();
        InitializeWithWindow.Initialize(picker, hwnd);
        return await picker.PickSingleItemAsync().AsTask(token);
    }

    public static Task<CapturedImage> TakeAsync(GraphicsCaptureItem item, CancellationToken token) =>
        Task.Run(async () =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            try
            {
                using var bitmap = await CopyFrameAsync(item, timeout.Token);
                return await FrameEncoding.EncodeAsync(bitmap, timeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new CaptureException("Кадр не получен за 10 секунд. Проверьте источник захвата.");
            }
            catch (Exception ex) when (ex is not (CaptureException or OperationCanceledException))
            {
                throw new CaptureException("Захват прервался. Выберите экран повторно.", ex);
            }
        }, token);

    private static async Task<SoftwareBitmap> CopyFrameAsync(GraphicsCaptureItem item,
        CancellationToken token)
    {
        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0)
            throw new CaptureException("Выбранный источник сейчас недоступен.");

        const DirectXPixelFormat format = DirectXPixelFormat.B8G8R8A8UIntNormalized;
        using var device = D3DDevice.Create();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, format, 2, size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;

        // Only signal here. Frame ownership stays with the async consumer.
        var signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        void FrameArrived(Direct3D11CaptureFramePool sender, object args) => signals.Writer.TryWrite(0);
        pool.FrameArrived += FrameArrived;
        try
        {
            session.StartCapture();
            while (true)
            {
                await signals.Reader.ReadAsync(token);
                var frame = pool.TryGetNextFrame();
                if (frame is null)
                    continue;

                using (frame)
                {
                    var content = frame.ContentSize;
                    if (content.Width <= 0 || content.Height <= 0)
                        continue;
                    if (content.Width == size.Width && content.Height == size.Height)
                    {
                        return await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                            frame.Surface, BitmapAlphaMode.Ignore).AsTask(token);
                    }
                    size = content;
                }

                pool.Recreate(device, format, 2, size);
            }
        }
        finally
        {
            pool.FrameArrived -= FrameArrived;
        }
    }
}
