using System.Runtime.CompilerServices;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Definitions;

public sealed class PipelineStartReadinessTests
{
    [Fact]
    public async Task StartAsync_WaitsForExecutorRunningSignal()
    {
        var source = new ReadinessGateSource();
        var observer = new ReadinessGateObserver { WaitForStartedEvent = true };
        var definition = CreateDefinition(source, observer);

        var start = definition.StartAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None);

        await source.InitializeEntered.Task;
        start.IsCompleted.Should().BeFalse();

        source.ReleaseInitialization.TrySetResult(null);
        await observer.StartedEntered.Task;
        start.IsCompleted.Should().BeFalse();

        observer.ReleaseStartedEvent.TrySetResult(null);
        var run = await start;

        run.State.Should().NotBe(PipelineRunState.NotStarted);
        await run.Completion;
    }

    [Fact]
    public async Task StartedEvent_IsAfterComponentInitialization()
    {
        var source = new ReadinessGateSource();
        var observer = new ReadinessGateObserver { WaitForStartedEvent = true };
        var definition = CreateDefinition(source, observer);
        var operation = definition.StartDeferred(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None);

        await source.InitializeEntered.Task;
        observer.StartedEntered.Task.IsCompleted.Should().BeFalse();
        source.ReleaseInitialization.TrySetResult(null);
        await observer.StartedEntered.Task;

        operation.Run.State.Should().Be(PipelineRunState.Running);
        operation.Ready.IsCompleted.Should().BeFalse();

        observer.ReleaseStartedEvent.TrySetResult(null);
        await operation.Ready;
        await operation.Completion;
    }

    [Fact]
    public async Task StartedEventFailure_PreventsReadySuccess()
    {
        var expected = new InvalidOperationException("started event failed");
        var source = new ReadinessGateSource { CompleteInitializationImmediately = true };
        var observer = new ReadinessGateObserver { StartedEventException = expected };
        var definition = CreateDefinition(
            source,
            observer,
            ObserverReliability.Critical,
            ObserverFailurePolicy.FaultPipeline);

        var act = () => definition.StartAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None);
        var error = await act.Should().ThrowAsync<InvalidOperationException>();

        error.Which.Should().BeSameAs(expected);
        source.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task FastCompletion_ReturnsRunningOrTerminalNeverNotStarted()
    {
        var source = new ReadinessGateSource { CompleteInitializationImmediately = true };
        var definition = CreateDefinition(source);

        var run = await definition.StartAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None);

        run.State.Should().NotBe(PipelineRunState.NotStarted);
        await run.Completion;
    }

    [Fact]
    public async Task StartAsync_WithoutContextCreatesValidIdentity()
    {
        var source = new ReadinessGateSource { CompleteInitializationImmediately = true };
        var definition = CreateDefinition(source);

        var run = await definition.StartAsync(CancellationToken.None);

        run.PipelineKey.Should().Be(definition.Key);
        run.RunId.Should().NotBe(Guid.Empty);
        await run.Completion;
    }

    [Fact]
    public async Task StartAsync_WithoutContextPreservesExplicitRuntimeClock()
    {
        var expected = DateTimeOffset.Parse("2032-04-05T06:07:08+00:00");
        var observer = new ReadinessGateObserver();
        var definition = CreateDefinition(
            new ReadinessGateSource { CompleteInitializationImmediately = true },
            observer,
            runtimeOptions: new PipelineRuntimeOptions
            {
                Clock = new ReadinessFixedClock(expected),
            });

        var run = await definition.StartAsync(CancellationToken.None);
        await run.Completion;

        observer.StartedTimestamp.Should().Be(expected);
    }

    [Fact]
    public async Task StartAsync_ExplicitTimeProviderOverridesRuntimeClock()
    {
        var runtimeTime = DateTimeOffset.Parse("2032-04-05T06:07:08+00:00");
        var expected = DateTimeOffset.Parse("2042-03-04T05:06:07+00:00");
        var observer = new ReadinessGateObserver();
        var definition = CreateDefinition(
            new ReadinessGateSource { CompleteInitializationImmediately = true },
            observer,
            runtimeOptions: new PipelineRuntimeOptions
            {
                Clock = new ReadinessFixedClock(runtimeTime),
            });
        var context = new PipelineActivationContext(
            definition.Key,
            Guid.NewGuid(),
            timeProvider: new ReadinessFixedTimeProvider(expected));

        var run = await definition.StartAsync(context, CancellationToken.None);
        await run.Completion;

        observer.StartedTimestamp.Should().Be(expected);
    }

    [Fact]
    public async Task CancelBeforeReady_CancelsActivation()
    {
        var source = new ReadinessGateSource();
        var definition = CreateDefinition(source);
        var operation = definition.StartDeferred(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None);

        await source.InitializeEntered.Task;
        await operation.Run.CancelAsync();
        source.ReleaseInitialization.TrySetResult(null);

        var completionError = await Record.ExceptionAsync(() => operation.Completion);

        completionError.Should().BeAssignableTo<OperationCanceledException>();
        source.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task AbortBeforeReady_EndsAborted()
    {
        var source = new ReadinessGateSource();
        var definition = CreateDefinition(source);
        var operation = definition.StartDeferred(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None);

        await source.InitializeEntered.Task;
        await operation.Run.AbortAsync();
        source.ReleaseInitialization.TrySetResult(null);

        _ = await Record.ExceptionAsync(() => operation.Completion);

        operation.Run.State.Should().Be(PipelineRunState.Aborted);
        source.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DisposeBeforeReady_WaitsRollbackOnce()
    {
        var source = new ReadinessGateSource();
        var definition = CreateDefinition(source);
        var operation = definition.StartDeferred(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None);

        await source.InitializeEntered.Task;
        var dispose = operation.Run.DisposeAsync().AsTask();

        dispose.IsCompleted.Should().BeFalse();
        source.ReleaseInitialization.TrySetResult(null);
        _ = await Record.ExceptionAsync(() => dispose);
        _ = await Record.ExceptionAsync(() => operation.Completion);

        source.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DrainBeforeReady_DoesNotDeadlock()
    {
        var source = new ReadinessGateSource();
        var definition = CreateDefinition(source);
        var operation = definition.StartDeferred(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None);

        await source.InitializeEntered.Task;
        var drain = operation.Run.DrainAsync(TimeSpan.FromSeconds(5)).AsTask();

        drain.IsCompleted.Should().BeFalse();
        source.ReleaseInitialization.TrySetResult(null);
        _ = await Record.ExceptionAsync(() => drain);
        _ = await Record.ExceptionAsync(() => operation.Completion);
    }

    [Fact]
    public async Task DeferredRun_OutputsAreAvailableBeforeReady()
    {
        var source = new ReadinessGateSource();
        var definition = CreateDefinition(source);
        var operation = definition.StartDeferred(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None);

        operation.Run.Outputs.Should().NotBeNull();

        await source.InitializeEntered.Task;
        await operation.Run.CancelAsync();
        source.ReleaseInitialization.TrySetResult(null);
        _ = await Record.ExceptionAsync(() => operation.Completion);
    }

    [Fact]
    public async Task ConcurrentDisposeBeforeReady_InvokesCleanupOnce()
    {
        var source = new ReadinessGateSource();
        var definition = CreateDefinition(source);
        var operation = definition.StartDeferred(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None);

        await source.InitializeEntered.Task;
        var disposals = Enumerable.Range(0, 64)
            .Select(_ => operation.Run.DisposeAsync().AsTask())
            .ToArray();

        source.ReleaseInitialization.TrySetResult(null);
        _ = await Record.ExceptionAsync(() => Task.WhenAll(disposals));
        _ = await Record.ExceptionAsync(() => operation.Completion);

        source.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task CanonicalStartupFailure_LeavesNoUnobservedCompletion()
    {
        var expected = new InvalidOperationException("initialization failed");
        var source = new ReadinessGateSource
        {
            InitializeException = expected,
            CompleteInitializationImmediately = true,
        };
        var definition = CreateDefinition(source);

        var act = () => definition.StartAsync(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None);
        var error = await act.Should().ThrowAsync<InvalidOperationException>();

        error.Which.Should().BeSameAs(expected);
        source.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task LegacyStartupFailure_IsObservedThroughRunCompletion()
    {
        var expected = new InvalidOperationException("initialization failed");
        var source = new ReadinessGateSource
        {
            InitializeException = expected,
            CompleteInitializationImmediately = true,
        };
        var definition = CreateDefinition(source);
        var operation = definition.StartDeferred(
            ActivationTestSupport.CreateContext(definition.Key),
            CancellationToken.None);

        var error = await Record.ExceptionAsync(() => operation.Completion);

        error.Should().BeSameAs(expected);
        source.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task AllRuntimeTimingUsesOneResolvedClock()
    {
        var expected = DateTimeOffset.Parse("2044-05-06T07:08:09+00:00");
        var timeProvider = new ReadinessCountingTimeProvider(expected);
        var observer = new ReadinessRecordingObserver();
        var definition = CreateTimingDefinition(new ReadinessThrowingClock(), observer);
        var context = new PipelineActivationContext(
            definition.Key,
            Guid.NewGuid(),
            timeProvider: timeProvider);

        var run = await definition.StartAsync(context, CancellationToken.None);
        var output = await run.Outputs.ReadAsync();
        await run.Completion;

        timeProvider.UtcNowCalls.Should().BeGreaterThan(0);
        timeProvider.TimestampCalls.Should().BeGreaterThan(0);
        output.Envelope.Should().NotBeNull();
        output.Envelope!.CreatedAtUtc.Should().Be(expected);
        output.Envelope.Lineage.Should().ContainSingle();
        output.Envelope.Lineage[0].StartedAtUtc.Should().Be(expected);
        output.Envelope.Lineage[0].CompletedAtUtc.Should().Be(expected);
        observer.Events.Should().NotBeEmpty();
        observer.Events.Should().OnlyContain(pipelineEvent => pipelineEvent.TimestampUtc == expected);
    }

    [Fact]
    public async Task FakeAndRealTime_AreNotMixedWithinRun()
    {
        var fakeNow = DateTimeOffset.Parse("2055-06-07T08:09:10+00:00");
        var observer = new ReadinessRecordingObserver();
        var definition = CreateTimingDefinition(new ReadinessThrowingClock(), observer);
        var context = new PipelineActivationContext(
            definition.Key,
            Guid.NewGuid(),
            timeProvider: new ReadinessCountingTimeProvider(fakeNow));

        var run = await definition.StartAsync(context, CancellationToken.None);
        var output = await run.Outputs.ReadAsync();
        await run.Completion;

        output.Envelope.Should().NotBeNull();
        var envelope = output.Envelope!;
        var observedTimes = observer.Events.Select(pipelineEvent => pipelineEvent.TimestampUtc)
            .Append(envelope.CreatedAtUtc)
            .Concat(envelope.Lineage.SelectMany(entry =>
                new[] { entry.StartedAtUtc, entry.CompletedAtUtc!.Value }));

        observedTimes.Should().NotBeEmpty().And.OnlyContain(timestamp => timestamp == fakeNow);
    }

    private static PipelineDefinition<int, int> CreateTimingDefinition(
        IPipelineClock legacyClock,
        IPipelineObserver observer) =>
        PipelineDefinitionBuilder
            .From(
                new PipelineKey("timing-probe"),
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>(
                    (_, _) => ValueTask.FromResult<IPipelineSource<int>>(
                        new ReadinessTimingSource())))
            .WithRuntimeOptions(new PipelineRuntimeOptions { Clock = legacyClock })
            .WithLineageMode(LineageMode.Full)
            .WithObserver(observer)
            .Transform(
                new PipelineStageKey("stage-1"),
                PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>(
                    (_, _) => ValueTask.FromResult<IPipelineTransformer<int, int>>(
                        new ReadinessTimingTransformer())))
            .Build();

    private static PipelineDefinition<int, int> CreateDefinition(
        ReadinessGateSource source,
        ReadinessGateObserver? observer = null,
        ObserverReliability reliability = ObserverReliability.BestEffort,
        ObserverFailurePolicy failurePolicy = ObserverFailurePolicy.Log,
        PipelineRuntimeOptions? runtimeOptions = null)
    {
        var builder = PipelineDefinitionBuilder.From(
            new PipelineKey("readiness"),
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>(
                (_, _) => ValueTask.FromResult<IPipelineSource<int>>(source)));

        if (observer is not null)
            builder = builder.WithObserver(observer, reliability, failurePolicy);
        if (runtimeOptions is not null)
            builder = builder.WithRuntimeOptions(runtimeOptions);

        return builder.Build();
    }
}

internal sealed class ReadinessGateSource : IPipelineSource<int>
{
    public TaskCompletionSource<object?> InitializeEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<object?> ReleaseInitialization { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Exception? InitializeException { get; init; }

    public bool CompleteInitializationImmediately { get; init; }

    public int DisposeCalls => Volatile.Read(ref _disposeCalls);

    private int _disposeCalls;

    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        InitializeEntered.TrySetResult(null);
        if (!CompleteInitializationImmediately)
            await ReleaseInitialization.Task.ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        if (InitializeException is not null)
            throw InitializeException;
    }

    public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCalls);
        return ValueTask.CompletedTask;
    }
}

internal sealed class ReadinessGateObserver : IPipelineObserver
{
    public TaskCompletionSource<object?> StartedEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<object?> ReleaseStartedEvent { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool WaitForStartedEvent { get; init; }

    public Exception? StartedEventException { get; init; }

    public DateTimeOffset? StartedTimestamp { get; private set; }

    public async ValueTask OnEventAsync(
        PipelineEvent pipelineEvent,
        CancellationToken ct = default)
    {
        if (pipelineEvent is not PipelineStartedEvent)
            return;

        StartedTimestamp = pipelineEvent.TimestampUtc;
        StartedEntered.TrySetResult(null);
        if (WaitForStartedEvent)
            await ReleaseStartedEvent.Task.ConfigureAwait(false);

        if (StartedEventException is not null)
            throw StartedEventException;
    }
}

internal sealed class ReadinessFixedClock(DateTimeOffset utcNow) : IPipelineClock
{
    public DateTimeOffset GetUtcNow() => utcNow;

    public long GetTimestamp() => 0;

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        TimeSpan.FromTicks(endingTimestamp - startingTimestamp);
}

internal sealed class ReadinessFixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;

    public override long GetTimestamp() => 0;
}

internal sealed class ReadinessCountingTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private long _timestamp;
    private int _utcNowCalls;
    private int _timestampCalls;

    public int UtcNowCalls => Volatile.Read(ref _utcNowCalls);

    public int TimestampCalls => Volatile.Read(ref _timestampCalls);

    public override DateTimeOffset GetUtcNow()
    {
        Interlocked.Increment(ref _utcNowCalls);
        return utcNow;
    }

    public override long GetTimestamp()
    {
        Interlocked.Increment(ref _timestampCalls);
        return Interlocked.Increment(ref _timestamp);
    }
}

internal sealed class ReadinessThrowingClock : IPipelineClock
{
    public DateTimeOffset GetUtcNow() =>
        throw new InvalidOperationException("The unresolved legacy clock was used.");

    public long GetTimestamp() =>
        throw new InvalidOperationException("The unresolved legacy clock was used.");

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        throw new InvalidOperationException("The unresolved legacy clock was used.");
}

internal sealed class ReadinessTimingSource : IPipelineSource<int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield return new ProcessingEnvelope<int>
        {
            PipelineId = string.Empty,
            RunId = string.Empty,
            TraceId = 0,
            Payload = 42,
            Metadata = MetadataBag.Empty,
            Lineage = [],
            Attempt = 0,
            CreatedAtUtc = default,
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ReadinessTimingTransformer : IPipelineTransformer<int, int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask<StageResult<int>> TransformAsync(
        ProcessingEnvelope<int> envelope,
        CancellationToken ct = default) =>
        ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ReadinessRecordingObserver : IPipelineObserver
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<PipelineEvent> _events = [];

    public IReadOnlyCollection<PipelineEvent> Events => _events.ToArray();

    public ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default)
    {
        _events.Enqueue(pipelineEvent);
        return ValueTask.CompletedTask;
    }
}
