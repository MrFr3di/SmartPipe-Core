#nullable enable

namespace SmartPipe.Core;

internal readonly struct PipelineTime
{
    private readonly TimeProvider? _timeProvider;

    public PipelineTime(IPipelineClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _timeProvider = clock is TimeProviderPipelineClock providerClock
            ? providerClock.TimeProvider
            : null;
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero)
            return Task.CompletedTask;

        return _timeProvider is null
            ? Task.Delay(delay, ct)
            : Task.Delay(delay, _timeProvider, ct);
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
