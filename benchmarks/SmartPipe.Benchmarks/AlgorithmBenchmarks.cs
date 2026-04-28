using BenchmarkDotNet.Attributes;
using SmartPipe.Core;

namespace SmartPipe.Benchmarks;

[MemoryDiagnoser]
public class AlgorithmBenchmarks
{
    private AdaptiveParallelism _ap = null!;
    private AdaptiveMetrics _am = null!;
    private CircuitBreaker _cb = null!;
    private BackpressureStrategy _bp = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ap = new AdaptiveParallelism(2, 16);
        _am = new AdaptiveMetrics();
        _cb = new CircuitBreaker();
        _bp = new BackpressureStrategy(1000);
    }

    [Benchmark]
    public void AdaptiveParallelism_Update() => _ap.Update(10.0, 5);

    [Benchmark]
    public void AdaptiveMetrics_Update() => _am.Update(10.0);

    [Benchmark]
    public double AdaptiveMetrics_Predict() => _am.PredictNextLatency();

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
