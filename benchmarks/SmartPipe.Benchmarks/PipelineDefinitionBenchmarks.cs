using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using SmartPipe.Core;

namespace SmartPipe.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("Definition")]
public class PipelineDefinitionBenchmarks
{
    private PipelineDefinition<int, int> _firstZeroStage = null!;
    private PipelineDefinition<int, int> _firstTenStages = null!;
    private PipelineDefinition<int, int> _cachedZeroStage = null!;
    private PipelineDefinition<int, int> _cachedOneStage = null!;
    private PipelineDefinition<int, int> _cachedTenStages = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _cachedZeroStage = CreateDefinition(0);
        _cachedOneStage = CreateDefinition(1);
        _cachedTenStages = CreateDefinition(10);
        _ = _cachedZeroStage.GetExecutionPlan();
        _ = _cachedOneStage.GetExecutionPlan();
        _ = _cachedTenStages.GetExecutionPlan();

        var result = await StartAndConsumeAsync(_cachedOneStage).ConfigureAwait(false);
        if (result != 42)
            throw new InvalidOperationException($"Definition benchmark expected 42, but received {result}.");
    }

    [IterationSetup(Target = nameof(Compile_First_ZeroStage))]
    public void PrepareFirstZeroStage() => _firstZeroStage = CreateDefinition(0);

    [IterationSetup(Target = nameof(Compile_First_TenStages))]
    public void PrepareFirstTenStages() => _firstTenStages = CreateDefinition(10);

    [Benchmark]
    public PipelineDefinition<int, int> Build_ZeroStage() => CreateDefinition(0);

    [Benchmark]
    public PipelineDefinition<int, int> Build_OneStage() => CreateDefinition(1);

    [Benchmark]
    public PipelineDefinition<int, int> Build_TenStages() => CreateDefinition(10);

    [Benchmark]
    public object Compile_First_ZeroStage() => _firstZeroStage.GetExecutionPlan();

    [Benchmark]
    public object Compile_First_TenStages() => _firstTenStages.GetExecutionPlan();

    [Benchmark]
    public object Compile_Cached_ZeroStage() => _cachedZeroStage.GetExecutionPlan();

    [Benchmark]
    public object Compile_Cached_TenStages() => _cachedTenStages.GetExecutionPlan();

    [Benchmark]
    public Task<int> StartAndComplete_ZeroStage() => StartAndConsumeAsync(_cachedZeroStage);

    [Benchmark]
    public Task<int> StartAndComplete_OneStage() => StartAndConsumeAsync(_cachedOneStage);

    [Benchmark]
    public Task<int> StartAndComplete_TenStages() => StartAndConsumeAsync(_cachedTenStages);

    [Benchmark]
    public Task<int> LegacyBuilder_StartAndComplete_OneStage()
    {
        var run = PipelineBuilder
            .FromFactory<int>(static _ => new DefinitionBenchmarkSource())
            .TransformFactory<int>(static _ => new DefinitionBenchmarkTransformer())
            .Run();
        return ConsumeRunAsync(run);
    }

    private static PipelineDefinition<int, int> CreateDefinition(int stageCount)
    {
        var root = PipelineDefinitionBuilder.From(
            new PipelineKey("definition-benchmark"),
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>(
                static (_, _) => ValueTask.FromResult<IPipelineSource<int>>(
                    new DefinitionBenchmarkSource())));
        if (stageCount == 0)
            return root.Build();

        var builder = root.Transform(
            new PipelineStageKey("stage-1"),
            CreateTransformer());
        for (var index = 2; index <= stageCount; index++)
        {
            builder = builder.Transform(
                new PipelineStageKey($"stage-{index}"),
                CreateTransformer());
        }

        return builder.Build();
    }

    private static PipelineComponent<IPipelineTransformer<int, int>> CreateTransformer() =>
        PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>(
            static (_, _) => ValueTask.FromResult<IPipelineTransformer<int, int>>(
                new DefinitionBenchmarkTransformer()));

    private static async Task<int> StartAndConsumeAsync(
        PipelineDefinition<int, int> definition)
    {
        var run = await definition.StartAsync(CancellationToken.None).ConfigureAwait(false);
        return await ConsumeRunAsync(run).ConfigureAwait(false);
    }

    private static async Task<int> ConsumeRunAsync(PipelineRun<int> run)
    {
        await using (run.ConfigureAwait(false))
        {
            var result = 0;
            await foreach (var output in run.Outputs.ReadAllAsync().ConfigureAwait(false))
            {
                if (output.Result.IsSuccess)
                    result = output.Result.Value;
            }

            await run.Completion.ConfigureAwait(false);
            return result;
        }
    }
}

internal sealed class DefinitionBenchmarkSource : IPipelineSource<int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield return new ProcessingEnvelope<int>
        {
            PipelineId = "definition-benchmark",
            RunId = "definition-benchmark-run",
            TraceId = 1,
            Payload = 42,
            Metadata = MetadataBag.Empty,
            Lineage = [],
            Attempt = 0,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class DefinitionBenchmarkTransformer : IPipelineTransformer<int, int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask<StageResult<int>> TransformAsync(
        ProcessingEnvelope<int> envelope,
        CancellationToken ct = default) =>
        ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
