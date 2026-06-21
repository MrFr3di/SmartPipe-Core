#nullable enable

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class ObserverDispatcherTests
{
    [Fact]
    public async Task BufferedObserver_UseRegistrationPolicy_FaultPipelineObserverFaultsRun()
    {
        var observer = new ThrowingObserver();

        var run = CreateObservedRun(
            observer,
            ObserverReliability.BestEffort,
            ObserverFailurePolicy.FaultPipeline,
            ObserverFailureMode.UseRegistrationPolicy);

        var act = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("observer failure");
        run.State.Should().Be(PipelineRunState.Faulted);
    }

    [Fact]
    public async Task BufferedObserver_UseRegistrationPolicy_CriticalObserverFaultsRun()
    {
        var observer = new ThrowingObserver();

        var run = CreateObservedRun(
            observer,
            ObserverReliability.Critical,
            ObserverFailurePolicy.Ignore,
            ObserverFailureMode.UseRegistrationPolicy);

        var act = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("observer failure");
        run.State.Should().Be(PipelineRunState.Faulted);
    }

    [Fact]
    public async Task BufferedObserver_UseRegistrationPolicy_RemoveObserverDisablesObserver()
    {
        var failingObserver = new ThrowingObserver();
        var recordingObserver = new RecordingObserver();

        var run = PipelineBuilder
            .From(new EnumerableSource<int>([1, 2]))
            .Transform(new PassThroughTransformer<int>())
            .WithObserver(
                failingObserver,
                ObserverReliability.BestEffort,
                ObserverFailurePolicy.RemoveObserver)
            .WithObserver(recordingObserver)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                ObserverDispatch = BufferedOptions(ObserverFailureMode.UseRegistrationPolicy),
            })
            .Run();

        _ = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        failingObserver.Calls.Should().Be(1);
        recordingObserver.Events.OfType<PipelineCompletedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task BufferedObserver_IgnoreMode_DoesNotFaultRun()
    {
        var observer = new ThrowingObserver();

        var run = CreateObservedRun(
            observer,
            ObserverReliability.Critical,
            ObserverFailurePolicy.FaultPipeline,
            ObserverFailureMode.Ignore);

        _ = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        run.State.Should().Be(PipelineRunState.Completed);
        observer.Calls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task InlineObserver_FaultPipelineObserverFaultsRun()
    {
        var observer = new ThrowingObserver();

        var run = PipelineBuilder
            .From(new EnumerableSource<int>([1]))
            .Transform(new PassThroughTransformer<int>())
            .WithObserver(
                observer,
                ObserverReliability.BestEffort,
                ObserverFailurePolicy.FaultPipeline)
            .Run();

        var act = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("observer failure");
        run.State.Should().Be(PipelineRunState.Faulted);
    }

    [Fact]
    public async Task ObserverDispatcher_CompleteAsync_PropagatesBufferedFault()
    {
        var dispatcher = PipelineObserverDispatcher.Create(
            [
                new PipelineObserverRegistration(
                    new ThrowingObserver(),
                    ObserverReliability.BestEffort,
                    ObserverFailurePolicy.FaultPipeline),
            ],
            BufferedOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);

        var act = async () => await dispatcher.CompleteAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("observer failure");

        await dispatcher.DisposeAsync();
    }

    private static PipelineRun<int> CreateObservedRun(
        IPipelineObserver observer,
        ObserverReliability reliability,
        ObserverFailurePolicy failurePolicy,
        ObserverFailureMode failureMode)
    {
        return PipelineBuilder
            .From(new EnumerableSource<int>([1]))
            .Transform(new PassThroughTransformer<int>())
            .WithObserver(observer, reliability, failurePolicy)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                ObserverDispatch = BufferedOptions(failureMode),
            })
            .Run();
    }

    private static ObserverDispatchOptions BufferedOptions(ObserverFailureMode failureMode)
    {
        return new ObserverDispatchOptions
        {
            Mode = ObserverDispatchMode.BufferedReliable,
            Capacity = 16,
            FullMode = BoundedChannelFullMode.Wait,
            FailureMode = failureMode,
            FlushOnCompletion = true,
        };
    }

    private static async Task<List<PipelineOutput<T>>> ReadOutputsAsync<T>(
        ChannelReader<PipelineOutput<T>> reader)
    {
        var outputs = new List<PipelineOutput<T>>();
        await foreach (var output in reader.ReadAllAsync())
            outputs.Add(output);

        return outputs;
    }

    private sealed class EnumerableSource<T> : IPipelineSource<T>
    {
        private readonly IReadOnlyList<T> _payloads;

        public EnumerableSource(IReadOnlyList<T> payloads)
        {
            _payloads = payloads;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < _payloads.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return ProcessingEnvelope<T>.Create(
                    _payloads[i],
                    "observer-dispatcher-tests",
                    "observer-dispatcher-run",
                    (ulong)(i + 1));
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PassThroughTransformer<T> : IPipelineTransformer<T, T>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<T>> TransformAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            return ValueTask.FromResult(StageResult<T>.Success(envelope.Payload));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingObserver : IPipelineObserver
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("observer failure");
        }
    }

    private sealed class RecordingObserver : IPipelineObserver
    {
        private readonly ConcurrentQueue<PipelineEvent> _events = [];

        public IReadOnlyCollection<PipelineEvent> Events => _events;

        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            _events.Enqueue(pipelineEvent);
            return ValueTask.CompletedTask;
        }
    }
}
