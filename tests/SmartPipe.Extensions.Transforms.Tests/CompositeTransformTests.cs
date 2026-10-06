using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms.Tests;

public sealed class CompositeTransformTests
{
    [Fact]
    public async Task InitializeAsync_IsSingleShotForConcurrentCallers()
    {
        var entered = NewGate();
        var release = NewGate();
        var child = new StubTransform(initialize: async _ =>
        {
            entered.SetResult();
            await release.Task;
        });
        var composite = new CompositeTransform<int>(child);

        Task first = composite.InitializeAsync(TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Task second = composite.InitializeAsync(TestContext.Current.CancellationToken).AsTask();

        Assert.Same(first, second);
        Assert.Equal(1, child.InitializeCount);
        release.SetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task InitializeAsync_RollsBackInReverseOrderAndKeepsPrimaryFailureFirst()
    {
        var events = new List<string>();
        var first = new StubTransform(
            initialize: _ => Record(events, "init:first"),
            dispose: () => Fail(events, "dispose:first", "cleanup first"));
        var second = new StubTransform(
            initialize: _ => Fail(events, "init:second", "initialize second"),
            dispose: () => Fail(events, "dispose:second", "cleanup second"));
        var composite = new CompositeTransform<int>(first, second);

        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            composite.InitializeAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            ["initialize second", "cleanup second", "cleanup first"],
            error.InnerExceptions.Select(static exception => exception.Message));
        Assert.Equal(["init:first", "init:second", "dispose:second", "dispose:first"], events);
    }

    [Fact]
    public async Task DisposeAsync_IsSingleShotBestEffortAndReverseOrder()
    {
        var events = new List<string>();
        var first = new StubTransform(dispose: () => Fail(events, "first", "first failed"));
        var second = new StubTransform(dispose: () => Fail(events, "second", "second failed"));
        var composite = new CompositeTransform<int>(first, second);
        await composite.InitializeAsync(TestContext.Current.CancellationToken);

        Task firstDispose = composite.DisposeAsync().AsTask();
        Task secondDispose = composite.DisposeAsync().AsTask();
        Assert.Same(firstDispose, secondDispose);
        var error = await Assert.ThrowsAsync<AggregateException>(() => firstDispose);

        Assert.Equal(["second", "first"], events);
        Assert.Equal(["second failed", "first failed"], error.InnerExceptions.Select(static exception => exception.Message));
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_RacingInitializationWaitsAndCleansExactlyOnce()
    {
        var entered = NewGate();
        var release = NewGate();
        var child = new StubTransform(initialize: async _ =>
        {
            entered.SetResult();
            await release.Task;
        });
        var composite = new CompositeTransform<int>(child);
        Task initialize = composite.InitializeAsync(TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Task dispose = composite.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        release.SetResult();
        await Task.WhenAll(initialize, dispose);

        Assert.Equal(1, child.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            composite.TransformAsync(
                ProcessingEnvelope<int>.Create(1), TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task TransformAsync_PreservesEnvelopeAndShortCircuitsTerminalResult()
    {
        ulong observedTraceId = 0;
        var first = new StubTransform(transform: (envelope, token) =>
        {
            observedTraceId = envelope.TraceId;
            return ValueTask.FromResult(StageResult<int>.Filtered());
        });
        var second = new StubTransform();
        var composite = new CompositeTransform<int>(first, second);
        await composite.InitializeAsync(TestContext.Current.CancellationToken);
        var envelope = ProcessingEnvelope<int>.Create(5);

        StageResult<int> result = await composite.TransformAsync(envelope, TestContext.Current.CancellationToken);

        Assert.Equal(StageResultKind.Filtered, result.Kind);
        Assert.Equal(envelope.TraceId, observedTraceId);
        Assert.Equal(0, second.TransformCount);
    }

    [Fact]
    public async Task TransformAsync_ReturnsExactFailureAndDoesNotInvokeDownstreamChild()
    {
        var marker = new InvalidOperationException("failure identity");
        var error = new SmartPipeError("terminal failure", ErrorType.Permanent, "CompositeTest", marker);
        StageResult<int> expected = StageResult<int>.Failure(error);
        var failing = new StubTransform(transform: (_, _) => ValueTask.FromResult(expected));
        var downstream = new StubTransform();
        var composite = new CompositeTransform<int>(failing, downstream);
        await composite.InitializeAsync(TestContext.Current.CancellationToken);

        StageResult<int> actual = await composite.TransformAsync(
            ProcessingEnvelope<int>.Create(5), TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);
        Assert.Equal(error, actual.Error);
        Assert.Same(marker, actual.Error!.Value.InnerException);
        Assert.Equal(1, failing.TransformCount);
        Assert.Equal(1, downstream.InitializeCount);
        Assert.Equal(0, downstream.TransformCount);
    }

    [Fact]
    public async Task TransformAsync_PassesExactCallerTokenToEveryChild()
    {
        var observedTokens = new List<CancellationToken>();
        var first = new StubTransform(transform: (envelope, token) =>
        {
            observedTokens.Add(token);
            return ValueTask.FromResult(StageResult<int>.Success(envelope.Payload + 1));
        });
        var second = new StubTransform(transform: (envelope, token) =>
        {
            observedTokens.Add(token);
            return ValueTask.FromResult(StageResult<int>.Success(envelope.Payload + 1));
        });
        var composite = new CompositeTransform<int>(first, second);
        await composite.InitializeAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();

        StageResult<int> result = await composite.TransformAsync(
            ProcessingEnvelope<int>.Create(1), cancellation.Token);

        Assert.Equal(3, result.Value);
        Assert.Equal([cancellation.Token, cancellation.Token], observedTokens);
        Assert.All(observedTokens, token => Assert.Equal(cancellation.Token, token));
    }

    [Fact]
    public async Task TransformAsync_RequiresSuccessfulInitialization()
    {
        var composite = new CompositeTransform<int>(new StubTransform());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            composite.TransformAsync(
                ProcessingEnvelope<int>.Create(1), TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task ConstructorDefensivelyCopiesAndRejectsNullChildren()
    {
        var original = new StubTransform();
        IPipelineTransformer<int, int>[] children = [original];
        var composite = new CompositeTransform<int>(children);
        children[0] = new StubTransform(initialize: _ => throw new InvalidOperationException("mutated"));

        await composite.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, original.InitializeCount);
        Assert.Throws<ArgumentNullException>(() => new CompositeTransform<int>(null!));
        Assert.Throws<ArgumentException>(() => new CompositeTransform<int>(original, null!));
    }

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ValueTask Record(List<string> events, string value)
    {
        events.Add(value);
        return ValueTask.CompletedTask;
    }

    private static ValueTask Fail(List<string> events, string value, string message)
    {
        events.Add(value);
        return ValueTask.FromException(new InvalidOperationException(message));
    }

    private sealed class StubTransform : IPipelineTransformer<int, int>
    {
        private readonly Func<CancellationToken, ValueTask> _initialize;
        private readonly Func<ProcessingEnvelope<int>, CancellationToken, ValueTask<StageResult<int>>> _transform;
        private readonly Func<ValueTask> _dispose;

        internal StubTransform(
            Func<CancellationToken, ValueTask>? initialize = null,
            Func<ProcessingEnvelope<int>, CancellationToken, ValueTask<StageResult<int>>>? transform = null,
            Func<ValueTask>? dispose = null)
        {
            _initialize = initialize ?? (_ => ValueTask.CompletedTask);
            _transform = transform ?? ((envelope, _) => ValueTask.FromResult(StageResult<int>.Success(envelope.Payload)));
            _dispose = dispose ?? (() => ValueTask.CompletedTask);
        }

        internal int InitializeCount { get; private set; }
        internal int TransformCount { get; private set; }
        internal int DisposeCount { get; private set; }

        public ValueTask InitializeAsync(CancellationToken ct = default)
        {
            InitializeCount++;
            return _initialize(ct);
        }

        public ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            TransformCount++;
            return _transform(envelope, ct);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return _dispose();
        }
    }
}
