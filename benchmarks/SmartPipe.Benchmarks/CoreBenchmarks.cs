using BenchmarkDotNet.Attributes;
using SmartPipe.Core;

namespace SmartPipe.Benchmarks;

[MemoryDiagnoser]
public class CoreBenchmarks
{
    private DeduplicationFilter _bloom = null!;
    private ObjectPool<string> _pool = null!;
    private ProcessingContext<int> _ctx = null!;
    private ITransformer<int, int> _transformer = null!;
    private AdaptiveMetrics _metrics = null!;
    private CircuitBreaker _cb = null!;

    [GlobalSetup]
    public void Setup()
    {
        _bloom = new DeduplicationFilter(1_000_000);
        _pool = new ObjectPool<string>(() => "test", 256);
        _ctx = new ProcessingContext<int>(42);
        _transformer = new BenchTransformer();
        _metrics = new AdaptiveMetrics();
        _cb = new CircuitBreaker();
    }

    [Benchmark] public bool Bloom_ContainsAndAdd() => _bloom.ContainsAndAdd(42UL);
    [Benchmark] public string ObjectPool_RentReturn() { var o = _pool.Rent()!; _pool.Return(o); return o; }
    [Benchmark] public ProcessingContext<int> New_Context() => new(42);
    [Benchmark] public async ValueTask<ProcessingResult<int>> ValueTask_Transform() => await _transformer.TransformAsync(_ctx);
    [Benchmark] public bool SecretScanner_Found() => SecretScanner.HasSecrets("api_key: 'sk-secret'");
    
    // New in v1.0.4
    [Benchmark] public void AdaptiveMetrics_Update() => _metrics.Update(10.0);
    [Benchmark] public double AdaptiveMetrics_Predict() => _metrics.PredictNextLatency();
    [Benchmark] public bool CircuitBreaker_AllowRequest() => _cb.AllowRequest();
    [Benchmark] public void CircuitBreaker_RecordSuccess() => _cb.RecordSuccess();
    [Benchmark] public void CircuitBreaker_RecordFailure() => _cb.RecordFailure();
}

// SAFETY: Benchmark helper — no secrets, just passes int values through
internal class BenchTransformer : ITransformer<int, int>
{
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask<ProcessingResult<int>> TransformAsync(ProcessingContext<int> ctx, CancellationToken ct = default)
        => ValueTask.FromResult(ProcessingResult<int>.Success(ctx.Payload, ctx.TraceId));
    public Task DisposeAsync() => Task.CompletedTask;
}
