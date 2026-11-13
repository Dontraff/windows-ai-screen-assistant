namespace ScreenAssistant.ScreenCapture;

internal sealed class CaptureException(string message, Exception? inner = null)
    : Exception(message, inner);
