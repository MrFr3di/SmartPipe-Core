#nullable enable

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class StageExecutorTests
{
    private static readonly TimeSpan MinimalRetryDelay = TimeSpan.FromTicks(1);

    [Fact]
    public async Task StageExecutor_Retry_RetriesConfiguredAttempts()
    {
        var transformer = new FailThenSucceedTransformer<int>(failuresBeforeSuccess: 2);
        var observer = new RecordingPipelineObserver();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([42]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(2, MinimalRetryDelay),
                })
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeTrue();
        outputs[0].Result.Value.Should().Be(42);
        transformer.Attempts.Should().Equal(0, 1, 2);
        observer.Events.OfType<RetryScheduledEvent>().Should().HaveCount(2);
        observer.Events.OfType<RetryExhaustedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task StageExecutor_Retry_StopsAfterMaxAttempts()
    {
        var transformer = new AlwaysFailingTransformer<int>(ErrorType.Transient);
        var observer = new RecordingPipelineObserver();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([7]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(2, MinimalRetryDelay),
                    OnRetryExhausted = FailureAction.EmitFailureResult,
                })
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeFalse();
        outputs[0].Result.Error!.Value.Type.Should().Be(ErrorType.Transient);
        transformer.Attempts.Should().Equal(0, 1, 2);
        observer.Events.OfType<RetryScheduledEvent>().Should().HaveCount(2);
        observer.Events.OfType<RetryExhaustedEvent>().Should().ContainSingle()
            .Which.Attempt.Should().Be(2);
    }

    [Fact]
    public async Task StageExecutor_Timeout_ProducesTimeoutFailure()
    {
        var transformer = new BlockingTimeoutTransformer<int>();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([9]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Timeout = new TimeoutPolicy
                    {
                        AttemptTimeout = TimeSpan.FromMilliseconds(25),
                    },
                    OnPermanentFailure = FailureAction.EmitFailureResult,
                })
            .Run();

        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeFalse();
        outputs[0].Result.Error!.Value.Category.Should().Be("Timeout");
        transformer.CancellationObserved.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task StageExecutor_CircuitBreaker_OpensAfterPolicy()
    {
        var observer = new RecordingPipelineObserver();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([1, 2]))
            .Transform(
                new AlwaysFailingTransformer<int>(ErrorType.Transient),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 1,
                        BreakDuration = TimeSpan.FromMinutes(1),
                    },
                    OnPermanentFailure = FailureAction.EmitFailureResult,
                })
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().HaveCount(2);
        outputs.Should().OnlyContain(output => !output.Result.IsSuccess);
        observer.Events.OfType<CircuitBreakerOpenedEvent>().Should().ContainSingle();
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().ContainSingle()
            .Which.TraceId.Should().Be(2);
    }

    [Fact]
    public async Task CircuitBreakerClosed_EmitsClosedEvent()
    {
        var observer = new RecordingPipelineObserver();
        var clock = new AdvancingPipelineClock(
            new DateTimeOffset(2026, 6, 16, 10, 0, 0, TimeSpan.Zero));

        var run = PipelineBuilder
            .From(new ClockAdvancingSource<int>([1, 2, 3], clock, advanceAfterFirst: TimeSpan.FromSeconds(2)))
            .Transform(
                new FailFirstItemTransformer(),
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 1,
                        BreakDuration = TimeSpan.FromSeconds(1),
                    },
                    OnPermanentFailure = FailureAction.Skip,
                })
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                Clock = clock,
            })
            .WithObserver(observer)
            .Run();

        _ = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        observer.Events.OfType<CircuitBreakerOpenedEvent>().Should().ContainSingle();
        observer.Events.OfType<CircuitBreakerClosedEvent>().Should().ContainSingle();
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task CircuitBreakerRejected_NotRetriedIntoOpenBreaker()
    {
        var transformer = new AlwaysFailingTransformer<int>(ErrorType.Transient);
        var observer = new RecordingPipelineObserver();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([1, 2]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    CircuitBreaker = new CircuitBreakerPolicy
                    {
                        FailureThreshold = 1,
                        BreakDuration = TimeSpan.FromMinutes(1),
                    },
                    Retry = new RetryPolicy(5, MinimalRetryDelay),
                    OnRetryExhausted = FailureAction.EmitFailureResult,
                })
            .WithObserver(observer)
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().HaveCount(2);
        transformer.Attempts.Should().Equal(0);
        observer.Events.OfType<CircuitBreakerRejectedEvent>().Should().ContainSingle()
            .Which.TraceId.Should().Be(2);
        observer.Events.OfType<RetryScheduledEvent>().Should().BeEmpty();
        observer.Events.OfType<RetryExhaustedEvent>().Should().HaveCount(2);
    }

    [Fact]
    public async Task StageExecutor_DeadLetter_WritesTerminalFailure()
    {
        await using var stream = new MemoryStream();
        var serializer = new CapturingDeadLetterSerializer<int>();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([123]))
            .Transform(
                new AlwaysFailingTransformer<int>(ErrorType.Permanent),
                new StageFailureOptions
                {
                    OnPermanentFailure = FailureAction.DeadLetter,
                },
                new StageDeadLetterOptions<int>(stream, serializer))
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeFalse();
        serializer.Written.Should().ContainSingle();
        var deadLetter = serializer.Written[0];
        deadLetter.OriginalPayload.Should().Be(123);
        deadLetter.Error.Type.Should().Be(ErrorType.Permanent);
        deadLetter.StageId.Should().Be("stage-1");
    }

    [Fact]
    public async Task TransformerException_RetriesWhenRetryPolicyAllows()
    {
        var transformer = new ThrowThenSucceedTransformer();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([5]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(1, MinimalRetryDelay, retryOn: _ => true),
                })
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeTrue();
        outputs[0].Result.Value.Should().Be(5);
        transformer.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task TransformerException_WritesDeadLetterWhenConfigured()
    {
        await using var stream = new MemoryStream();
        var serializer = new CapturingDeadLetterSerializer<int>();

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([5]))
            .Transform(
                new ThrowingPolicyTransformer(),
                new StageFailureOptions { OnPermanentFailure = FailureAction.DeadLetter },
                new StageDeadLetterOptions<int>(stream, serializer))
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeFalse();
        serializer.Written.Should().ContainSingle()
            .Which.Error.Category.Should().Be("StageException");
    }

    [Fact]
    public async Task TransformerException_EmitsFailureResultByDefault()
    {
        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([5]))
            .Transform(new ThrowingPolicyTransformer())
            .Run();

        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsFailure.Should().BeTrue();
        outputs[0].Result.Error!.Value.Category.Should().Be("StageException");
    }

    [Fact]
    public async Task TransformerException_FaultsOnlyWhenPolicyIsFaultPipeline()
    {
        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([5]))
            .Transform(
                new ThrowingPolicyTransformer(),
                new StageFailureOptions { OnPermanentFailure = FailureAction.FaultPipeline })
            .Run();

        var completion = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await completion.Should().ThrowAsync<PipelineFailureActionException>()
            .WithMessage("*stage exception boom*");
        run.State.Should().Be(PipelineRunState.Faulted);
    }

    [Fact]
    public async Task TransformerOperationCanceledException_RemainsCancellation()
    {
        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([5]))
            .Transform(new CancelingPolicyTransformer())
            .Run();

        var completion = async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await completion.Should().ThrowAsync<OperationCanceledException>();
        run.State.Should().Be(PipelineRunState.Cancelled);
    }

    [Fact]
    public async Task StageExecutor_Drain_CompletesAcceptedRetryPolicy()
    {
        var transformer = new FailThenSucceedTransformer<int>(failuresBeforeSuccess: 1);

        var run = PipelineBuilder
            .From(new EnumerablePipelineSource<int>([55]))
            .Transform(
                transformer,
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(1, TimeSpan.FromMilliseconds(100)),
                })
            .Run();

        await transformer.FirstFailureReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var drainTask = run.DrainAsync(TimeSpan.FromSeconds(5)).AsTask();
        var outputs = await ReadOutputsAsync(run.Outputs).WaitAsync(TimeSpan.FromSeconds(5));

        await drainTask.WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Should().ContainSingle();
        outputs[0].Result.IsSuccess.Should().BeTrue();
        outputs[0].Result.Value.Should().Be(55);
        transformer.Attempts.Should().Equal(0, 1);
        run.State.Should().Be(PipelineRunState.Completed);
    }

    private static async Task<List<PipelineOutput<T>>> ReadOutputsAsync<T>(
        ChannelReader<PipelineOutput<T>> reader)
    {
        var outputs = new List<PipelineOutput<T>>();
        await foreach (var output in reader.ReadAllAsync())
            outputs.Add(output);

        return outputs;
    }

    private sealed class EnumerablePipelineSource<T> : IPipelineSource<T>
    {
        private readonly IReadOnlyList<T> _items;

        public EnumerablePipelineSource(IReadOnlyList<T> items)
        {
            _items = items;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return ProcessingEnvelope<T>.Create(
                    _items[i],
                    "stage-executor-tests",
                    "stage-executor-run",
                    (ulong)(i + 1));
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailThenSucceedTransformer<T> : IPipelineTransformer<T, T>
    {
        private readonly int _failuresBeforeSuccess;
        private readonly ConcurrentQueue<int> _attempts = [];

        public FailThenSucceedTransformer(int failuresBeforeSuccess)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public IReadOnlyCollection<int> Attempts => _attempts;

        public TaskCompletionSource FirstFailureReturned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<T>> TransformAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            _attempts.Enqueue(envelope.Attempt);
            if (envelope.Attempt < _failuresBeforeSuccess)
                FirstFailureReturned.TrySetResult();

            return ValueTask.FromResult(
                envelope.Attempt < _failuresBeforeSuccess
                    ? StageResult<T>.Failure(new SmartPipeError(
                        "transient failure",
                        ErrorType.Transient,
                        "Transient"))
                    : StageResult<T>.Success(envelope.Payload));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AlwaysFailingTransformer<T> : IPipelineTransformer<T, T>
    {
        private readonly ErrorType _errorType;
        private readonly ConcurrentQueue<int> _attempts = [];

        public AlwaysFailingTransformer(ErrorType errorType)
        {
            _errorType = errorType;
        }

        public IReadOnlyCollection<int> Attempts => _attempts;

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<T>> TransformAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            _attempts.Enqueue(envelope.Attempt);
            return ValueTask.FromResult(StageResult<T>.Failure(new SmartPipeError(
                $"{_errorType} failure",
                _errorType,
                _errorType.ToString())));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingPolicyTransformer : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromException<StageResult<int>>(
                new InvalidOperationException("stage exception boom"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowThenSucceedTransformer : IPipelineTransformer<int, int>
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                return ValueTask.FromException<StageResult<int>>(
                    new InvalidOperationException("stage exception boom"));
            }

            return ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancelingPolicyTransformer : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromException<StageResult<int>>(
                new OperationCanceledException("stage cancelled"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailFirstItemTransformer : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default)
        {
            return ValueTask.FromResult(
                envelope.Payload == 1
                    ? StageResult<int>.Failure(new SmartPipeError(
                        "first item fails",
                        ErrorType.Permanent,
                        "FirstItem"))
                    : StageResult<int>.Success(envelope.Payload));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ClockAdvancingSource<T> : IPipelineSource<T>
    {
        private readonly IReadOnlyList<T> _items;
        private readonly AdvancingPipelineClock _clock;
        private readonly TimeSpan _advanceAfterFirst;

        public ClockAdvancingSource(
            IReadOnlyList<T> items,
            AdvancingPipelineClock clock,
            TimeSpan advanceAfterFirst)
        {
            _items = items;
            _clock = clock;
            _advanceAfterFirst = advanceAfterFirst;
        }

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (i == 1)
                    _clock.Advance(_advanceAfterFirst);

                ct.ThrowIfCancellationRequested();
                yield return ProcessingEnvelope<T>.Create(
                    _items[i],
                    "stage-executor-tests",
                    "stage-executor-run",
                    (ulong)(i + 1));
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AdvancingPipelineClock : IPipelineClock
    {
        private DateTimeOffset _now;

        public AdvancingPipelineClock(DateTimeOffset now)
        {
            _now = now;
        }

        public DateTimeOffset GetUtcNow() => _now;

        public long GetTimestamp() => _now.UtcTicks;

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class BlockingTimeoutTransformer<T> : IPipelineTransformer<T, T>
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<StageResult<T>> TransformAsync(
            ProcessingEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }

            return StageResult<T>.Success(envelope.Payload);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingPipelineObserver : IPipelineObserver
    {
        private readonly ConcurrentQueue<PipelineEvent> _events = [];
        private readonly ConcurrentDictionary<Type, TaskCompletionSource> _waiters = [];

        public IReadOnlyCollection<PipelineEvent> Events => _events;

        public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
        {
            _events.Enqueue(pipelineEvent);
            if (_waiters.TryGetValue(pipelineEvent.GetType(), out var waiter))
                waiter.TrySetResult();

            return ValueTask.CompletedTask;
        }

        public Task WaitForAsync<TEvent>()
            where TEvent : PipelineEvent
        {
            if (_events.OfType<TEvent>().Any())
                return Task.CompletedTask;

            return _waiters.GetOrAdd(
                typeof(TEvent),
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task;
        }
    }

    private sealed class CapturingDeadLetterSerializer<T> : IDeadLetterSerializer<T>
    {
        private readonly List<DeadLetterEnvelope<T>> _written = [];

        public IReadOnlyList<DeadLetterEnvelope<T>> Written => _written;

        public ValueTask WriteAsync(
            DeadLetterEnvelope<T> envelope,
            Stream stream,
            CancellationToken ct = default)
        {
            _written.Add(envelope);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<DeadLetterEnvelope<T>> ReadAsync(
            Stream stream,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var envelope in _written)
            {
                ct.ThrowIfCancellationRequested();
                yield return envelope;
                await Task.Yield();
            }
        }
    }
}
