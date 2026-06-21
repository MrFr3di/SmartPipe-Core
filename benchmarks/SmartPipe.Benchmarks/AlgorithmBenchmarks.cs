using BenchmarkDotNet.Attributes;
using SmartPipe.Core;

namespace SmartPipe.Benchmarks;

[MemoryDiagnoser]
public class AlgorithmBenchmarks
{
    private CircuitBreaker _cb = null!;
    private BackpressureStrategy _bp = null!;

    [GlobalSetup]
    public void Setup()
    {
        _cb = new CircuitBreaker();
        _bp = new BackpressureStrategy(1000);
    }

    [Benchmark]
    public bool CircuitBreaker_AllowRequest() => _cb.AllowRequest();

    [Benchmark]
    public void CircuitBreaker_RecordSuccess() => _cb.RecordSuccess();

    [Benchmark]
    public void CircuitBreaker_RecordFailure() => _cb.RecordFailure();

    [Benchmark]
    public void Backpressure_Throttle()
    {
        _bp.UpdateThroughput(500);
        _bp.ThrottleAsync(500, CancellationToken.None).GetAwaiter().GetResult();
    }
}
