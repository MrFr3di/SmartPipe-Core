#nullable enable

namespace SmartPipe.Core;

internal sealed class RunRetryBudget
{
    private readonly RetryBudgetOptions _options;
    private readonly PipelineTime _time;
    private readonly object _gate = new();
    private long _updatedAt;
    private double _available;
    private int _reserved;

    public RunRetryBudget(RetryBudgetOptions options, PipelineTime time)
    {
        _options = options;
        _time = time;
        _available = options.Capacity;
        _updatedAt = time.GetTimestamp();
    }

    public bool TryReserve(out RetryBudgetLease? lease)
    {
        lock (_gate)
        {
            Refill();
            if (_available < 1)
            {
                lease = null;
                return false;
            }
            lease = new RetryBudgetLease(this);
            _available--;
            _reserved++;
            return true;
        }
    }

    internal void Complete(bool refund)
    {
        lock (_gate)
        {
            // Advance the clock before opening refill capacity. Time spent with
            // reserved capacity cannot be banked for a burst after commit/refund.
            Refill();
            _reserved--;
            if (refund)
                _available = Math.Min(_available + 1, _options.Capacity - _reserved);
        }
    }

    private void Refill()
    {
        var now = _time.GetTimestamp();
        var seconds = Math.Max(0, _time.GetElapsedTime(_updatedAt, now).TotalSeconds);
        _updatedAt = now;
        _available = Math.Min(_options.Capacity - _reserved,
            _available + seconds * _options.RefillTokensPerSecond);
    }
}

internal sealed class RetryBudgetLease(RunRetryBudget budget) : IDisposable
{
    private int _completed;

    public void Commit()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            budget.Complete(refund: false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            budget.Complete(refund: true);
    }
}
