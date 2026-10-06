using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Definitions;

public sealed class PipelineDefinitionCompilerTests
{
    [Fact]
    public void Compiler_RejectsDefaultPipelineKey()
    {
        var state = CreateState<int>(default(PipelineKey));

        var act = () => PipelineDefinitionCompiler.Compile(state, sink: null);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Compiler_RejectsDuplicateStageKeyWithIndexesAndKey()
    {
        var duplicate = new PipelineStageKey("normalize");
        var state = CreateState<int>(
            new PipelineKey("orders"),
            new IPipelineStageDescriptor[]
            {
                CreateStage<int>(duplicate),
                CreateStage<int>(duplicate),
            });

        var act = () => PipelineDefinitionCompiler.Compile(state, sink: null);

        var error = act.Should().Throw<InvalidOperationException>().Which;
        error.Message.Should().Contain("normalize");
        error.Message.Should().Contain("0");
        error.Message.Should().Contain("1");
    }

    [Fact]
    public void Compiler_RejectsOutputGenericMismatchWithoutInvokingFactories()
    {
        var stageCalls = 0;
        var state = CreateState<int, int>(
            new PipelineKey("orders"),
            new IPipelineStageDescriptor[]
            {
                CreateStage<int, string>(new PipelineStageKey("format"), () => stageCalls++),
            });

        var act = () => PipelineDefinitionCompiler.Compile(state, sink: null);

        act.Should().Throw<ArgumentException>();
        stageCalls.Should().Be(0);
    }

    [Fact]
    public void Compiler_IsPureAndNeverInvokesFactories()
    {
        var sourceCalls = 0;
        var stageCalls = 0;
        var state = CreateState<int>(
            new PipelineKey("orders"),
            new IPipelineStageDescriptor[]
            {
                CreateStage<int>(new PipelineStageKey("normalize"), () => stageCalls++),
            },
            () => sourceCalls++);

        _ = PipelineDefinitionCompiler.Compile(state, sink: null);

        sourceCalls.Should().Be(0);
        stageCalls.Should().Be(0);
    }

    [Fact]
    public void Compiler_RecordsServiceRequirementForEveryScopeOwnedRole()
    {
        var defaults = PipelineRuntimeOptionsSnapshot.Create(new PipelineRuntimeOptions());
        var sourceState = new PipelineDefinitionState<int, int>(
            new PipelineKey("source"),
            ScopeSource<int>(),
            [],
            [],
            defaults,
            LineageMode.Minimal);
        var stageState = CreateState<int>(
            new PipelineKey("stage"),
            [CreateScopeStage<int>(new PipelineStageKey("normalize"))]);
        var sinkState = CreateState<int>(new PipelineKey("sink"));
        var runtimeState = CreateState<int>(new PipelineKey("runtime"));

        PipelineDefinitionCompiler.Compile(sourceState, sink: null)
            .RequiresServices.Should().BeTrue();
        PipelineDefinitionCompiler.Compile(stageState, sink: null)
            .RequiresServices.Should().BeTrue();
        PipelineDefinitionCompiler.Compile(sinkState, ScopeSink<int>())
            .RequiresServices.Should().BeTrue();
        PipelineDefinitionCompiler.Compile(runtimeState, sink: null)
            .RequiresServices.Should().BeFalse();
    }

    private static PipelineDefinitionState<TInput, TOutput> CreateState<TInput, TOutput>(
        PipelineKey key,
        IPipelineStageDescriptor[]? stages = null,
        Action? sourceFactoryCalled = null)
    {
        return new(
            key,
            RuntimeSource<TInput>(sourceFactoryCalled),
            stages ?? [],
            [],
            PipelineRuntimeOptionsSnapshot.Create(new PipelineRuntimeOptions()),
            LineageMode.Minimal);
    }

    private static PipelineDefinitionState<T, T> CreateState<T>(
        PipelineKey key,
        IPipelineStageDescriptor[]? stages = null,
        Action? sourceFactoryCalled = null) =>
        CreateState<T, T>(key, stages, sourceFactoryCalled);

    private static IPipelineStageDescriptor CreateStage<T>(
        PipelineStageKey key,
        Action? factoryCalled = null) =>
        CreateStage<T, T>(key, factoryCalled);

    private static IPipelineStageDescriptor CreateStage<TInput, TOutput>(
        PipelineStageKey key,
        Action? factoryCalled = null)
    {
        return new PipelineStageDescriptor<TInput, TOutput>(
            key,
            RuntimeTransformer<TInput, TOutput>(factoryCalled),
            StageFailureOptionsSnapshot.Create(StageFailureOptions.Default),
            deadLetterOptions: null,
            name: key.Value);
    }

    private static PipelineComponent<IPipelineSource<T>> RuntimeSource<T>(Action? onCreate = null) =>
        PipelineComponent.RuntimeOwned<IPipelineSource<T>>((_, _) =>
        {
            onCreate?.Invoke();
            return ValueTask.FromResult<IPipelineSource<T>>(new TestSource<T>());
        });

    private static PipelineComponent<IPipelineTransformer<TInput, TOutput>> RuntimeTransformer<TInput, TOutput>(Action? onCreate = null) =>
        PipelineComponent.RuntimeOwned<IPipelineTransformer<TInput, TOutput>>((_, _) =>
        {
            onCreate?.Invoke();
            return ValueTask.FromResult<IPipelineTransformer<TInput, TOutput>>(new TestTransformer<TInput, TOutput>());
        });

    private static IPipelineStageDescriptor CreateScopeStage<T>(PipelineStageKey key) =>
        new PipelineStageDescriptor<T, T>(
            key,
            PipelineComponent.ScopeOwned<IPipelineTransformer<T, T>>((_, _) =>
                ValueTask.FromResult<IPipelineTransformer<T, T>>(new TestTransformer<T, T>())),
            StageFailureOptionsSnapshot.Create(StageFailureOptions.Default),
            deadLetterOptions: null,
            name: key.Value);

    private static PipelineComponent<IPipelineSource<T>> ScopeSource<T>() =>
        PipelineComponent.ScopeOwned<IPipelineSource<T>>((_, _) =>
            ValueTask.FromResult<IPipelineSource<T>>(new TestSource<T>()));

    private static PipelineComponent<IPipelineSink<T>> ScopeSink<T>() =>
        PipelineComponent.ScopeOwned<IPipelineSink<T>>((_, _) =>
            ValueTask.FromResult<IPipelineSink<T>>(new TestSink<T>()));

    private sealed class TestSource<T> : IPipelineSource<T>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestTransformer<TInput, TOutput> : IPipelineTransformer<TInput, TOutput>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<TOutput>> TransformAsync(
            ProcessingEnvelope<TInput> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<TOutput>.Success(default!));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestSink<T> : IPipelineSink<T>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
