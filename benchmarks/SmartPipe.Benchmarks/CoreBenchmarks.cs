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

    [GlobalSetup]
    public void Setup()
    {
        _bloom = new DeduplicationFilter(1_000_000);
        _pool = new ObjectPool<string>(() => "test", 256);
        _ctx = new ProcessingContext<int>(42);
        _transformer = new BenchTransformer();
    }

    [Benchmark]
    public bool Bloom_ContainsAndAdd() => _bloom.ContainsAndAdd(42UL);

    [Benchmark]
    public string ObjectPool_RentReturn()
    {
        var obj = _pool.Rent()!;
        _pool.Return(obj);
        return obj;
    }

    [Benchmark]
    public ProcessingContext<int> New_Context() => new(42);

    [Benchmark]
    public async ValueTask<ProcessingResult<int>> ValueTask_Transform() 
        => await _transformer.TransformAsync(_ctx);

    [Benchmark]
    public bool SecretScanner_Found() 
        => SecretScanner.HasSecrets("api_key: 'sk-secret'");
}

internal class BenchTransformer : ITransformer<int, int>
{
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask<ProcessingResult<int>> TransformAsync(ProcessingContext<int> ctx, CancellationToken ct = default)
        => ValueTask.FromResult(ProcessingResult<int>.Success(ctx.Payload, ctx.TraceId));
    public Task DisposeAsync() => Task.CompletedTask;
}
