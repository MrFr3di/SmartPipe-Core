using Polly;
using Polly.Retry;
using SmartPipe.Core;
using static SmartPipe.Extensions.Polly.Tests.PollyTestSupport;

namespace SmartPipe.Extensions.Polly.Tests;

public sealed class PollyTransformDecoratorLifecycleTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TransformBeforeInitialization_IsRejectedWithoutCallingInner()
    {
        var inner = Succeeding();
        await using var decorator = new PollyTransformDecorator<int, int>(inner, Empty<int>(), PollyInnerTransformOwnership.Owned);

        await Assert.ThrowsAsync<InvalidOperationException>(() => decorator.TransformAsync(Envelope(1), TestToken).AsTask());

        Assert.Equal(0, inner.TransformCalls);
    }

    [Fact]
    public async Task ConcurrentInitialization_InitializesInnerOnce()
    {
        var gate = new Gate();
        var inner = new RecordingTransformer<int, int>(SuccessBehavior) { OnInitialize = ct => gate.WaitAsync(ct) };
        await using var decorator = new PollyTransformDecorator<int, int>(inner, Empty<int>(), PollyInnerTransformOwnership.Borrowed);

        var first = decorator.InitializeAsync(TestToken).AsTask();
        await gate.Entered.WaitAsync(TestToken);
        var second = decorator.InitializeAsync(TestToken).AsTask();
        Assert.False(second.IsCompleted);
        gate.Release();
        await Task.WhenAll(first, second);
        await decorator.InitializeAsync(TestToken);

        Assert.Equal(1, inner.InitializeCalls);
        Assert.True((await decorator.TransformAsync(Envelope(1), TestToken)).IsSuccess);
    }

    [Fact]
    public async Task FailedInitialization_IsSharedAndBlocksTransforms()
    {
        var failure = new InvalidOperationException("init");
        var gate = new Gate();
        var inner = new RecordingTransformer<int, int>(SuccessBehavior)
        {
            OnInitialize = async ct =>
            {
                await gate.WaitAsync(ct);
                throw failure;
            },
        };
        var decorator = new PollyTransformDecorator<int, int>(inner, Empty<int>(), PollyInnerTransformOwnership.Owned);

        var first = decorator.InitializeAsync(TestToken).AsTask();
        await gate.Entered.WaitAsync(TestToken);
        var second = decorator.InitializeAsync(TestToken).AsTask();
        gate.Release();

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => first));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => second));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => decorator.InitializeAsync(TestToken).AsTask()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => decorator.TransformAsync(Envelope(1), TestToken).AsTask());
        Assert.Equal(1, inner.InitializeCalls);
        Assert.Equal(0, inner.TransformCalls);

        await decorator.DisposeAsync();
        Assert.Equal(1, inner.DisposeCalls);
    }

    [Fact]
    public async Task OwnedInner_IsDisposedOnceAcrossConcurrentDisposers()
    {
        var gate = new Gate();
        var inner = new RecordingTransformer<int, int>(SuccessBehavior) { OnDispose = () => gate.WaitAsync(TestToken) };
        var decorator = await InitializedAsync(inner, Empty<int>());

        var first = decorator.DisposeAsync().AsTask();
        await gate.Entered.WaitAsync(TestToken);
        var second = decorator.DisposeAsync().AsTask();
        Assert.False(second.IsCompleted);
        gate.Release();
        await Task.WhenAll(first, second);
        await decorator.DisposeAsync();

        Assert.Equal(1, inner.DisposeCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => decorator.TransformAsync(Envelope(1), TestToken).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => decorator.InitializeAsync(TestToken).AsTask());
    }

    [Fact]
    public async Task BorrowedInner_IsInitializedButNeverDisposed()
    {
        var inner = Succeeding();
        var decorator = await InitializedAsync(inner, Empty<int>(), ownership: PollyInnerTransformOwnership.Borrowed);

        Assert.True((await decorator.TransformAsync(Envelope(1), TestToken)).IsSuccess);
        await decorator.DisposeAsync();
        await decorator.DisposeAsync();

        Assert.Equal(1, inner.InitializeCalls);
        Assert.Equal(0, inner.DisposeCalls);
    }

    [Fact]
    public async Task DisposalFailure_IsSharedByEveryDisposer()
    {
        var failure = new InvalidOperationException("dispose");
        var gate = new Gate();
        var inner = new RecordingTransformer<int, int>(SuccessBehavior)
        {
            OnDispose = async () =>
            {
                await gate.WaitAsync(TestToken);
                throw failure;
            },
        };
        var decorator = await InitializedAsync(inner, Empty<int>());

        var first = decorator.DisposeAsync().AsTask();
        await gate.Entered.WaitAsync(TestToken);
        var second = decorator.DisposeAsync().AsTask();
        gate.Release();

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => first));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => second));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => decorator.DisposeAsync().AsTask()));
        Assert.Equal(1, inner.DisposeCalls);
    }

    [Fact]
    public async Task DisposeDuringInitialization_WaitsThenDisposesOwnedInner()
    {
        var gate = new Gate();
        var inner = new RecordingTransformer<int, int>(SuccessBehavior) { OnInitialize = ct => gate.WaitAsync(ct) };
        var decorator = new PollyTransformDecorator<int, int>(inner, Empty<int>(), PollyInnerTransformOwnership.Owned);

        var initialization = decorator.InitializeAsync(TestToken).AsTask();
        await gate.Entered.WaitAsync(TestToken);
        var disposal = decorator.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        Assert.Equal(0, inner.DisposeCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => decorator.InitializeAsync(TestToken).AsTask());

        gate.Release();
        await initialization;
        await disposal;

        Assert.Equal(["inner-initialize-start", "inner-initialize-end", "inner-dispose"], inner.Log.Events);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => decorator.TransformAsync(Envelope(1), TestToken).AsTask());
    }

    [Fact]
    public async Task Disposal_DrainsActiveExecutionBeforeOwnedCleanup()
    {
        var gate = new Gate();
        var inner = new RecordingTransformer<int, int>(async (envelope, _, ct) =>
        {
            await gate.WaitAsync(ct);
            return StageResult<int>.Success(envelope.Payload);
        });
        var decorator = await InitializedAsync(inner, Empty<int>());

        var execution = decorator.TransformAsync(Envelope(4), TestToken).AsTask();
        await gate.Entered.WaitAsync(TestToken);
        var disposal = decorator.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => decorator.TransformAsync(Envelope(5), TestToken).AsTask());
        Assert.Equal(0, inner.DisposeCalls);

        gate.Release();
        Assert.Equal(StageResult<int>.Success(4), await execution);
        await disposal;

        Assert.Equal(1, inner.TransformCalls);
        Assert.Equal("inner-dispose", inner.Log.Events[^1]);
        Assert.Equal(1, inner.DisposeCalls);
    }

    [Fact]
    public async Task Disposal_WaitsAcrossPollyRetryDelay()
    {
        var time = new SignalingTimeProvider();
        var inner = new RecordingTransformer<int, int>((envelope, attempt, _) => attempt == 1
            ? ThrowFromInner<int>(new InvalidOperationException("first"))
            : ValueTask.FromResult(StageResult<int>.Success(envelope.Payload)));
        var pipeline = new ResiliencePipelineBuilder<StageResult<int>> { TimeProvider = time }
            .AddRetry(new RetryStrategyOptions<StageResult<int>>
            {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.FromSeconds(10),
                BackoffType = DelayBackoffType.Constant,
                UseJitter = false,
            })
            .Build();
        var decorator = await InitializedAsync(inner, pipeline);

        var execution = decorator.TransformAsync(Envelope(9), TestToken).AsTask();
        await time.TimerCreated.WaitAsync(TestToken);
        var disposal = decorator.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        Assert.Equal(1, inner.TransformCalls);
        Assert.Equal(0, inner.DisposeCalls);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(StageResult<int>.Success(9), await execution);
        await disposal;

        Assert.Equal(["inner-initialize-start", "inner-initialize-end", "inner-transform-1", "inner-transform-2", "inner-dispose"], inner.Log.Events);
    }

    [Fact]
    public async Task DirectOwner_DisposesOwnedInnerAfterFailedInitialization()
    {
        var inner = new RecordingTransformer<int, int>(SuccessBehavior)
        {
            OnInitialize = _ => ValueTask.FromException(new InvalidOperationException("init")),
        };
        var decorator = new PollyTransformDecorator<int, int>(inner, Empty<int>(), PollyInnerTransformOwnership.Owned);

        await Assert.ThrowsAsync<InvalidOperationException>(() => decorator.InitializeAsync(TestToken).AsTask());
        Assert.Equal(0, inner.DisposeCalls);
        await decorator.DisposeAsync();

        Assert.Equal(1, inner.DisposeCalls);
    }

    private static RecordingTransformer<int, int> Succeeding() => new(SuccessBehavior);

    private static ValueTask<StageResult<int>> SuccessBehavior(ProcessingEnvelope<int> envelope, int attempt, CancellationToken ct) =>
        ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));
}
