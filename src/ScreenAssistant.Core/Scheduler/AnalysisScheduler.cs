namespace ScreenAssistant.Core.Scheduler;

public enum SessionEnd { Stopped, DurationElapsed }

public sealed class AnalysisScheduler(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private int _running;
    private int _analyzing;
    private int _pendingWake;
    private TaskCompletionSource? _wake;

    public async Task<SessionEnd> RunAsync(
        TimeSpan interval, TimeSpan duration,
        Func<CancellationToken, Task<TimeSpan?>> analyze, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("Сессия уже запущена.");

        try
        {
            using var deadline = new CancellationTokenSource(duration, _clock);
            using var session = CancellationTokenSource.CreateLinkedTokenSource(
                deadline.Token, cancellationToken);

            try
            {
                while (true)
                {
                    session.Token.ThrowIfCancellationRequested();
                    var started = _clock.GetTimestamp();
                    var cooldown = await RunAnalyzeAsync(analyze, session.Token).ConfigureAwait(false)
                        ?? TimeSpan.Zero;
                    var wait = interval - _clock.GetElapsedTime(started);
                    if (cooldown > wait)
                        wait = cooldown;

                    if (wait > TimeSpan.Zero)
                    {
                        var wake = new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        Volatile.Write(ref _wake, wake);
                        if (Interlocked.Exchange(ref _pendingWake, 0) == 1)
                            wake.TrySetResult();
                        try
                        {
                            var delay = Task.Delay(
                                wait < duration ? wait : duration, _clock, session.Token);
                            await Task.WhenAny(delay, wake.Task).ConfigureAwait(false);
                            session.Token.ThrowIfCancellationRequested();
                        }
                        finally
                        {
                            Interlocked.CompareExchange(ref _wake, null, wake);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
                return cancellationToken.IsCancellationRequested
                    ? SessionEnd.Stopped
                    : SessionEnd.DurationElapsed;
            }
        }
        finally
        {
            Volatile.Write(ref _wake, null);
            Volatile.Write(ref _pendingWake, 0);
            Volatile.Write(ref _running, 0);
        }
    }

    public async Task<bool> TriggerAsync(
        Func<CancellationToken, Task<TimeSpan?>> analyze, CancellationToken token)
    {
        if (Volatile.Read(ref _running) == 1)
        {
            if (Volatile.Read(ref _analyzing) == 1)
                return false;

            if (Volatile.Read(ref _wake) is { } wake)
                wake.TrySetResult();
            else
                Interlocked.Exchange(ref _pendingWake, 1);
            return true;
        }

        if (Interlocked.CompareExchange(ref _analyzing, 1, 0) != 0)
            return false;

        try
        {
            await analyze(token).ConfigureAwait(false);
            return true;
        }
        finally
        {
            Volatile.Write(ref _analyzing, 0);
        }
    }

    private async Task<TimeSpan?> RunAnalyzeAsync(
        Func<CancellationToken, Task<TimeSpan?>> analyze, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _analyzing, 1, 0) != 0)
            return null;

        try
        {
            return await analyze(token).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _analyzing, 0);
        }
    }
}
