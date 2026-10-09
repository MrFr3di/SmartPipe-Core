using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.DependencyInjection.Tests;

public sealed class ScopedPipelineRunLifetimeTests
{
    [Fact]
    public async Task RealScope_AfterTimedFinalizationFailure_WaitsForActiveStageBeforeDisposal()
    {
        var deadline = TimeSpan.FromSeconds(10);
        var stage = new HeldScopedStage();
        var services = new ServiceCollection();
        services.AddScoped(_ => stage);
        await using var root = services.BuildServiceProvider();
        var scope = root.CreateAsyncScope();
        var definition = PipelineDefinitionBuilder.From(new PipelineKey("held-scope"),
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) => ValueTask.FromResult<IPipelineSource<int>>(new OneItemSource())))
            .Transform(new PipelineStageKey("held"),
                PipelineComponent.ScopeOwned<IPipelineTransformer<int, int>>((context, _) =>
                    ValueTask.FromResult<IPipelineTransformer<int, int>>(context.Services!.GetRequiredService<HeldScopedStage>())),
                new StageFailureOptions
                {
                    Timeout = new TimeoutPolicy
                    {
                        AttemptTimeout = TimeSpan.FromMinutes(1),
                        LateAttemptFinalizationTimeout = TimeSpan.Zero,
                    },
                }).Build();
        var inner = await definition.StartAsync(new PipelineActivationContext(definition.Key, Guid.NewGuid(), scope.ServiceProvider));
        ScopedPipelineRunLifetime<int, int>? lifetime = null;
        try
        {
            await stage.Entered.Task.WaitAsync(deadline);
            await inner.CancelAsync().AsTask().WaitAsync(deadline);
            await stage.Cancelled.Task.WaitAsync(deadline);
            var error = await Record.ExceptionAsync(async () => await inner.Completion.WaitAsync(deadline));
            Assert.IsType<AggregateException>(error);
            // Construct the wrapper after Core publishes the budget failure: cleanup
            // enters synchronously, so a missing Core join deterministically closes the scope.
            lifetime = new ScopedPipelineRunLifetime<int, int>(inner, new CountingLease(), scope,
                DateTimeOffset.UtcNow, TimeProvider.System, new TestObservationStore());
            var disposal = lifetime.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.False(lifetime.Completion.IsCompleted);
            Assert.Equal(0, stage.DisposeCalls);
            stage.Release.TrySetResult();
            _ = await Record.ExceptionAsync(async () => await lifetime.Completion.WaitAsync(deadline));
            await disposal.WaitAsync(deadline);
            Assert.Equal(1, stage.DisposeCalls);
            Assert.False(stage.DisposedWhileActive);
        }
        finally
        {
            stage.Release.TrySetResult();
            if (lifetime is not null)
                _ = await Record.ExceptionAsync(async () => await lifetime.DisposeAsync().AsTask().WaitAsync(deadline));
            else
            {
                _ = await Record.ExceptionAsync(async () => await inner.DisposeAsync().AsTask().WaitAsync(deadline));
                await scope.DisposeAsync();
            }
        }
    }

    private sealed class HeldScopedStage : IPipelineTransformer<int, int>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCalls { get; private set; }
        public bool DisposedWhileActive { get; private set; }
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            using var registration = ct.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            Exited.TrySetResult();
            return StageResult<int>.Success(envelope.Payload);
        }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            DisposedWhileActive |= !Exited.Task.IsCompleted;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OneItemSource : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            yield return ProcessingEnvelope<int>.Create(1);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task NaturalCompletionDuringConcurrentDispose_SharesOneCleanupOutcome()
    {
        var innerCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var innerDisposeCalls = 0;
        var cleanupOrder = new List<string>();
        var lease = new CountingLease(recorder: cleanupOrder);
        var scope = new CountingScope(recorder: cleanupOrder);
        var observations = new TestObservationStore();
        var inner = CreateRun(
            innerCompletion.Task,
            async () =>
            {
                Interlocked.Increment(ref innerDisposeCalls);
                cleanupOrder.Add("inner");
                cleanupEntered.SetResult();
                await cleanupRelease.Task;
            });
        var lifetime = new ScopedPipelineRunLifetime<int, int>(
            inner,
            lease,
            new AsyncServiceScope(scope),
            DateTimeOffset.UnixEpoch,
            TimeProvider.System,
            observations);

        var disposals = Enumerable.Range(0, 64)
            .Select(_ => lifetime.DisposeAsync().AsTask())
            .ToArray();
        await cleanupEntered.Task;
        innerCompletion.SetResult();
        cleanupRelease.SetResult();

        await Task.WhenAll(disposals.Append(lifetime.Completion));

        Assert.All(disposals, task => Assert.Same(disposals[0], task));
        Assert.Equal(1, innerDisposeCalls);
        Assert.Equal(1, observations.RecordCalls);
        Assert.Equal(1, lease.DisposeCalls);
        Assert.Equal(1, scope.DisposeCalls);
        Assert.Equal(["inner", "registry", "scope"], cleanupOrder);
    }

    [Fact]
    public async Task CompletionFailure_WithCleanupFailures_AggregatesInCleanupOrder()
    {
        var terminalError = new InvalidOperationException("terminal");
        var innerDisposeError = new InvalidOperationException("inner dispose");
        var leaseError = new InvalidOperationException("registry lease");
        var scopeError = new InvalidOperationException("scope dispose");
        var inner = CreateRun(
            Task.FromException(terminalError),
            () => ValueTask.FromException(innerDisposeError));
        var lifetime = new ScopedPipelineRunLifetime<int, int>(
            inner,
            new CountingLease(leaseError),
            new AsyncServiceScope(new CountingScope(scopeError)),
            DateTimeOffset.UnixEpoch,
            TimeProvider.System,
            new TestObservationStore());

        var error = await Assert.ThrowsAsync<AggregateException>(() => lifetime.Completion);

        Assert.Equal(
            [terminalError, innerDisposeError, leaseError, scopeError],
            error.InnerExceptions);
    }

    [Fact]
    public async Task TerminalOutcomeMappingCoversSuccessFaultCancellationAndAbort()
    {
        var outcomes = new[]
        {
            (PipelineRunState.Completed, SmartPipeRunObservationOutcome.Completed),
            (PipelineRunState.Faulted, SmartPipeRunObservationOutcome.Faulted),
            (PipelineRunState.Cancelled, SmartPipeRunObservationOutcome.Cancelled),
            (PipelineRunState.Aborted, SmartPipeRunObservationOutcome.Aborted),
        };

        foreach (var (state, expected) in outcomes)
        {
            var observations = new TestObservationStore();
            var lifetime = new ScopedPipelineRunLifetime<int, int>(
                CreateRun(Task.CompletedTask, static () => ValueTask.CompletedTask, state),
                new CountingLease(),
                new AsyncServiceScope(new CountingScope()),
                DateTimeOffset.UnixEpoch,
                TimeProvider.System,
                observations);

            await lifetime.DisposeAsync();

            Assert.Equal(expected, observations.Candidate?.Outcome);
        }
    }

    [Fact]
    public async Task TerminalPublicationFailure_DoesNotSkipRegistryOrScopeCleanup()
    {
        var publicationError = new InvalidOperationException("terminal publication");
        var lease = new CountingLease();
        var scope = new CountingScope();
        var observations = new ThrowingObservationStore(publicationError);
        var lifetime = new ScopedPipelineRunLifetime<int, int>(
            CreateRun(Task.CompletedTask, static () => ValueTask.CompletedTask),
            lease,
            new AsyncServiceScope(scope),
            DateTimeOffset.UnixEpoch,
            TimeProvider.System,
            observations);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifetime.DisposeAsync().AsTask());

        Assert.Same(publicationError, error);
        Assert.Equal(1, observations.RecordCalls);
        Assert.Equal(1, lease.DisposeCalls);
        Assert.Equal(1, scope.DisposeCalls);
    }

    [Fact]
    public async Task TerminalCandidateCapturesFinalMetricsAndCapacitiesBeforeRegistryAndScopeRelease()
    {
        var lease = new CountingLease();
        var scope = new CountingScope();
        var observations = new TestObservationStore();
        var innerDisposed = false;
        var metrics = new SmartPipeMetricsSnapshot(
            itemsProcessed: 17,
            itemsFailed: 2,
            itemsFiltered: 0,
            itemsDropped: 0,
            outputItemsDropped: 0,
            observerEventsDropped: 0,
            itemsRetried: 0,
            itemsDeadLettered: 0,
            inputQueueDepth: 0,
            outputQueueDepth: 0,
            lastStageLatencyMs: 3,
            lastProcessedAtUtc: DateTimeOffset.UnixEpoch,
            lastActivityAtUtc: DateTimeOffset.UnixEpoch,
            duplicatesFiltered: 0,
            avgLatencyMs: 3,
            smoothLatencyMs: 3,
            smoothThroughput: 1,
            queueSize: 0,
            poolHitRate: 0);
        var inner = new PipelineRun<int>(
            Channel.CreateUnbounded<PipelineOutput<int>>().Reader,
            Task.CompletedTask,
            static () => PipelineRunState.Completed,
            cancel: null,
            drain: null,
            tryDrain: null,
            abort: null,
            dispose: () =>
            {
                innerDisposed = true;
                return ValueTask.CompletedTask;
            },
            metricsProvider: () =>
            {
                Assert.True(innerDisposed);
                Assert.Equal(0, lease.DisposeCalls);
                Assert.Equal(0, scope.DisposeCalls);
                return metrics;
            },
            new PipelineKey("terminal-metrics"),
            Guid.NewGuid(),
            inputCapacity: 8,
            outputCapacity: 4);
        var lifetime = new ScopedPipelineRunLifetime<int, int>(
            inner,
            lease,
            new AsyncServiceScope(scope),
            DateTimeOffset.UnixEpoch,
            TimeProvider.System,
            observations);

        await lifetime.DisposeAsync();

        Assert.Equal(metrics, observations.Candidate?.Metrics);
        Assert.Equal(8, observations.Candidate?.InputCapacity);
        Assert.Equal(4, observations.Candidate?.OutputCapacity);
        Assert.Equal(1, lease.DisposeCalls);
        Assert.Equal(1, scope.DisposeCalls);
    }

    [Fact]
    public async Task WallClockMovedBackwards_ClampsTerminalTimestampToStart()
    {
        var startedAtUtc = DateTimeOffset.UnixEpoch.AddHours(1);
        var observations = new TestObservationStore();
        var lifetime = new ScopedPipelineRunLifetime<int, int>(
            CreateRun(Task.CompletedTask, static () => ValueTask.CompletedTask),
            new CountingLease(),
            new AsyncServiceScope(new CountingScope()),
            startedAtUtc,
            new FixedTimeProvider(DateTimeOffset.UnixEpoch),
            observations);

        await lifetime.Completion;

        Assert.Equal(startedAtUtc, observations.Candidate?.CompletedAtUtc);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static PipelineRun<int> CreateRun(
        Task completion,
        Func<ValueTask> dispose,
        PipelineRunState state = PipelineRunState.Completed) =>
        new(
            Channel.CreateUnbounded<PipelineOutput<int>>().Reader,
            completion,
            () => state,
            dispose: dispose);

    private sealed class TestObservationStore : ISmartPipeMutableRunObservationStore
    {
        internal SmartPipeTerminalRunCandidate? Candidate { get; private set; }

        internal int RecordCalls { get; private set; }

        public SmartPipeTerminalRunObservation RecordTerminal(SmartPipeTerminalRunCandidate candidate)
        {
            Candidate = candidate;
            RecordCalls++;
            return new()
            {
                Identity = new SmartPipeRunIdentity
                {
                    PipelineKey = new PipelineKey("lifetime"),
                    RunId = Guid.NewGuid(),
                },
                InputType = candidate.InputType,
                OutputType = candidate.OutputType,
                Outcome = candidate.Outcome,
                StartedAtUtc = candidate.StartedAtUtc,
                CompletedAtUtc = candidate.CompletedAtUtc,
                Metrics = candidate.Metrics,
                InputCapacity = 1,
                OutputCapacity = 1,
                Sequence = 1,
            };
        }
    }

    private sealed class ThrowingObservationStore : ISmartPipeMutableRunObservationStore
    {
        private readonly Exception _error;

        internal ThrowingObservationStore(Exception error) => _error = error;

        internal int RecordCalls { get; private set; }

        public SmartPipeTerminalRunObservation RecordTerminal(SmartPipeTerminalRunCandidate candidate)
        {
            RecordCalls++;
            throw _error;
        }
    }

    private sealed class CountingLease : IDisposable
    {
        private readonly Exception? _error;
        private readonly List<string>? _recorder;

        internal CountingLease(Exception? error = null, List<string>? recorder = null)
        {
            _error = error;
            _recorder = recorder;
        }

        internal int DisposeCalls { get; private set; }

        public void Dispose()
        {
            DisposeCalls++;
            _recorder?.Add("registry");
            if (_error is not null)
            {
                throw _error;
            }
        }
    }

    private sealed class CountingScope : IServiceScope, IAsyncDisposable
    {
        private readonly Exception? _error;
        private readonly List<string>? _recorder;

        internal CountingScope(Exception? error = null, List<string>? recorder = null)
        {
            _error = error;
            _recorder = recorder;
        }

        internal int DisposeCalls { get; private set; }

        public IServiceProvider ServiceProvider => throw new NotSupportedException();

        public void Dispose() => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            _recorder?.Add("scope");
            return _error is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(_error);
        }
    }
}
