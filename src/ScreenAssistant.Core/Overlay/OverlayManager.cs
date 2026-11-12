namespace ScreenAssistant.Core.Overlay;

public sealed class OverlayManager(IOverlayDispatcher dispatcher, IOverlayRenderer renderer)
    : IOverlayService
{
    private OverlayState _state = new("Готово к тренировке", "", OverlayPhase.Idle);
    private string _lastAnswer = "";
    private long _request;
    private bool _active;
    private bool _visible;
    private bool _closed;

    public Task SetVisibleAsync(bool visible) => dispatcher.InvokeAsync(() =>
    {
        if (_closed || visible == _visible)
            return;

        if (visible)
            renderer.Render(_state);

        renderer.SetVisible(visible);
        _visible = visible;
    });

    public async Task<long> BeginAsync(string status, bool reset = false)
    {
        long id = 0;
        await dispatcher.InvokeAsync(() =>
        {
            if (_closed)
                return;

            id = ++_request;
            _active = true;
            if (reset)
                _lastAnswer = "";

            Update(new(status, _lastAnswer, OverlayPhase.Loading));
        }).ConfigureAwait(false);
        return id;
    }

    public Task AppendAsync(long requestId, string delta) => dispatcher.InvokeAsync(() =>
    {
        if (!IsCurrent(requestId) || delta.Length == 0)
            return;

        var prior = _state.Phase == OverlayPhase.Streaming ? _state.Text : "";
        Update(new("AI отвечает…", prior + delta, OverlayPhase.Streaming));
    });

    public Task CompleteAsync(long requestId, string text) => dispatcher.InvokeAsync(() =>
    {
        if (!IsCurrent(requestId))
            return;

        _active = false;
        _lastAnswer = text;
        Update(new("Ответ получен", text, OverlayPhase.Ready));
    });

    public Task FailAsync(long requestId, string status) => dispatcher.InvokeAsync(() =>
    {
        if (IsCurrent(requestId))
            Finish(status, error: true);
    });

    public Task EndAsync(string status, bool error = false) => dispatcher.InvokeAsync(() =>
    {
        if (_closed)
            return;

        Finish(status, error);
    });

    public Task ShutdownAsync() => dispatcher.InvokeAsync(() =>
    {
        if (_closed)
            return;

        _closed = true;
        _active = false;
        if (_visible)
            renderer.SetVisible(false);
        _visible = false;
    });

    private void Finish(string status, bool error)
    {
        _active = false;
        Update(new(status, _lastAnswer, error ? OverlayPhase.Error : OverlayPhase.Idle));
    }

    private bool IsCurrent(long id) => !_closed && _active && id == _request;

    private void Update(OverlayState state)
    {
        _state = state;
        if (_visible)
            renderer.Render(state);
    }
}
