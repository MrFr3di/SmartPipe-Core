using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Definitions;

public sealed class PipelineDefinitionBuilderTests
{
    [Fact]
    public void From_Transform_Build_ProducesTypedDefinition()
    {
        var builder = PipelineDefinitionBuilder.From(
            new PipelineKey("orders"),
            RuntimeSource<int>());

        PipelineDefinition<int, string> definition = builder
            .Transform(new PipelineStageKey("format"), RuntimeTransformer<int, string>())
            .Build();

        definition.Key.Value.Should().Be("orders");
        definition.Stages.Should().ContainSingle();
        definition.Stages[0].Key.Value.Should().Be("format");
        definition.Stages[0].InputType.Should().Be(typeof(int));
        definition.Stages[0].OutputType.Should().Be(typeof(string));
    }

    [Fact]
    public void Branches_AreImmutableAndIndependent()
    {
        var baseBuilder = PipelineDefinitionBuilder.From(
            new PipelineKey("orders"),
            RuntimeSource<int>());

        var first = baseBuilder
            .WithLineageMode(LineageMode.Full)
            .Transform(new PipelineStageKey("first"), RuntimeTransformer<int, int>())
            .Build();
        var second = baseBuilder
            .WithLineageMode(LineageMode.Minimal)
            .Transform(new PipelineStageKey("second"), RuntimeTransformer<int, int>())
            .Build();
        var root = baseBuilder.Build();

        root.Stages.Should().BeEmpty();
        first.Stages.Select(stage => stage.Key.Value).Should().Equal("first");
        second.Stages.Select(stage => stage.Key.Value).Should().Equal("second");
        first.LineageMode.Should().Be(LineageMode.Full);
        second.LineageMode.Should().Be(LineageMode.Minimal);
    }

    [Fact]
    public void Transform_DefaultsStageNameToKey()
    {
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("orders"),
                RuntimeSource<int>())
            .Transform(new PipelineStageKey("normalize"), RuntimeTransformer<int, int>())
            .Build();

        definition.Stages.Single().Name.Should().Be("normalize");
    }

    [Fact]
    public void Transform_PreservesExplicitStageNameExactly()
    {
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("orders"),
                RuntimeSource<int>())
            .Transform(
                new PipelineStageKey("normalize"),
                RuntimeTransformer<int, int>(),
                stageName: " Normalize orders ")
            .Build();

        definition.Stages.Single().Name.Should().Be(" Normalize orders ");
    }

    [Fact]
    public void Transform_WhitespaceStageName_IsRejected()
    {
        var builder = PipelineDefinitionBuilder.From(
            new PipelineKey("orders"),
            RuntimeSource<int>());

        var act = () => builder.Transform(
            new PipelineStageKey("normalize"),
            RuntimeTransformer<int, int>(),
            stageName: " \t");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TerminalPaths_CopyMetadataAndNeverInvokeFactories()
    {
        var sourceCalls = 0;
        var transformerCalls = 0;
        var sinkCalls = 0;
        var observer = new TestObserver();
        var options = new PipelineRuntimeOptions { MaxConcurrency = 2 };
        var builder = PipelineDefinitionBuilder.From(
                new PipelineKey("orders"),
                RuntimeSource<int>(() => sourceCalls++))
            .WithRuntimeOptions(options)
            .WithLineageMode(LineageMode.Full)
            .WithObserver(observer)
            .Transform(
                new PipelineStageKey("normalize"),
                RuntimeTransformer<int, int>(() => transformerCalls++));

        var sinkless = builder.Build();
        var withSink = builder.To(RuntimeSink<int>(() => sinkCalls++));

        sourceCalls.Should().Be(0);
        transformerCalls.Should().Be(0);
        sinkCalls.Should().Be(0);
        sinkless.HasSink.Should().BeFalse();
        withSink.HasSink.Should().BeTrue();
        withSink.LineageMode.Should().Be(LineageMode.Full);
        withSink.RuntimeOptions.MaxConcurrency.Should().Be(2);
        withSink.RuntimeOptions.Should().NotBeSameAs(options);
        withSink.Stages.Single().Name.Should().Be("normalize");
    }

    [Fact]
    public void Definitions_ExposeZeroOneAndMultiStageReadOnlyMetadata()
    {
        var builder = PipelineDefinitionBuilder.From(
            new PipelineKey("orders"),
            RuntimeSource<int>());

        var zero = builder.Build();
        var one = builder
            .Transform(new PipelineStageKey("one"), RuntimeTransformer<int, int>())
            .Build();
        var many = builder
            .Transform(new PipelineStageKey("one"), RuntimeTransformer<int, int>())
            .Transform(new PipelineStageKey("two"), RuntimeTransformer<int, int>())
            .Build();

        zero.Stages.Should().BeEmpty();
        one.Stages.Should().ContainSingle();
        many.Stages.Select(stage => stage.Key.Value).Should().Equal("one", "two");
        many.Stages.Should().NotBeAssignableTo<PipelineStageMetadata[]>();
    }

    [Fact]
    public void IsReusable_TracksEveryBorrowedRegistration()
    {
        var borrowedSource = PipelineDefinitionBuilder.From(
                new PipelineKey("borrowed-source"),
                PipelineComponent.Borrowed<IPipelineSource<int>>(new TestSource<int>()))
            .Build();
        var borrowedStage = PipelineDefinitionBuilder.From(
                new PipelineKey("borrowed-stage"),
                RuntimeSource<int>())
            .Transform(
                new PipelineStageKey("stage"),
                PipelineComponent.Borrowed<IPipelineTransformer<int, int>>(new TestTransformer<int, int>()))
            .Build();
        var borrowedSink = PipelineDefinitionBuilder.From(
                new PipelineKey("borrowed-sink"),
                RuntimeSource<int>())
            .To(PipelineComponent.Borrowed<IPipelineSink<int>>(new TestSink<int>()));
        var borrowedObserver = PipelineDefinitionBuilder.From(
                new PipelineKey("borrowed-observer"),
                RuntimeSource<int>())
            .WithObserver(new TestObserver())
            .Build();
        var deadLetter = PipelineDefinitionBuilder.From(
                new PipelineKey("dead-letter"),
                RuntimeSource<int>())
            .Transform(
                new PipelineStageKey("stage"),
                RuntimeTransformer<int, int>(),
                deadLetterOptions: new StageDeadLetterOptions<int>(Stream.Null))
            .Build();

        borrowedSource.IsReusable.Should().BeFalse();
        borrowedStage.IsReusable.Should().BeFalse();
        borrowedSink.IsReusable.Should().BeFalse();
        borrowedObserver.IsReusable.Should().BeFalse();
        deadLetter.IsReusable.Should().BeFalse();
    }

    [Fact]
    public void IsReusable_AllPerRunRegistrations_IsTrue()
    {
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("orders"),
                RuntimeSource<int>())
            .Transform(new PipelineStageKey("normalize"), RuntimeTransformer<int, int>())
            .To(RuntimeSink<int>());

        definition.IsReusable.Should().BeTrue();
    }

    [Fact]
    public void Build_DuplicateStageKeys_IsRejectedWithoutInvokingFactories()
    {
        var sourceCalls = 0;
        var stageCalls = 0;
        var duplicate = new PipelineStageKey("normalize");
        var builder = PipelineDefinitionBuilder.From(
                new PipelineKey("orders"),
                RuntimeSource<int>(() => sourceCalls++))
            .Transform(duplicate, RuntimeTransformer<int, int>(() => stageCalls++))
            .Transform(duplicate, RuntimeTransformer<int, int>(() => stageCalls++));

        var act = () => builder.Build();

        var error = act.Should().Throw<InvalidOperationException>().Which;
        error.Message.Should().Contain("normalize");
        error.Message.Should().Contain("0");
        error.Message.Should().Contain("1");
        sourceCalls.Should().Be(0);
        stageCalls.Should().Be(0);
    }

    [Fact]
    public void GetExecutionPlan_DoesNotInvokeFactories()
    {
        var sourceCalls = 0;
        var stageCalls = 0;
        var sinkCalls = 0;
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("orders"),
                RuntimeSource<int>(() => sourceCalls++))
            .Transform(
                new PipelineStageKey("normalize"),
                RuntimeTransformer<int, int>(() => stageCalls++))
            .To(RuntimeSink<int>(() => sinkCalls++));

        _ = definition.GetExecutionPlan();

        sourceCalls.Should().Be(0);
        stageCalls.Should().Be(0);
        sinkCalls.Should().Be(0);
    }

    [Fact]
    public void Definition_OptionsAreSnapshotIsolated()
    {
        var options = new PipelineRuntimeOptions
        {
            MaxConcurrency = 3,
            ObserverDispatch = new ObserverDispatchOptions { Capacity = 11 },
            AdaptiveParallelism = new AdaptiveParallelismOptions { MaxConcurrency = 3 },
        };

        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("orders"),
                RuntimeSource<int>())
            .WithRuntimeOptions(options)
            .Build();

        definition.RuntimeOptions.Should().NotBeSameAs(options);
        definition.RuntimeOptions.ObserverDispatch.Should().NotBeSameAs(options.ObserverDispatch);
        definition.RuntimeOptions.AdaptiveParallelism.Should().NotBeSameAs(options.AdaptiveParallelism);
        definition.RuntimeOptions.MaxConcurrency.Should().Be(3);
    }

    private static PipelineComponent<IPipelineSource<T>> RuntimeSource<T>(Action? onCreate = null)
    {
        return PipelineComponent.RuntimeOwned<IPipelineSource<T>>((_, _) =>
        {
            onCreate?.Invoke();
            return ValueTask.FromResult<IPipelineSource<T>>(new TestSource<T>());
        });
    }

    private static PipelineComponent<IPipelineTransformer<TInput, TOutput>> RuntimeTransformer<TInput, TOutput>(Action? onCreate = null)
    {
        return PipelineComponent.RuntimeOwned<IPipelineTransformer<TInput, TOutput>>((_, _) =>
        {
            onCreate?.Invoke();
            return ValueTask.FromResult<IPipelineTransformer<TInput, TOutput>>(new TestTransformer<TInput, TOutput>());
        });
    }

    private static PipelineComponent<IPipelineSink<T>> RuntimeSink<T>(Action? onCreate = null)
    {
        return PipelineComponent.RuntimeOwned<IPipelineSink<T>>((_, _) =>
        {
            onCreate?.Invoke();
            return ValueTask.FromResult<IPipelineSink<T>>(new TestSink<T>());
        });
    }

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

        public ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestObserver : IPipelineObserver
    {
        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }
}
