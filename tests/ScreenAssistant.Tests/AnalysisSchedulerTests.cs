using Microsoft.Extensions.Time.Testing;
using ScreenAssistant.Core.Scheduler;

namespace ScreenAssistant.Tests;

public sealed class AnalysisSchedulerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(50);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RunsImmediatelyAndThenWaitsForTheInterval()
    {
        var clock = new FakeTimeProvider();
        var scheduler = new AnalysisScheduler(clock);
        using var stop = new CancellationTokenSource();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var run = scheduler.RunAsync(Interval, Duration, _ =>
        {
            if (Interlocked.Increment(ref calls) == 2)
                second.SetResult();
            return Task.FromResult<TimeSpan?>(null);
        }, stop.Token);

        Assert.Equal(1, Volatile.Read(ref calls));
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(1, Volatile.Read(ref calls));
        clock.Advance(TimeSpan.FromSeconds(1));
        await second.Task.WaitAsync(TestTimeout);
        stop.Cancel();
        Assert.Equal(SessionEnd.Stopped, await run.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task DoesNotOverlapSlowWorkOrAllowASecondSession()
    {
        var clock = new FakeTimeProvider();
        var scheduler = new AnalysisScheduler(clock);
        using var stop = new CancellationTokenSource();
        var calls = 0;
        async Task<TimeSpan?> Work(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        }

        var run = scheduler.RunAsync(Interval, Duration, Work, stop.Token);
        clock.Advance(Interval * 3);
        Assert.Equal(1, Volatile.Read(ref calls));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scheduler.RunAsync(Interval, Duration, Work, CancellationToken.None));
        stop.Cancel();
        Assert.Equal(SessionEnd.Stopped, await run.WaitAsync(TestTimeout));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task CancelsInFlightWorkAtTheFiftyMinuteDeadline()
    {
        var clock = new FakeTimeProvider();
        var scheduler = new AnalysisScheduler(clock);
        var operationCanceled = false;
        var run = scheduler.RunAsync(Interval, Duration, async token =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { operationCanceled = token.IsCancellationRequested; }
            return null;
        }, CancellationToken.None);

        clock.Advance(Duration);
        Assert.Equal(SessionEnd.DurationElapsed, await run.WaitAsync(TestTimeout));
        Assert.True(operationCanceled);
    }

    [Fact]
    public async Task WaitsForRetryAfterEvenWhenItExceedsTheInterval()
    {
        var clock = new FakeTimeProvider();
        var scheduler = new AnalysisScheduler(clock);
        using var stop = new CancellationTokenSource();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var run = scheduler.RunAsync(Interval, Duration, _ =>
        {
            if (Interlocked.Increment(ref calls) == 2)
                second.SetResult();
            return Task.FromResult<TimeSpan?>(TimeSpan.FromMinutes(5));
        }, stop.Token);

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(1, Volatile.Read(ref calls));
        clock.Advance(TimeSpan.FromMinutes(1));
        await second.Task.WaitAsync(TestTimeout);
        stop.Cancel();
        Assert.Equal(SessionEnd.Stopped, await run.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task AllowsAnotherSessionAfterStop()
    {
        var clock = new FakeTimeProvider();
        var scheduler = new AnalysisScheduler(clock);
        using var firstStop = new CancellationTokenSource();
        var first = scheduler.RunAsync(Interval, Duration, _ => Task.FromResult<TimeSpan?>(null), firstStop.Token);
        firstStop.Cancel();
        Assert.Equal(SessionEnd.Stopped, await first.WaitAsync(TestTimeout));

        using var secondStop = new CancellationTokenSource();
        var calls = 0;
        var second = scheduler.RunAsync(Interval, Duration, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<TimeSpan?>(null);
        }, secondStop.Token);
        Assert.Equal(1, Volatile.Read(ref calls));
        secondStop.Cancel();
        Assert.Equal(SessionEnd.Stopped, await second.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task TriggerAsyncDuringActiveTickDoesNotStartSecondAnalyze()
    {
        var clock = new FakeTimeProvider();
        var scheduler = new AnalysisScheduler(clock);
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var run = scheduler.RunAsync(Interval, Duration, async token =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        }, stop.Token);

        await started.Task.WaitAsync(TestTimeout);
        Assert.False(await scheduler.TriggerAsync(_ => Task.FromResult<TimeSpan?>(null), CancellationToken.None)
            .WaitAsync(TestTimeout));
        Assert.Equal(1, Volatile.Read(ref calls));
        stop.Cancel();
        Assert.Equal(SessionEnd.Stopped, await run.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task TriggerAsyncDuringWaitWakesNextTickWithoutSecondParallelAnalyze()
    {
        var clock = new FakeTimeProvider();
        var scheduler = new AnalysisScheduler(clock);
        using var stop = new CancellationTokenSource();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var run = scheduler.RunAsync(Interval, Duration, _ =>
        {
            if (Interlocked.Increment(ref calls) == 2)
                second.TrySetResult();
            return Task.FromResult<TimeSpan?>(null);
        }, stop.Token);

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.True(await scheduler.TriggerAsync(_ => Task.FromResult<TimeSpan?>(null), CancellationToken.None)
            .WaitAsync(TestTimeout));
        await second.Task.WaitAsync(TestTimeout);
        Assert.Equal(2, Volatile.Read(ref calls));
        stop.Cancel();
        Assert.Equal(SessionEnd.Stopped, await run.WaitAsync(TestTimeout));
    }
}
