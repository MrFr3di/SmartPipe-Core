#nullable enable

namespace SmartPipe.Core;

internal readonly struct PipelineTime
{
    private readonly TimeProvider? _timeProvider;
    private readonly IPipelineClock _clock;

    public PipelineTime(IPipelineClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _timeProvider = clock is TimeProviderPipelineClock providerClock
            ? providerClock.TimeProvider
            : null;
    }

    public long GetTimestamp() => _clock.GetTimestamp();

    public TimeSpan GetElapsedTime(long start, long end) => _clock.GetElapsedTime(start, end);

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero)
            return Task.CompletedTask;

        return _timeProvider is null
            ? Task.Delay(delay, ct)
            : Task.Delay(delay, _timeProvider, ct);
    }

    public CancellationTokenSource CreateCancellationTokenSource(TimeSpan timeout)
    {
        if (timeout == TimeSpan.Zero)
        {
            var canceled = new CancellationTokenSource();
            canceled.Cancel();
            return canceled;
        }

        return _timeProvider is null
            ? new CancellationTokenSource(timeout)
            : new CancellationTokenSource(timeout, _timeProvider);
    }

    public Task WaitAsync(Task task, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (timeout == Timeout.InfiniteTimeSpan)
            return task.WaitAsync(ct);

        return _timeProvider is null
            ? task.WaitAsync(timeout, ct)
            : task.WaitAsync(timeout, _timeProvider, ct);
    }

    public Task<T> WaitAsync<T>(Task<T> task, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (timeout == Timeout.InfiniteTimeSpan)
            return task.WaitAsync(ct);

        return _timeProvider is null
            ? task.WaitAsync(timeout, ct)
            : task.WaitAsync(timeout, _timeProvider, ct);
    }
}
