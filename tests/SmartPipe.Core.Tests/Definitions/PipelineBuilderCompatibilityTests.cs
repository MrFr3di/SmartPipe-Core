using System.Collections.Concurrent;
using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Definitions;

/// <summary>
/// Compatibility contract tests for the 2.1.2 fluent builder surface.
///
/// These tests intentionally exercise the public builder routes rather than the
/// implementation-specific compiler or activator.  The TCS gates make startup and
/// concurrent-start assertions deterministic without a timing delay.
/// </summary>
public sealed class PipelineBuilderCompatibilityTests
{
    [Fact]
    public async Task LegacyRun_ReturnsBeforeBlockedInitializeCompletes()
    {
        var initializeGate = NewGate();
        var source = new CompatibilitySource<int>([1], initializeGate);
        var builder = PipelineBuilder
            .From(source)
            .Transform(new CompatibilityTransformer<int, int>(value => value));

        var run = builder.Run();

        await source.InitializeEntered.Task;
        run.Completion.IsCompleted.Should().BeFalse();

        initializeGate.SetResult(null);
        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Should().ContainSingle();
        outputs[0].Result.Value.Should().Be(1);
    }

    [Fact]
    public async Task LegacyRun_ActivationFailure_IsReportedThroughCompletion()
    {
        var expected = new InvalidOperationException("legacy activation failed");
        var builder = PipelineBuilder
            .FromFactory<int>(_ => throw expected)
            .TransformFactory<string>(_ =>
                new CompatibilityTransformer<int, string>(value => value.ToString()));

        PipelineRun<string>? run = null;
        var start = () => run = builder.Run();

        start.Should().NotThrow();
        run.Should().NotBeNull();

        var observed = await Record.ExceptionAsync(() => run!.Completion);
        observed.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task LegacyInstance_SecondRun_FailsBeforeActivation()
    {
        var initializeGate = NewGate();
        var source = new CompatibilitySource<int>([1], initializeGate);
        var builder = PipelineBuilder
            .From(source)
            .Transform(new CompatibilityTransformer<int, int>(value => value));

        var firstRun = builder.Run();
        await source.InitializeEntered.Task;

        var secondStart = () => builder.Run();
        secondStart.Should().Throw<InvalidOperationException>().WithMessage("*single-use*");
        source.InitializeCount.Should().Be(1);

        initializeGate.SetResult(null);
        await firstRun.Completion;
    }

    [Fact]
    public async Task LegacyReusableInstance_SecondRunFailsBeforeActivation()
    {
        var initializeGate = NewGate();
        var source = new CompatibilitySource<int>(
            [1],
            initializeGate,
            PipelineComponentLifetime.Reusable);
        var builder = PipelineBuilder
            .From(source)
            .Transform(new CompatibilityTransformer<int, int>(value => value));

        var firstRun = builder.Run();
        await source.InitializeEntered.Task;

        var secondStart = () => builder.Run();
        secondStart.Should().Throw<InvalidOperationException>().WithMessage("*single-use*");
        source.InitializeCount.Should().Be(1);

        initializeGate.SetResult(null);
        await firstRun.Completion;
    }

    [Fact]
    public async Task LegacySingletonExternalInstance_SecondRunFailsBeforeActivation()
    {
        var initializeGate = NewGate();
        var source = new CompatibilitySource<int>(
            [1],
            initializeGate,
            PipelineComponentLifetime.SingletonExternal,
            ownsResources: false);
        var builder = PipelineBuilder
            .From(source)
            .Transform(new CompatibilityTransformer<int, int>(value => value));

        var firstRun = builder.Run();
        await source.InitializeEntered.Task;

        var secondStart = () => builder.Run();
        secondStart.Should().Throw<InvalidOperationException>().WithMessage("*single-use*");
        source.InitializeCount.Should().Be(1);

        initializeGate.SetResult(null);
        await firstRun.Completion;
    }

    [Fact]
    public async Task LegacyInstanceObserver_SecondRunFailsBeforeActivation()
    {
        var sourceCreated = 0;
        var stageCreated = 0;
        var builder = PipelineBuilder
            .FromFactory<int>(_ =>
            {
                Interlocked.Increment(ref sourceCreated);
                return new CompatibilitySource<int>([1]);
            })
            .TransformFactory<string>(_ =>
            {
                Interlocked.Increment(ref stageCreated);
                return new CompatibilityTransformer<int, string>(value => value.ToString());
            })
            .WithObserver(new CompatibilityObserver());

        var firstRun = builder.Run();
        await firstRun.Completion;

        var secondStart = () => builder.Run();
        secondStart.Should().Throw<InvalidOperationException>().WithMessage("*single-use*");
        sourceCreated.Should().Be(1);
        stageCreated.Should().Be(1);
    }

    [Fact]
    public async Task LegacyFactoryPipeline_RemainsSequentiallyReusable()
    {
        var sourceCreated = 0;
        var stageCreated = 0;
        var sinkCreated = 0;
        var builder = PipelineBuilder
            .FromFactory<int>(_ =>
            {
                Interlocked.Increment(ref sourceCreated);
                return new CompatibilitySource<int>([1]);
            })
            .TransformFactory<string>(_ =>
            {
                Interlocked.Increment(ref stageCreated);
                return new CompatibilityTransformer<int, string>(value => value.ToString());
            });

        var firstRun = builder.ToFactory(_ =>
        {
            Interlocked.Increment(ref sinkCreated);
            return new CompatibilitySink<string>();
        });
        await firstRun.Completion;

        var secondRun = builder.ToFactory(_ =>
        {
            Interlocked.Increment(ref sinkCreated);
            return new CompatibilitySink<string>();
        });
        await secondRun.Completion;

        sourceCreated.Should().Be(2);
        stageCreated.Should().Be(2);
        sinkCreated.Should().Be(2);
    }

    [Fact]
    public async Task LegacyFactoryDefinition_RemainsConcurrent()
    {
        var firstSourceCreated = NewCompletionSource<CompatibilitySource<int>>();
        var secondSourceCreated = NewCompletionSource<CompatibilitySource<int>>();
        var sourceCall = 0;
        var stageCreated = 0;
        var builder = PipelineBuilder
            .FromFactory<int>(_ =>
            {
                var source = new CompatibilitySource<int>([1], NewGate());
                switch (Interlocked.Increment(ref sourceCall))
                {
                    case 1:
                        firstSourceCreated.TrySetResult(source);
                        break;
                    case 2:
                        secondSourceCreated.TrySetResult(source);
                        break;
                }

                return source;
            })
            .TransformFactory<string>(_ =>
            {
                Interlocked.Increment(ref stageCreated);
                return new CompatibilityTransformer<int, string>(value => value.ToString());
            });

        var firstRun = builder.Run();
        var firstSource = await firstSourceCreated.Task;
        await firstSource.InitializeEntered.Task;

        var secondRun = builder.Run();
        var secondSource = await secondSourceCreated.Task;
        await secondSource.InitializeEntered.Task;

        firstSource.InitializationGate!.SetResult(null);
        secondSource.InitializationGate!.SetResult(null);
        await Task.WhenAll(firstRun.Completion, secondRun.Completion);

        sourceCall.Should().Be(2);
        stageCreated.Should().Be(2);
    }

    [Fact]
    public async Task LegacyBuilder_ExplicitPipelineId_IsPreservedVerbatim()
    {
        var source = new CompatibilitySource<int>([1]);
        var run = PipelineBuilder
            .From(source)
            .WithPipelineId("legacy-explicit-id")
            .Transform(new CompatibilityTransformer<int, int>(value => value))
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        run.PipelineKey.Value.Should().Be("legacy-explicit-id");
        outputs.Single().Envelope!.PipelineId.Should().Be("legacy-explicit-id");
    }

    [Fact]
    public async Task LegacyBuilder_NoId_InstanceGetsGeneratedKeyAndPreservesEnvelopeId()
    {
        var run = PipelineBuilder
            .From(new CompatibilitySource<int>([1]))
            .Transform(new CompatibilityTransformer<int, int>(value => value))
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        run.PipelineKey.Value.Should().MatchRegex("^pipeline-[0-9a-f]{32}$");
        outputs.Single().Envelope!.PipelineId.Should().Be("source-pipeline");
    }

    [Fact]
    public async Task LegacyBuilder_NoId_FactoryGetsFreshPipelineIdPerRun()
    {
        var builder = PipelineBuilder
            .FromFactory<int>(_ => new CompatibilitySource<int>([1]))
            .TransformFactory<int>(_ => new CompatibilityTransformer<int, int>(value => value));

        var firstRun = builder.Run();
        var firstOutputs = await ReadOutputsAsync(firstRun.Outputs);
        await firstRun.Completion;

        var secondRun = builder.Run();
        var secondOutputs = await ReadOutputsAsync(secondRun.Outputs);
        await secondRun.Completion;

        firstRun.PipelineKey.Value.Should().MatchRegex("^pipeline-[0-9a-f]{32}$");
        secondRun.PipelineKey.Value.Should().MatchRegex("^pipeline-[0-9a-f]{32}$");
        secondRun.PipelineKey.Should().NotBe(firstRun.PipelineKey);
        firstOutputs.Single().Envelope!.PipelineId.Should().Be("source-pipeline");
        secondOutputs.Single().Envelope!.PipelineId.Should().Be("source-pipeline");
    }

    [Fact]
    public async Task LegacyBuilder_GeneratedStageIds_AreStableAndSequential()
    {
        var run = PipelineBuilder
            .From(new CompatibilitySource<int>([1]))
            .Transform(new CompatibilityTransformer<int, int>(value => value))
            .Transform(new CompatibilityTransformer<int, int>(value => value))
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs);
        await run.Completion;

        outputs.Single().Envelope!.Lineage
            .Select(entry => entry.StageId)
            .Should()
            .Equal("stage-1", "stage-2");
    }

    [Fact]
    public async Task LegacyOwnership_SingletonExternal_DefaultDoesNotDisposeComponents()
    {
        var source = new CompatibilitySource<int>(
            [1],
            lifetime: PipelineComponentLifetime.SingletonExternal,
            ownsResources: false);
        var transformer = new CompatibilityTransformer<int, int>(
            value => value,
            PipelineComponentLifetime.SingletonExternal,
            ownsResources: false);
        var sink = new CompatibilitySink<int>(
            PipelineComponentLifetime.SingletonExternal,
            ownsResources: false);

        var run = PipelineBuilder.From(source).Transform(transformer).To(sink);
        await run.Completion;
        await run.DisposeAsync();

        source.DisposeCount.Should().Be(0);
        transformer.DisposeCount.Should().Be(0);
        sink.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task LegacyOwnership_ExplicitDisposeExternalTrue_DisposesBorrowedInstances()
    {
        var ownership = new ComponentOwnershipOptions { DisposeExternalComponents = true };
        var source = new CompatibilitySource<int>(
            [1],
            lifetime: PipelineComponentLifetime.SingletonExternal,
            ownsResources: false);
        var transformer = new CompatibilityTransformer<int, int>(
            value => value,
            PipelineComponentLifetime.SingletonExternal,
            ownsResources: false);
        var sink = new CompatibilitySink<int>(
            PipelineComponentLifetime.SingletonExternal,
            ownsResources: false);

        var adapter = LegacyPipelineDefinitionAdapter<int, int>.FromInstance(source, ownership);
        var transformed = adapter.TransformInstance(transformer, null, null);
        var run = transformed.Start(sink, CancellationToken.None);

        await run.Completion;
        await run.DisposeAsync();

        source.DisposeCount.Should().Be(1);
        transformer.DisposeCount.Should().Be(1);
        sink.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task Legacy_2_1_2_SourceSnippets_StillCompileAndRun()
    {
        var instanceRun = PipelineBuilder
            .From(new CompatibilitySource<int>([1]))
            .Transform(new CompatibilityTransformer<int, string>(value => value.ToString()))
            .To(new CompatibilitySink<string>());

        var reusableBuilder = PipelineBuilder
            .FromFactory<int>(_ => new CompatibilitySource<int>([1]))
            .TransformFactory<string>(_ =>
                new CompatibilityTransformer<int, string>(value => value.ToString()))
            .WithPipelineId("snippet-pipeline");
        var factoryRun = reusableBuilder.ToFactory(_ => new CompatibilitySink<string>());

        await Task.WhenAll(instanceRun.Completion, factoryRun.Completion);
    }

    private static async Task<List<PipelineOutput<T>>> ReadOutputsAsync<T>(
        ChannelReader<PipelineOutput<T>> reader)
    {
        var outputs = new List<PipelineOutput<T>>();
        await foreach (var output in reader.ReadAllAsync().ConfigureAwait(false))
            outputs.Add(output);
        return outputs;
    }

    private static TaskCompletionSource<object?> NewGate() => NewCompletionSource<object?>();

    private static TaskCompletionSource<T> NewCompletionSource<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class CompatibilitySource<T> : IPipelineSource<T>, IPipelineComponentDescriptor
    {
        private readonly ProcessingEnvelope<T>[] _items;
        private readonly TaskCompletionSource<object?>? _initializeGate;
        private int _initializeCount;
        private int _disposeCount;

        public CompatibilitySource(
            IEnumerable<T> payloads,
            TaskCompletionSource<object?>? initializeGate = null,
            PipelineComponentLifetime lifetime = PipelineComponentLifetime.SingleUse,
            bool ownsResources = true)
        {
            _initializeGate = initializeGate;
            Lifetime = lifetime;
            OwnsResources = ownsResources;
            _items = payloads
                .Select((payload, index) => ProcessingEnvelope<T>.Create(
                    payload,
                    "source-pipeline",
                    $"source-run-{index + 1}",
                    (ulong)(index + 1)))
                .ToArray();
        }

        public TaskCompletionSource<object?> InitializeEntered { get; } = NewGate();

        public TaskCompletionSource<object?>? InitializationGate => _initializeGate;

        public int InitializeCount => Volatile.Read(ref _initializeCount);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public PipelineComponentLifetime Lifetime { get; }

        public bool OwnsResources { get; }

        public async ValueTask InitializeAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _initializeCount);
            InitializeEntered.TrySetResult(null);
            if (_initializeGate is not null)
                await _initializeGate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in _items)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CompatibilityTransformer<TInput, TOutput>
        : IPipelineTransformer<TInput, TOutput>,
            IPipelineComponentDescriptor
    {
        private readonly Func<TInput, TOutput> _transform;
        private int _disposeCount;

        public CompatibilityTransformer(
            Func<TInput, TOutput> transform,
            PipelineComponentLifetime lifetime = PipelineComponentLifetime.SingleUse,
            bool ownsResources = true)
        {
            _transform = transform;
            Lifetime = lifetime;
            OwnsResources = ownsResources;
        }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public PipelineComponentLifetime Lifetime { get; }

        public bool OwnsResources { get; }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<TOutput>> TransformAsync(
            ProcessingEnvelope<TInput> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<TOutput>.Success(_transform(envelope.Payload)));

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CompatibilitySink<T> : IPipelineSink<T>, IPipelineComponentDescriptor
    {
        private int _disposeCount;

        public CompatibilitySink(
            PipelineComponentLifetime lifetime = PipelineComponentLifetime.SingleUse,
            bool ownsResources = true)
        {
            Lifetime = lifetime;
            OwnsResources = ownsResources;
        }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public PipelineComponentLifetime Lifetime { get; }

        public bool OwnsResources { get; }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CompatibilityObserver : IPipelineObserver
    {
        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }
}
