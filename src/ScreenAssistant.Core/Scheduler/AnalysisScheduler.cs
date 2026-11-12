namespace ScreenAssistant.Core.Scheduler;

public enum SessionEnd { Stopped, DurationElapsed }

public sealed class AnalysisScheduler(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private int _running;

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
                    var cooldown = await analyze(session.Token) ?? TimeSpan.Zero;
                    var wait = interval - _clock.GetElapsedTime(started);
                    if (cooldown > wait)
                        wait = cooldown;

                    if (wait > TimeSpan.Zero)
                    {
                        // Long Retry-After values can exceed the timer's maximum delay.
                        await Task.Delay(
                            wait < duration ? wait : duration, _clock, session.Token);
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
            Volatile.Write(ref _running, 0);
        }
    }
}
