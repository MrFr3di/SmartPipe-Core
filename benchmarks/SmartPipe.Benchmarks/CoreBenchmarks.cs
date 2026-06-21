using BenchmarkDotNet.Attributes;
using SmartPipe.Core;

namespace SmartPipe.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("Core")]
public class CoreBenchmarks
{
    private DeduplicationFilter _bloom = null!;
    private ObjectPool<string> _pool = null!;
    private ProcessingEnvelope<int> _envelope = null!;
    private IPipelineTransformer<int, int> _transformer = null!;
    private CircuitBreaker _cb = null!;

    [GlobalSetup]
    public void Setup()
    {
        _bloom = new DeduplicationFilter(1_000_000);
        _pool = new ObjectPool<string>(() => "test", 256);
        _envelope = ProcessingEnvelope<int>.Create(42);
        _transformer = new BenchTransformer();
        _cb = new CircuitBreaker();
    }

    [Benchmark] public bool Bloom_ContainsAndAdd() => _bloom.ContainsAndAdd(42UL);
    [Benchmark] public string ObjectPool_RentReturn() { var o = _pool.Rent()!; _pool.Return(o); return o; }
    [Benchmark] public ProcessingEnvelope<int> New_Envelope() => ProcessingEnvelope<int>.Create(42);
    [Benchmark] public async ValueTask<StageResult<int>> ValueTask_Transform() => await _transformer.TransformAsync(_envelope);
    [Benchmark] public bool SecretScanner_Found() => SecretScanner.HasSecrets("api_key: 'sk-secret'");

    [Benchmark] public bool CircuitBreaker_AllowRequest() => _cb.AllowRequest();
    [Benchmark] public void CircuitBreaker_RecordSuccess() => _cb.RecordSuccess();
    [Benchmark] public void CircuitBreaker_RecordFailure() => _cb.RecordFailure();
}

// SAFETY: Benchmark helper — no secrets, just passes int values through
internal class BenchTransformer : IPipelineTransformer<int, int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        => ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
