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
        var expected = new InvalidOperationException("observer failure");

        var dispatcher = PipelineObserverDispatcher.Create(
            [
                new PipelineObserverRegistration(
                    new ThrowingObserver(expected),
                    ObserverReliability.BestEffort,
                    ObserverFailurePolicy.FaultPipeline),
            ],
            BufferedOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);

        var act = async () => await dispatcher.CompleteAsync(CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Should().BeSameAs(expected);

        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task ObserverDispatcher_EmitAsyncAfterBufferedFault_ThrowsOriginalObserverException()
    {
        var expected = new InvalidOperationException("observer failure");
        var dispatcher = PipelineObserverDispatcher.Create(
            [
                new PipelineObserverRegistration(
                    new ThrowingObserver(expected),
                    ObserverReliability.BestEffort,
                    ObserverFailurePolicy.FaultPipeline),
            ],
            BufferedOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);

        var exception = await WaitUntilEmitThrowsAsync<InvalidOperationException>(
            dispatcher,
            new PipelineCompletedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            TimeSpan.FromSeconds(5));

        exception.Should().BeSameAs(expected);

        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task ObserverDispatcher_EmitAsyncAfterNormalDispose_ThrowsChannelClosedException()
    {
        var dispatcher = PipelineObserverDispatcher.Create(
            [],
            BufferedOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        await dispatcher.DisposeAsync();

        var act = async () => await dispatcher.EmitAsync(
            new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            CancellationToken.None);

        await act.Should().ThrowAsync<ChannelClosedException>();
    }

    [Fact]
    public async Task ObserverDispatcher_DisposeAsyncAfterRecordedBufferedFault_DoesNotThrow()
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
        await WaitUntilEmitThrowsAsync<InvalidOperationException>(
            dispatcher,
            new PipelineCompletedEvent("pipeline", "run", DateTimeOffset.UtcNow),
            TimeSpan.FromSeconds(5));

        var act = async () => await dispatcher.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ObserverDispatcher_DisposeAsyncWithoutEvents_DoesNotThrow()
    {
        var dispatcher = PipelineObserverDispatcher.Create(
            [],
            BufferedOptions(ObserverFailureMode.UseRegistrationPolicy),
            SystemPipelineClock.Instance);

        var act = async () => await dispatcher.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task InputDroppedEvent_BestEffortEmissionFailure_RecordsObserverDropAndDoesNotFaultRun()
    {
        var observer = new ThrowingOnEventTypeObserver(typeof(InputDroppedEvent));
        var transformer = new BlockingTransformer<int>(expectedConcurrentCalls: 2);

        var run = PipelineBuilder
            .From(new EnumerableSource<int>(Enumerable.Range(0, 64).ToArray()))
            .Transform(transformer)
            .WithObserver(
                observer,
                ObserverReliability.BestEffort,
                ObserverFailurePolicy.FaultPipeline)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 2,
                InputCapacity = 1,
                InputFullMode = BoundedChannelFullMode.DropWrite,
                ObserverDispatch = new ObserverDispatchOptions
                {
                    Mode = ObserverDispatchMode.Inline,
                    FailureMode = ObserverFailureMode.UseRegistrationPolicy,
                },
            })
            .Run();

        await transformer.ExpectedConcurrentCallsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await observer.EventObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        transformer.Release();

        _ = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        run.State.Should().Be(PipelineRunState.Completed);
        run.Metrics.ItemsDropped.Should().BeGreaterThan(0);
        run.Metrics.ObserverEventsDropped.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task OutputDroppedEvent_BestEffortEmissionFailure_RecordsObserverDropAndDoesNotFaultRun()
    {
        var observer = new ThrowingOnEventTypeObserver(typeof(OutputDroppedEvent));

        var run = PipelineBuilder
            .From(new EnumerableSource<int>([1, 2, 3]))
            .Transform(new PassThroughTransformer<int>())
            .WithObserver(
                observer,
                ObserverReliability.BestEffort,
                ObserverFailurePolicy.FaultPipeline)
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputCapacity = 1,
                OutputFullMode = BoundedChannelFullMode.DropOldest,
                ObserverDispatch = new ObserverDispatchOptions
                {
                    Mode = ObserverDispatchMode.Inline,
                    FailureMode = ObserverFailureMode.UseRegistrationPolicy,
                },
            })
            .Run();

        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));

        run.State.Should().Be(PipelineRunState.Completed);
        outputs.Should().ContainSingle();
        run.Metrics.OutputItemsDropped.Should().BeGreaterThan(0);
        run.Metrics.ObserverEventsDropped.Should().BeGreaterThan(0);
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

    private static async Task<TException> WaitUntilEmitThrowsAsync<TException>(
        IPipelineObserverDispatcher dispatcher,
        PipelineEvent pipelineEvent,
        TimeSpan timeout)
        where TException : Exception
    {
        using var cts = new CancellationTokenSource(timeout);

        while (!cts.IsCancellationRequested)
        {
            try
            {
                await dispatcher.EmitAsync(pipelineEvent, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
            catch (TException ex)
            {
                return ex;
            }

            await Task.Yield();
        }

        throw new TimeoutException(
            $"Dispatcher did not throw {typeof(TException).Name} for {pipelineEvent.GetType().Name} within {timeout}.");
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

    private sealed class BlockingTransformer<T> : IPipelineTransformer<T, T>
    {
        private readonly int _expectedConcurrentCalls;
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeCalls;

        public BlockingTransformer(int expectedConcurrentCalls)
        {
            _expectedConcurrentCalls = expectedConcurrentCalls;
        }

        public TaskCompletionSource ExpectedConcurrentCallsEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<StageResult<T>> TransformAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            var active = Interlocked.Increment(ref _activeCalls);
            if (active >= _expectedConcurrentCalls)
                ExpectedConcurrentCallsEntered.TrySetResult();

            try
            {
                await _release.Task.WaitAsync(ct).ConfigureAwait(false);
                return StageResult<T>.Success(envelope.Payload);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        public void Release() => _release.TrySetResult();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingObserver : IPipelineObserver
    {
        private readonly Exception _exception;
        private int _calls;

        public ThrowingObserver()
            : this(new InvalidOperationException("observer failure"))
        {
        }

        public ThrowingObserver(Exception exception)
        {
            _exception = exception;
        }

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            throw _exception;
        }
    }

    private sealed class ThrowingOnEventTypeObserver : IPipelineObserver
    {
        private readonly Type _eventType;

        public ThrowingOnEventTypeObserver(Type eventType)
        {
            _eventType = eventType;
        }

        public TaskCompletionSource EventObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            if (pipelineEvent.GetType() == _eventType)
            {
                EventObserved.TrySetResult();
                throw new InvalidOperationException("observer failure");
            }

            return ValueTask.CompletedTask;
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
