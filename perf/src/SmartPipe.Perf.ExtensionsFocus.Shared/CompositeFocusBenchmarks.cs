using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Perf.ExtensionsFocus;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[MemoryDiagnoser]
[BenchmarkCategory("Comparative", "StrictAB", "SP220-07-Focus")]
public class CompositeFocusBenchmarks
{
    private readonly ProcessingEnvelope<int> _envelope =
        ProcessingEnvelope<int>.Create(42, "perf-sp22007-focus", "run", 1);

    private CompositeTransform<int>? _zero;
    private CompositeTransform<int>? _one;
    private CompositeTransform<int>? _three;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _zero = new CompositeTransform<int>();
        _one = new CompositeTransform<int>(new IncrementTransform());
        _three = new CompositeTransform<int>(
            new IncrementTransform(),
            new IncrementTransform(),
            new IncrementTransform());

        await _zero.InitializeAsync().ConfigureAwait(false);
        await _one.InitializeAsync().ConfigureAwait(false);
        await _three.InitializeAsync().ConfigureAwait(false);

        AssertSuccess(ZeroChildren(), 42, nameof(ZeroChildren));
        AssertSuccess(OneChild(), 43, nameof(OneChild));
        AssertSuccess(ThreeChildren(), 45, nameof(ThreeChildren));
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_three is not null)
            await _three.DisposeAsync().ConfigureAwait(false);
        if (_one is not null)
            await _one.DisposeAsync().ConfigureAwait(false);
        if (_zero is not null)
            await _zero.DisposeAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public StageResult<int> ZeroChildren() =>
        Zero.TransformAsync(_envelope).GetAwaiter().GetResult();

    [Benchmark]
    public StageResult<int> OneChild() =>
        One.TransformAsync(_envelope).GetAwaiter().GetResult();

    [Benchmark]
    public StageResult<int> ThreeChildren() =>
        Three.TransformAsync(_envelope).GetAwaiter().GetResult();

    private CompositeTransform<int> Zero =>
        _zero ?? throw new InvalidOperationException("Zero-child composite is not initialized.");

    private CompositeTransform<int> One =>
        _one ?? throw new InvalidOperationException("One-child composite is not initialized.");

    private CompositeTransform<int> Three =>
        _three ?? throw new InvalidOperationException("Three-child composite is not initialized.");

    private static void AssertSuccess(StageResult<int> result, int expected, string operation)
    {
        if (!result.IsSuccess || result.Value != expected)
            throw new InvalidOperationException(
                $"{operation} expected {expected}, got kind={result.Kind}, value={result.Value}.");
    }

    private sealed class IncrementTransform : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<int>.Success(envelope.Payload + 1));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
