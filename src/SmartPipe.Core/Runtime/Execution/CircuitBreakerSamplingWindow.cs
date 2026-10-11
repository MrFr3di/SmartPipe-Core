#nullable enable

namespace SmartPipe.Core;

internal readonly record struct CircuitWindowStatistics(long Total, long Failures);

internal interface ICircuitBreakerSamplingWindow
{
    void AddSample(bool isSuccess);
    CircuitWindowStatistics ExpireAndGetStatistics();
    CircuitWindowStatistics GetStatistics();
    void Reset();
}

internal sealed class ExactCircuitBreakerSamplingWindow(TimeSpan samplingDuration, ICircuitBreakerTimeSource timeSource)
    : ICircuitBreakerSamplingWindow
{
    private readonly Queue<(long Timestamp, bool IsSuccess)> _samples = new();
    private readonly object _gate = new();
    private long _failures;

    public void AddSample(bool isSuccess)
    {
        lock (_gate)
        {
            _samples.Enqueue((timeSource.GetTimestamp(), isSuccess));
            if (!isSuccess) _failures++;
        }
    }

    public CircuitWindowStatistics ExpireAndGetStatistics()
    {
        var now = timeSource.GetTimestamp();
        lock (_gate)
        {
            while (_samples.TryPeek(out var sample)
                && timeSource.GetElapsedTime(sample.Timestamp, now) > samplingDuration)
            {
                // Custom clocks can reenter even while a monitor is held.
                if (!_samples.TryPeek(out var current) || current != sample) continue;
                _samples.Dequeue();
                if (!sample.IsSuccess) _failures--;
            }
            return new CircuitWindowStatistics(_samples.Count, _failures);
        }
    }

    public CircuitWindowStatistics GetStatistics()
    {
        lock (_gate) return new CircuitWindowStatistics(_samples.Count, _failures);
    }

    internal long RetainedStorageBytes
    {
        get { lock (_gate) return (long)_samples.EnsureCapacity(0) * System.Runtime.CompilerServices.Unsafe.SizeOf<(long, bool)>(); }
    }

    public void Reset()
    {
        lock (_gate) { _samples.Clear(); _failures = 0; }
    }
}

internal sealed class BucketedCircuitBreakerSamplingWindow : ICircuitBreakerSamplingWindow
{
    private struct Bucket
    {
        public long Sequence, Total, Failures;
    }

    private readonly Bucket[] _buckets;
    private readonly object _gate = new();
    private readonly long _durationTicks, _widthTicks;
    private readonly ICircuitBreakerTimeSource _time;
    private bool _initialized;
    private long _origin, _lastElapsed, _nextToExpire, _total, _failures;

    public BucketedCircuitBreakerSamplingWindow(TimeSpan samplingDuration, int bucketCount, ICircuitBreakerTimeSource timeSource)
    {
        if (samplingDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(samplingDuration));
        if (bucketCount is < 2 or > 4096) throw new ArgumentOutOfRangeException(nameof(bucketCount));
        _time = timeSource ?? throw new ArgumentNullException(nameof(timeSource));
        _durationTicks = samplingDuration.Ticks;
        // Division and remainder avoid overflow at TimeSpan.MaxValue.
        _widthTicks = _durationTicks / bucketCount + (_durationTicks % bucketCount == 0 ? 0 : 1);
        _buckets = new Bucket[bucketCount + 1];
    }

    internal int StorageSlotCount => _buckets.Length;
    internal long RetainedStorageBytes => (long)_buckets.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<Bucket>();

    public void AddSample(bool isSuccess)
    {
        lock (_gate)
        {
            var elapsed = ReadElapsed();
            Expire(elapsed);
            var sequence = elapsed / _widthTicks;
            ref var bucket = ref _buckets[(int)(sequence % _buckets.Length)];
            if (bucket.Sequence != sequence)
            {
                _total -= bucket.Total;
                _failures -= bucket.Failures;
                bucket = new Bucket { Sequence = sequence };
            }
            var total = checked(_total + 1);
            var bucketTotal = checked(bucket.Total + 1);
            var failures = checked(_failures + (isSuccess ? 0 : 1));
            var bucketFailures = checked(bucket.Failures + (isSuccess ? 0 : 1));
            _total = total;
            _failures = failures;
            bucket.Total = bucketTotal;
            bucket.Failures = bucketFailures;
        }
    }

    public CircuitWindowStatistics ExpireAndGetStatistics()
    {
        lock (_gate)
        {
            Expire(ReadElapsed());
            return new CircuitWindowStatistics(_total, _failures);
        }
    }

    public CircuitWindowStatistics GetStatistics()
    {
        lock (_gate) return new CircuitWindowStatistics(_total, _failures);
    }

    private long ReadElapsed()
    {
        var now = _time.GetTimestamp();
        if (!_initialized)
        {
            _origin = now;
            _initialized = true;
            return _lastElapsed = 0;
        }
        var elapsed = _time.GetElapsedTime(_origin, now).Ticks;
        if (elapsed < _lastElapsed) throw new InvalidOperationException("Bucketed sampling requires a monotonic time source.");
        return _lastElapsed = elapsed;
    }

    private void Expire(long elapsed)
    {
        // Keep the oldest partially overlapping bucket. Its temporal overhang
        // is less than one bucket width; its failure ratio contribution is unbounded.
        var oldestKept = elapsed >= _durationTicks ? (elapsed - _durationTicks) / _widthTicks : 0;
        if (oldestKept - _nextToExpire >= _buckets.Length)
        {
            Array.Clear(_buckets);
            _total = _failures = 0;
        }
        else
        {
            for (var sequence = _nextToExpire; sequence < oldestKept; sequence++)
            {
                ref var bucket = ref _buckets[(int)(sequence % _buckets.Length)];
                if (bucket.Sequence != sequence) continue;
                _total -= bucket.Total;
                _failures -= bucket.Failures;
                bucket = default;
            }
        }
        _nextToExpire = oldestKept;
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_buckets);
            _initialized = false;
            _origin = _lastElapsed = _nextToExpire = _total = _failures = 0;
        }
    }
}
