#nullable enable

namespace SmartPipe.Core;

internal sealed class TimedAttemptAdmissionLimiter
{
    private readonly SemaphoreSlim _slots;
    private readonly TimedAttemptAdmissionMode _mode;
    private readonly PipelineTime _time;

    public TimedAttemptAdmissionLimiter(TimedAttemptAdmissionOptions options, PipelineTime time)
    {
        _slots = new SemaphoreSlim(options.MaxOutstandingAttempts, options.MaxOutstandingAttempts);
        _mode = options.Mode;
        _time = time;
    }

    public async ValueTask<TimedAttemptLease?> AcquireAsync(TimeSpan? remainingStageTimeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (remainingStageTimeout is { } deadline && deadline <= TimeSpan.Zero && deadline != Timeout.InfiniteTimeSpan)
            throw new TimeoutException("Stage deadline expired while admitting a timed attempt.");
        if (await _slots.WaitAsync(0, ct).ConfigureAwait(false))
            return new TimedAttemptLease(_slots);
        if (_mode == TimedAttemptAdmissionMode.Reject)
            return null;

        using var waiterStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var waiting = _slots.WaitAsync(waiterStop.Token);
        try
        {
            if (remainingStageTimeout is { } timeout)
                await _time.WaitAsync(waiting, timeout, ct).ConfigureAwait(false);
            else
                await waiting.ConfigureAwait(false);
            return new TimedAttemptLease(_slots);
        }
        catch (Exception error)
        {
            // Never leave a ghost waiter after a deadline/cancellation race.
            await waiterStop.CancelAsync().ConfigureAwait(false);
            try
            {
                await waiting.ConfigureAwait(false);
                _slots.Release(); // Acquisition won the race; refund it.
            }
            catch (OperationCanceledException)
            {
                // The linked waiter was cancelled and did not consume a slot.
            }
            if (error is OperationCanceledException && ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            throw;
        }
    }
}

internal sealed class TimedAttemptLease(SemaphoreSlim slots) : IDisposable
{
    private int _released;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            slots.Release();
    }
}

internal readonly record struct AttemptAdmissionResult(
    TimedAttemptLease? Lease = null, bool Rejected = false, bool DeadlineExpired = false);
